using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using global::Dalamud.Game.ClientState.Objects.Enums;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private readonly HohRoomAdvanceState _hohRoomAdvance = new();
    private readonly ICombatPositionNavigation _hohEntryGeometry = new CombatPositionNavigation();
    private int _hohEntryFrom = -1, _hohEntryTo = -1;
    private Vector3 _hohEntryDestination;
    private readonly HohRoomProgressWatch _hohEntryProgress = new();
    private readonly List<Vector3> _hohRejectedEntrances = [];
    private static HohMimicCandidate[] VisibleHohMimics() => Service.GameObjects.OfType<IBattleChara>()
        .Where(e => e.ObjectKind == ObjectKind.BattleNpc && (BattleNpcSubKind)e.SubKind == BattleNpcSubKind.Combatant &&
            e.NameId == Hoh70FloorPolicy.MimicNameId)
        .Select(e => new HohMimicCandidate(e.GameObjectId, e.Position, !e.IsDead && e.CurrentHp > 0)).ToArray();

    private void ObserveHohMimicChest()
    {
        if (!HohWorkEnabled || ActiveChestAttempt is not { EntityId: not 0 } attempt ||
            _activeWaypoint?.Type != RoomObjectiveType.ChestGold || ObjectEvidence.Current is not { Available: true } evidence) return;
        var lifecycle = _ctx.ChestInteraction.Observe(attempt, evidence);
        bool missing = lifecycle.Evidence.Object.EntityId == 0;
        if (attempt.HohMimic.Observe(DateTime.UtcNow, missing, VisibleHohMimics()))
            RecordReplayEvent("hoh-chest-transformed-into-mimic", new {
                floor = Floor, chestId = attempt.EntityId, mimicId = attempt.HohMimic.MimicId,
                nameId = Hoh70FloorPolicy.MimicNameId, source = "dispatched-box-disappeared-new-single-mimic-within3m5s",
                lootConfirmed = false });
    }

    private unsafe bool TryFinishHohMissingChest(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (!HohWorkEnabled || HasPendingHohRelief || ActiveChestAttempt is not { EntityId: not 0 } attempt ||
            _activeWaypoint is not { } waypoint || !IsChestWaypoint(waypoint) || waypoint.Type == RoomObjectiveType.ChestBanded ||
            _ctx.HohCombatInProgress || ObjectEvidence.Current is not { Available: true } evidence) return false;
        var lifecycle = _ctx.ChestInteraction.Observe(attempt, evidence);
        if (lifecycle.Decision.Complete || lifecycle.Evidence.Object.EntityId != 0)
        { attempt.HohMissingSince = null; return false; }
        bool mimic = attempt.HohMimic.MimicId != 0;
        if (!mimic && _ctx.RunOptions.Current.HarvestChestsRequired) return false;
        attempt.HohMissingSince ??= DateTime.UtcNow;
        if (!mimic && DateTime.UtcNow - attempt.HohMissingSince.Value < TimeSpan.FromSeconds(1.5))
        { _navDriver.Cancel(); ObjectEvidence.Invalidate(); _status = "天宫：短暂复核消失的宝箱"; return true; }
        var phase = _taskRunner!.Phase; double elapsed = _taskRunner.ElapsedSeconds;
        _taskRunner.Reset();
        _finishedSupplyChests.Add(attempt.EntityId);
        RecordReplayEvent("hoh-missing-chest-terminal", new { floor = Floor, chestId = attempt.EntityId,
            mimicId = attempt.HohMimic.MimicId, lootConfirmed = false,
            reason = mimic ? "transformed-into-mimic" : "missing-after-interaction-unconfirmed" });
        HandleTaskSkip(dd, player, mimic ? "HohChestTransformedIntoMimic" : "HohChestMissingAfterInteraction",
            mimic ? WaypointOutcomeKind.PolicySkipped : WaypointOutcomeKind.Deferred, elapsed, phase);
        return true;
    }

    private unsafe bool TryUpdateHohAdjacentRoomPatrol(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        if (!Hoh70FloorPolicy.Enabled(_ctx.FarmingPlan?.Mode, DungeonId, Floor) || _ctx.PassageOpenForCombat ||
            _ctx.HohCombatInProgress || NormalGraph is not { } graph) return false;
        int room = _ctx.ResolveHohCombatRoom(dd, graph.ReachableRooms);
        var now = DateTime.UtcNow;
        if (_hohRoomAdvance.Enter(room, now))
        {
            _navDriver.Cancel(); _noClearingTargetAt = now;
            _hohEntryFrom = _hohEntryTo = -1;
            _hohRejectedEntrances.Clear();
            RecordReplayEvent("hoh91-room-entered", new { floor = Floor, room, action = "rescan-current-room-before-moving-on" });
        }
        int next = _hohRoomAdvance.Next(room, graph.ReachableRooms, graph.RoomDistances, now);
        if (next < 0)
        { _navDriver.Cancel(); _status = "天宫：检查当前房间／等待相邻房间路径"; return true; }
        if (_hohEntryFrom != room || _hohEntryTo != next)
        {
            if (!TryResolveHohEntranceStop(dd, player, room, next, out _hohEntryDestination))
            {
                _hohRoomAdvance.Block(room, next, now); _navDriver.Cancel();
                _status = "天宫：入口落点不可用，检查其他相邻房间"; return true;
            }
            _hohEntryFrom = room; _hohEntryTo = next;
            RecordReplayEvent("hoh91-room-entrance-planned", new { floor = Floor, fromRoom = room, toRoom = next,
                destination = new { _hohEntryDestination.X, _hohEntryDestination.Y, _hohEntryDestination.Z },
                roomCenterDestination = false, centaurTransitAllowed = true });
        }
        var destination = _hohEntryDestination;
        var result = _navDriver.Drive(destination, player.Position, 1.5f, dd, room, next);
        bool stalled = _hohEntryProgress.Stalled(next, player.Position, now);
        if (result == NavDriveResult.Failed || result == NavDriveResult.Arrived || stalled)
        {
            _hohRejectedEntrances.Add(destination); _navDriver.Cancel();
            _hohEntryFrom = _hohEntryTo = -1;
            RecordReplayEvent("hoh91-room-entrance-retry", new { floor = Floor, fromRoom = room, toRoom = next,
                result = result.ToString(), stalled,
                destination = new { destination.X, destination.Y, destination.Z },
                player = new { player.Position.X, player.Position.Y, player.Position.Z } });
        }
        _status = $"天宫：逐房推进 {room} → {next}，在入口侧检查目标，不先走房间中间";
        return true;
    }

    private unsafe bool TryResolveHohEntranceStop(InstanceContentDeepDungeon* dd, IPlayerCharacter player,
        int from, int next, out Vector3 destination)
    {
        destination = default;
        if (NormalGraph is not { } graph || !DeepDungeon.Fsd.Dalamud.Map.MapPos.TryGetRoomCenter(dd, from, out var fromCenter) ||
            !DeepDungeon.Fsd.Dalamud.Map.MapPos.TryGetRoomCenter(dd, next, out var nextCenter)) return false;
        var horses = _ctx.HohLiveCentaurObstacles();
        Vector3? transitOnly = null;
        foreach (var seed in HohRoomEntryPolicy.Seeds(fromCenter, nextCenter))
        {
            var ground = _hohEntryGeometry.GroundPoint(seed, .6f, 2);
            if (ground is not { } point || _hohRejectedEntrances.Any(p => Vector3.DistanceSquared(p, point) < 1) ||
                RoomGraph.GetRoomIndexForPosition(dd, point, graph.ReachableRooms, -1) != next) continue;
            transitOnly ??= point;
            if (!HohCentaurPullPolicy.ClearPosition(point, player.HitboxRadius, horses,
                    HohCentaurPullPolicy.PlayerClearance, out _)) continue;
            destination = point; return true;
        }
        // Passing a centaur is permitted; the common pull gate still forbids attacking from here.
        if (transitOnly is not { } fallback) return false;
        destination = fallback; return true;
    }
}
