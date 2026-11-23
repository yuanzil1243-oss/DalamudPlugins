using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private bool HohWorkEnabled => DungeonId == 2 && Kind == FloorRuntimeKind.Normal &&
        _ctx.FarmingPlan?.Mode == FarmingMode.FreeExploration;
    private bool _hohActualChestWork, _hohChestCombatPaused;
    private bool _hohLocalChestsBeforeAdvance;
    private readonly Dictionary<ulong, (int Room, FloorChestKind Kind, bool Ready)> _hohNativeChestObjects = [];
    private readonly Dictionary<int, DateTime> _hohMissingChestSince = [];

    private static bool PendingHohChest(FloorChestEvidence chest) => chest.Object.IsTargetable &&
        (!chest.NativeStateAvailable || chest.NativeCompletionKind != NativeTreasureCompletionKind.TreasureState ||
            (byte)chest.State == 0 && ((byte)chest.Flags & 1) == 0);

    private bool HohChestEnabled(FloorChestKind kind) => kind switch
    {
        FloorChestKind.Gold => _ctx.RunOptions.Current.OpenGold,
        FloorChestKind.Silver => _ctx.RunOptions.Current.OpenSilver,
        FloorChestKind.Bronze => _ctx.RunOptions.Current.OpenBronze,
        _ => false,
    };

    // The native table exposes room/type, not entity identity or spawn cause.
    // A matching late entity is therefore a native/ambiguous candidate, never a proven drop.
    private static unsafe int NativeHohChestCount(InstanceContentDeepDungeon* dd, int room, FloorChestKind kind)
    {
        if (dd == null || room < 0) return 0;
        byte type = kind switch { FloorChestKind.Bronze => 1, FloorChestKind.Silver => 2, FloorChestKind.Gold => 3, _ => 0 };
        if (type == 0) return 0;
        int count = 0;
        foreach (var chest in dd->Chests)
            if (chest.RoomIndex == room && chest.ChestType == type) count++;
        return count;
    }

    private unsafe bool ObserveHohNativeChest(InstanceContentDeepDungeon* dd, FloorChestEvidence chest, int room)
    {
        if (!HohWorkEnabled || NativeHohChestCount(dd, room, chest.Kind) == 0) return false;
        bool ready = PendingHohChest(chest) || _finishedSupplyChests.Contains(chest.Object.GameObjectId) ||
            chest.NativeStateAvailable && chest.NativeCompletionKind == NativeTreasureCompletionKind.TreasureState &&
                ((byte)chest.State != 0 || ((byte)chest.Flags & 1) != 0);
        _hohNativeChestObjects[chest.Object.GameObjectId] = (room, chest.Kind, ready);
        _lateSupplyChests.Remove(chest.Object.GameObjectId);
        return true;
    }

    private unsafe bool HohRoomHasChestWork(InstanceContentDeepDungeon* dd, int room)
    {
        if (_executor == null) return false;
        if (ObjectEvidence.Current is { Available: true } evidence && evidence.Chests.Any(c =>
                HohChestEnabled(c.Kind) && PendingHohChest(c) && !_finishedSupplyChests.Contains(c.Object.GameObjectId) &&
                FindChestRoom(dd, c.Object.Position) == room &&
                (!_lateSupplyChests.Contains(c.Object.GameObjectId) || _supplyEligibility.GetValueOrDefault(c.Object.GameObjectId))))
            return true;
        return !_executor.IsChestRoomSearched(room) &&
            DeepDungeonChestData.RoomHasEnabledChest(dd, room, _ctx.RunOptions.Current);
    }

    private unsafe void RefreshHohActualChestWork(InstanceContentDeepDungeon* dd)
    {
        if (!HohWorkEnabled || _executor == null) return;
        var old = _executor.SnapshotPlannedRoute();
        int currentRoom = _ctx.ResolveHohCombatRoom(dd, NormalGraph?.ReachableRooms);
        bool localPull = HohHighFloorPolicy.Enabled(_ctx.FarmingPlan?.Mode, DungeonId, Floor) &&
            Service.GameObjects.OfType<IBattleChara>().Any(e => e.IsTargetable && !e.IsDead && e.CurrentHp > 0 &&
                e.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)e.SubKind ==
                    global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant &&
                _ctx.IsNewCombatTargetAllowed(e));
        bool preferLocal = HohRoomEntryPolicy.PreferLocalChests(
            HohHighFloorPolicy.Enabled(_ctx.FarmingPlan?.Mode, DungeonId, Floor), _ctx.PassageOpenForCombat,
            _ctx.HohCombatInProgress, _ctx.HohPetrification.Clearing, localPull,
            currentRoom >= 0 && HohRoomHasChestWork(dd, currentRoom));
        // Retain the shared PT room executor/ledger. Only its HoH work input is narrowed.
        var actual = old.Where(e => e.ShouldSearchChests && HohRoomHasChestWork(dd, e.RoomIndex))
            .Select(e => e with { ShouldProbeHoard = false, ShouldVisitForIntel = false })
            .ToArray();
        bool idle = _executor.RoomContext == null && _activeWaypoint == null;
        if (preferLocal && idle) actual = HohRoomEntryPolicy.PrioritizeLocalChests(actual, currentRoom);
        if (_executor.RoomContext == null && _activeWaypoint == null && !old.SequenceEqual(actual))
            _executor.ApplyRetainedRoute(actual);
        // An outstanding room task remains authoritative. Never turn a remote task
        // into local chest work just because the route was filtered on this frame.
        _hohLocalChestsBeforeAdvance = preferLocal &&
            (_executor.RoomContext?.RoomIndex == currentRoom || idle && _executor.CurrentTargetRoomIndex == currentRoom);
        _hohActualChestWork = actual.Length > 0 || _executor.RoomContext?.Waypoints
            .Skip(_executor.RoomContext.CurrentWaypointIndex).Any(w => IsChestWaypoint(w) && !IsFinishedOptionalChest(w)) == true ||
            _optionalChestPositions.Keys.Any(id => !_finishedSupplyChests.Contains(id) && _optionalChestBudget.Reason(id) == null);
    }

    private unsafe bool WaitForHohNativeChestEvidence(InstanceContentDeepDungeon* dd, int room, DateTime now)
    {
        if (!HohWorkEnabled || _executor?.CurrentPlanEntry?.ShouldSearchChests != true) return false;
        bool missing = new[] { FloorChestKind.Gold, FloorChestKind.Silver, FloorChestKind.Bronze }.Any(kind =>
            HohChestEnabled(kind) && NativeHohChestCount(dd, room, kind) > _hohNativeChestObjects.Values.Count(v => v.Room == room && v.Kind == kind && v.Ready));
        if (!missing) { _hohMissingChestSince.Remove(room); return false; }
        if (!_hohMissingChestSince.TryGetValue(room, out var since)) _hohMissingChestSince[room] = since = now;
        if (now - since >= TimeSpan.FromSeconds(3)) return false;
        ObjectEvidence.Invalidate();
        _nextSupplyScan = DateTime.MinValue;
        _status = $"天宫：等待区域 {room} 原生宝箱加载，保留未完成取箱任务";
        return true;
    }

    private RoomObjectiveOutcomeResult ProtectHohChestOutcome(int room, RoomObjectiveOutcomeResult outcome)
    {
        if (HohWorkEnabled && _hohMissingChestSince.ContainsKey(room) && outcome.Chests == ObjectiveOutcomeKind.Succeeded)
        {
            RecordReplayEvent("hoh-native-chest-unresolved", new { room, reason = "native-room-table-without-complete-entity-evidence",
                action = "bounded-rescan-then-explicit-optional-skip", confirmedOpened = false });
            return outcome with { Chests = ObjectiveOutcomeKind.Deferred };
        }
        return outcome;
    }

    internal void SuspendHohOptionalChestClock(DateTime now)
    {
        if (!HohWorkEnabled) return;
        // Resume is called even though the host deliberately did not Tick during the pause.
        foreach (ulong id in _optionalChestPositions.Keys) _optionalChestBudget.Tick(id, now, paused: true);
        _nextOptionalChestTick = default;
    }

    private unsafe (int Room, Vector3 Position) ResolveClearingSearchOrigin(InstanceContentDeepDungeon* dd,
        global::Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter player) =>
        (_ctx.ResolveHohCombatRoom(dd, NormalGraph?.ReachableRooms), player.Position);
}
