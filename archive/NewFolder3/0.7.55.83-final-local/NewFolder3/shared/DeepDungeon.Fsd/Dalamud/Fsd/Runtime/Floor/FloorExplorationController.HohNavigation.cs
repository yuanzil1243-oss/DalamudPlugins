using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using global::Dalamud.Game.ClientState.Objects.SubKinds;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private readonly HohPassageCandidates _hohPassages = new();
    private readonly HashSet<int> _hohPassageProbes = [];
    private readonly HashSet<int> _hohRegeneratedRooms = [];
    private DateTime? _hohProbeArrivedAt;
    private int? _hohWorldArrivedRoom;
    private readonly HohRoomProgressWatch _hohRoomProgress = new();

    private bool WithinHohWorldSearchArea(IPlayerCharacter player) => _executor?.RoomContext is { } room &&
        HohRoomArrivalPolicy.CanContinueSearch(DungeonId, _hohWorldArrivedRoom, room.RoomIndex,
            room.Contains(player.Position), player.Position.Y - room.RoomCenter.Y);

    private unsafe bool TryResolveHohPassageActor(InstanceContentDeepDungeon* dd,
        FloorObjectEvidenceSnapshot evidence, Vector3 player, out Vector3 destination)
    {
        destination = default;
        int passageRoom = RoomGraph.GetPassageRoomIndex(dd);
        var reachable = NormalGraph?.ReachableRooms ?? RoomGraph.BuildReachableRoomOrder(dd, RoomGraph.GetLocalPlayerRoomIndex(dd));
        var candidates = evidence.PassageActors.Select(a => new HohPassageCandidate(a.GameObjectId, a.Position,
            passageRoom >= 0 && RoomGraph.GetRoomIndexForPosition(dd, a.Position, reachable, -1) == passageRoom)).ToArray();
        bool found = _hohPassages.TrySelect(candidates, player, DateTime.UtcNow, out var selected, out bool changed, out bool expired,
            _ctx.PassageOpenForCombat && !_ctx.HohCombatInProgress && !_ctx.Duty.IsTransitioning);
        if (changed || expired)
        {
            ReleasePassageStandstill(); _navDriver.Cancel();
            _hohProbeArrivedAt = null;
            Service.Log.Info($"[HOH passage] Candidate changed: selected={(found ? selected.Position : null)}, activationTimedOut={expired}, rejected={_hohPassages.RejectedCount}");
            RecordReplayEvent("hoh-passage-candidate-changed", new { found, selected, expired, rejected = _hohPassages.RejectedCount });
        }
        if (found) destination = selected.Position;
        return found;
    }

    private unsafe void NavigateToHohPassage(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (ObjectEvidence.Current is not { Available: true } evidence) return;
        bool exact = TryResolveHohPassageActor(dd, evidence, player.Position, out var destination);
        int probeRoom = -1;
        if (!exact)
        {
            int nativePassage = RoomGraph.GetPassageRoomIndex(dd);
            var rooms = NormalGraph?.ReachableRooms ?? RoomGraph.BuildReachableRoomOrder(dd, RoomGraph.GetLocalPlayerRoomIndex(dd));
            var probes = new List<(int Room, Vector3 Point)>();
            foreach (int room in rooms)
                if (!_hohPassageProbes.Contains(room) && MapPos.TryGetRoomCenter(dd, room, out var center)) probes.Add((room, center));
            if (probes.Count == 0)
            {
                _navDriver.Cancel(); _passageUnknownAt ??= DateTime.UtcNow;
                _status = "天宫：等待新的传送石证据，已排除无响应的候选";
                if ((DateTime.UtcNow - _passageUnknownAt.Value).TotalSeconds >= 30)
                    StopHohNavigation("天宫：本层传送石候选及探索点已用尽，请检查后手动继续探索。");
                return;
            }
            var probe = probes.OrderByDescending(p => p.Room == nativePassage)
                .ThenBy(p => Vector3.DistanceSquared(p.Point, player.Position)).First();
            probeRoom = probe.Room; destination = probe.Point;
        }
        _passageUnknownAt = null;
        var walking = exact ? ResolvePassageWalkingPosition(destination) : destination;
        var result = _navDriver.Drive(walking, player.Position, exact ? .5f : 1.2f,
            dd, RoomGraph.GetLocalPlayerRoomIndex(dd), exact ? null : probeRoom);
        if (result == NavDriveResult.Failed)
        {
            if (exact)
            {
                _hohPassages.RejectCurrent(); ReleasePassageStandstill();
                RecordReplayEvent("hoh-passage-unreachable-rejected", new { destination });
            }
            else _hohPassageProbes.Add(probeRoom);
            _navDriver.Cancel(); _hohProbeArrivedAt = null;
            _status = "天宫：候选路径不可达，检查其他传送石或本层探索点";
            return;
        }
        if (result == NavDriveResult.Arrived && !exact)
        {
            // Let a fresh object scan discover landmarks before advancing the probe.
            _hohProbeArrivedAt ??= DateTime.UtcNow;
            if ((DateTime.UtcNow - _hohProbeArrivedAt.Value).TotalSeconds >= 1)
            { _hohPassageProbes.Add(probeRoom); _hohProbeArrivedAt = null; }
        }
        else if (result != NavDriveResult.Arrived) _hohProbeArrivedAt = null;
        _status = exact ? result == NavDriveResult.Arrived ? "天宫：已到传送石，限时等待实际传送" : "天宫：沿完整路径前往传送石候选"
            : $"天宫：继续探索传送石位置（区域 {probeRoom}）";
    }

    private void StopHohNavigation(string reason)
    {
        _navDriver.Cancel(); ReleasePassageStandstill();
        _ctx.StopExplorationRecoverably(reason); _status = reason;
        RecordReplayEvent("hoh-navigation-recoverable-stop", new { floor = Floor, reason });
    }

    private unsafe void HandleHohRoomFailure(InstanceContentDeepDungeon* dd, int room)
    {
        if (_hohRegeneratedRooms.Add(room))
        {
            MapPosGeneration.EnsureCentersAvailable(dd, forceRegenerate: true);
            _navDriver.RecheckHohPathAfterMapRefresh();
            RecordReplayEvent("hoh-room-centers-rechecked", new { floor = Floor, room });
            return;
        }
        var entry = _executor?.CurrentPlanEntry;
        if (HohWorkEnabled && entry is { ShouldSearchChests: true, ShouldProbeHoard: false, ShouldVisitForIntel: false } optional &&
            optional.RoomIndex == room && !_ctx.RunOptions.Current.HarvestChestsRequired && Service.LocalPlayer is { } player)
        {
            if (!BeginRoomObjectiveExecutions(room, entry)) { _executor!.DiscardCurrentPlanEntry(); return; }
            if (ObjectEvidence.Current is { } evidence)
                foreach (var chest in evidence.Chests)
                    if (FindChestRoom(dd, chest.Object.Position) == room && HohChestEnabled(chest.Kind))
                        _finishedSupplyChests.Add(chest.Object.GameObjectId);
            RecordReplayEvent("hoh-optional-room-unreachable", new { room, action = "skip-optional-work-not-floor", confirmedOpened = false });
            FinalizeRoomSearch(dd, player, room, useRoomFinishPomander: false, allowBandedRevealExpectation: false,
                explicitOutcome: BuildTerminalOutcomeSnapshot(entry, ObjectiveOutcomeKind.Failed), finalizeReason: "HohOptionalRoomUnreachable");
            RefreshHohActualChestWork(dd);
            return;
        }
        StopHohNavigation($"天宫：区域 {room} 完整路径校验失败；已停止重复走向边缘，可手动复位后继续探索。");
    }

    private void ResetHohNavigationAfterManualContinue()
    {
        if (DungeonId != 2) return;
        _navDriver.ResetHohBudget(); _hohPassages.Clear(); _hohPassageProbes.Clear();
        _hohRegeneratedRooms.Clear(); _hohProbeArrivedAt = null; _passageUnknownAt = null;
        _hohWorldArrivedRoom = null; _hohRoomProgress.Reset();
    }
}
