using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private bool FortuneFateSupply;
    private readonly HashSet<ulong> _seenSupplyChests = [];
    private readonly HashSet<ulong> _lateSupplyChests = [];
    private readonly HashSet<ulong> _finishedSupplyChests = [];
    private bool _supplyBaselineTaken;
    private readonly Dictionary<ulong, bool> _supplyEligibility = [];
    private readonly HashSet<uint> _stoneEaters = [];
    private DateTime _nextSupplyScan, _nextCharm, _nextCharmScan;
    private bool _extraChestWork;
    // This controller is one floor generation. Objective/task replacement must not reset these budgets.
    private readonly OptionalChestBudget _optionalChestBudget = new();
    private readonly Dictionary<ulong, Vector3> _optionalChestPositions = [];
    private readonly HashSet<ulong> _reportedAbandonedChests = [];
    private int[]? _lastOptionalInventory;
    private DateTime _nextOptionalChestTick;

    private bool IsFinishedOptionalChest(RoomWaypoint waypoint) =>
        waypoint.Type is RoomObjectiveType.ChestGold or RoomObjectiveType.ChestSilver &&
        _optionalChestPositions.Any(pair => _finishedSupplyChests.Contains(pair.Key) &&
            Vector2.DistanceSquared(Xz(pair.Value), Xz(waypoint.Position)) < 1);

    private void RecordOptionalChestRecoveryFailure(RoomWaypoint waypoint)
    {
        foreach (var (id, position) in _optionalChestPositions)
            if (Vector2.DistanceSquared(Xz(position), Xz(waypoint.Position)) < 1)
                _optionalChestBudget.FailedRecovery(id);
    }

    private unsafe void UpdateOptionalChestWatchdog(InstanceContentDeepDungeon* dd)
    {
        if (Kind != FloorRuntimeKind.Normal || _ctx.FarmingPlan?.IsProgression != true ||
            _ctx.RunOptions.Current.HarvestChestsRequired || Service.LocalPlayer is not { } player ||
            ObjectEvidence.Current is not { Available: true } evidence) return;
        var now = DateTime.UtcNow;
        var selected = _activeWaypoint ?? _executor?.CurrentWaypoint;
        ulong selectedId = 0;
        if (selected is { Type: RoomObjectiveType.ChestGold or RoomObjectiveType.ChestSilver } w)
            foreach (var chest in evidence.Chests)
                if (chest.Kind is FloorChestKind.Gold or FloorChestKind.Silver &&
                    Vector2.DistanceSquared(Xz(chest.Object.Position), Xz(w.Position)) < 1)
                {
                    selectedId = chest.Object.GameObjectId;
                    _optionalChestPositions.TryAdd(selectedId, chest.Object.Position);
                    _optionalChestBudget.Select(selectedId, Vector2.Distance(Xz(player.Position), Xz(chest.Object.Position)), now);
                    break;
                }
        if (_optionalChestPositions.Count == 0 || now < _nextOptionalChestTick) return;
        _nextOptionalChestTick = now.AddMilliseconds(250);
        bool paused = Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.WatchingCutscene] ||
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInCutSceneEvent] ||
            MobMechanicsYielding || _ctx.Duty.IsTransitioning || _hohPetrificationHolding;
        var inventory = Enumerable.Range(0, 16).Select(i => (int)_pomanderManager.GetCount((uint)i)).ToArray();
        bool inventoryDecreased = _lastOptionalInventory != null &&
            inventory.Where((count, i) => count < _lastOptionalInventory[i]).Any();
        _lastOptionalInventory = inventory;
        foreach (var (id, position) in _optionalChestPositions.ToArray())
        {
            if (_finishedSupplyChests.Contains(id)) continue;
            _optionalChestBudget.Tick(id, now, paused,
                Vector2.Distance(Xz(player.Position), Xz(position)),
                confirmedProgress: id == selectedId && inventoryDecreased);
            if (_optionalChestBudget.Reason(id) is not { } reason) continue;
            _finishedSupplyChests.Add(id);
            if (_reportedAbandonedChests.Add(id))
                RecordReplayEvent("optional-chest-abandoned", new { floor = Floor, generation = Generation,
                    entityId = id, reason, passageOpen = _ctx.Duty.PassageOpen,
                    budget = _optionalChestBudget.Diagnostics(id), selectedId, paused, inventory,
                    native = evidence.Chests.Where(c => c.Object.GameObjectId == id).Select(c => new {
                        c.NativeStateAvailable, completion = c.NativeCompletionKind.ToString(),
                        state = c.State.ToString(), flags = c.Flags.ToString() }).ToArray(),
                    distance = Vector2.Distance(Xz(player.Position), Xz(position)),
                    nextAction = _ctx.Duty.PassageOpen ? "finish-remaining-required-work-then-passage" : "resume-exploration-or-clearing" });
            if (_activeWaypoint is { } active &&
                active.Type is RoomObjectiveType.ChestGold or RoomObjectiveType.ChestSilver &&
                Vector2.DistanceSquared(Xz(position), Xz(active.Position)) < 1)
            {
                var phase = _taskRunner?.Phase ?? TaskPhase.Idle;
                double elapsed = _taskRunner?.ElapsedSeconds ?? 0;
                _taskRunner?.Reset();
                HandleTaskSkip(dd, player, "OptionalChestBudget:" + reason, WaypointOutcomeKind.PolicySkipped, elapsed, phase);
            }
            // Filters and extra-room queues are rebuilt in this same arbitration tick.
            _nextSupplyScan = DateTime.MinValue;
        }
    }

    private unsafe bool HasSupplyTime(InstanceContentDeepDungeon* dd) => dd != null &&
        _ctx.FarmingPlan?.IsProgression == true &&
        SupplyRoutePolicy.HasTime(dd->ContentTimeLeft, SupplyRoutePolicy.RemainingNormalFloorsInSet(dd->Floor));

    private unsafe bool PreservePassageSupplies(InstanceContentDeepDungeon* dd) =>
        FarmingPassageItemConfirmed && HasSupplyTime(dd);

    private unsafe void UpdateSupplyCandidates(InstanceContentDeepDungeon* dd)
    {
        if (_ctx.FarmingPlan?.IsProgression != true || Kind != FloorRuntimeKind.Normal ||
            ObjectEvidence.Current is not { Available: true } evidence || Service.LocalPlayer is not { } player ||
            _executor == null) return;
        if (HohWorkEnabled)
            foreach (var chest in evidence.Chests)
            {
                int room = FindChestRoom(dd, chest.Object.Position);
                if (!ObserveHohNativeChest(dd, chest, room) || !PendingHohChest(chest) ||
                    !HohChestEnabled(chest.Kind) || _finishedSupplyChests.Contains(chest.Object.GameObjectId)) continue;
                if (_executor.RoomContext is { } context && context.RoomIndex == room)
                    context.AppendLiveChest(chest.Object.Position, chest.Kind switch {
                        FloorChestKind.Gold => RoomObjectiveType.ChestGold,
                        FloorChestKind.Silver => RoomObjectiveType.ChestSilver, _ => RoomObjectiveType.ChestBronze });
            }
        // Capture before the entry incense can kill enemies. Waiting until the
        // exploration plan exists would misclassify those drops as initial chests.
        if (!_supplyBaselineTaken)
        {
            _seenSupplyChests.UnionWith(evidence.Chests.Select(c => c.Object.GameObjectId));
            _supplyBaselineTaken = true;
        }
        if (!PlanningState.SetupPlanGenerated || DateTime.UtcNow < _nextSupplyScan) return;
        _nextSupplyScan = DateTime.UtcNow.AddMilliseconds(500);
        int playerRoom = _ctx.ResolveHohCombatRoom(dd, NormalGraph?.ReachableRooms);
        if (playerRoom < 0) return;
        bool canSupply = HasSupplyTime(dd), canReturn = canSupply && FortuneFateSupply;
        Vector3? forward = _activeWaypoint?.Position;
        // A queued drop must not become its own definition of "forward".
        if (forward.HasValue && evidence.Chests.Any(c => _lateSupplyChests.Contains(c.Object.GameObjectId) &&
                Vector2.DistanceSquared(Xz(c.Object.Position), Xz(forward.Value)) < 1)) forward = null;
        int? nextRoom = _executor.PlannedRoute.Where(entry => entry.RoomIndex != playerRoom)
            .Select(entry => (int?)entry.RoomIndex).FirstOrDefault();
        if (!forward.HasValue && nextRoom is { } targetRoom &&
            MapPos.TryGetRoomCenter(dd, targetRoom, out var targetCenter)) forward = targetCenter;
        forward ??= evidence.PassageActors.Count > 0 ? evidence.PassageActors[0].Position : null;
        var extraRooms = new HashSet<int>();
        bool pendingCommitment = _optionalChestPositions.Keys.Any(id => !_finishedSupplyChests.Contains(id) &&
            _optionalChestBudget.Reason(id) == null);
        foreach (var chest in evidence.Chests)
        {
            if (chest.Kind is not (FloorChestKind.Gold or FloorChestKind.Silver) ||
                !chest.Object.IsTargetable || _finishedSupplyChests.Contains(chest.Object.GameObjectId) ||
                HohWorkEnabled && (!HohChestEnabled(chest.Kind) || !PendingHohChest(chest))) continue;
            int room = FindChestRoom(dd, chest.Object.Position);
            if (room < 0) continue;
            ulong id = chest.Object.GameObjectId;
            bool nativeCandidate = HohWorkEnabled && _hohNativeChestObjects.ContainsKey(id);
            // HoH native room/type evidence outranks first-observed time. PT retains its old policy.
            if (_seenSupplyChests.Add(id) && _supplyBaselineTaken && !nativeCandidate) _lateSupplyChests.Add(id);
            bool onWay = room == playerRoom && forward.HasValue && SupplyRoutePolicy.OnWay(
                Xz(player.Position), Xz(chest.Object.Position), Xz(forward.Value));
            bool committed = _optionalChestBudget.Contains(id) && _optionalChestBudget.Reason(id) == null;
            bool eligible = committed || !_lateSupplyChests.Contains(id) || canReturn || onWay;
            if (!_supplyEligibility.TryGetValue(id, out bool previous) || previous != eligible)
                RecordReplayEvent("supply-chest-policy", new { entityId = chest.Object.EntityId, room,
                    lateSpawn = _lateSupplyChests.Contains(id), eligible, onWay, canReturn,
                    secondsLeft = dd->ContentTimeLeft, x = chest.Object.Position.X, z = chest.Object.Position.Z });
            _supplyEligibility[id] = eligible;
            if (!eligible) continue;
            if (!IsOnPassageRoute(dd, chest.Object.Position)) continue;
            // Extra visits are only for drops in searched rooms, on the current path,
            // or after the Fortune/Fate combination. Initial chests keep normal plans.
            if (canSupply && (committed || canReturn || onWay || nativeCandidate) &&
                (_lateSupplyChests.Contains(id) || _executor.IsChestRoomSearched(room)))
            {
                // Queueing this live optional box is already a selection. The budget must
                // also cover pre-waypoint oscillation, before an interaction attempt exists.
                if (!pendingCommitment && !_ctx.RunOptions.Current.HarvestChestsRequired)
                {
                    _optionalChestPositions.TryAdd(id, chest.Object.Position);
                    _optionalChestBudget.Select(id, Vector2.Distance(Xz(player.Position), Xz(chest.Object.Position)), DateTime.UtcNow);
                    pendingCommitment = true;
                }
                extraRooms.Add(room);
                if (_executor.RoomContext is { } context && context.RoomIndex == room)
                    context.AppendLiveChest(chest.Object.Position, chest.Kind == FloorChestKind.Gold
                        ? RoomObjectiveType.ChestGold : RoomObjectiveType.ChestSilver);
            }
        }
        _supplyBaselineTaken = true;
        _executor.LiveChestFilter = chest => !_finishedSupplyChests.Contains(chest.Object.GameObjectId) &&
            (!_lateSupplyChests.Contains(chest.Object.GameObjectId) ||
             _supplyEligibility.GetValueOrDefault(chest.Object.GameObjectId));
        _executor.SetExtraChestRooms(extraRooms);
        _extraChestWork = extraRooms.Count > 0;
        // Rebuild only at an idle boundary; never interrupt a chest request/room task.
        if (_extraChestWork && _executor.RoomContext == null && _activeWaypoint == null &&
            _executor.PlannedRouteCount == 0 && NormalGraph is { } graph)
            _executor.GeneratePlan(dd, graph, _chatWatchers, player.Position, _nativeIntuitionActive);
        RefreshHohActualChestWork(dd);
    }

    private unsafe int FindChestRoom(InstanceContentDeepDungeon* dd, Vector3 position)
    {
        int room = -1; float best = float.MaxValue;
        if (NormalGraph == null) return room;
        foreach (int candidate in NormalGraph.ReachableRooms)
            if (MapPos.TryGetRoomCenter(dd, candidate, out var center))
            {
                float distance = Vector2.DistanceSquared(Xz(position), Xz(center));
                if (distance < best) { best = distance; room = candidate; }
            }
        return room;
    }

    private void FinishSupplyChest(RoomWaypoint waypoint)
    {
        if (_ctx.FarmingPlan?.IsProgression != true ||
            ObjectEvidence.Current is not { } evidence) return;
        foreach (var chest in evidence.Chests)
            if (Vector2.DistanceSquared(Xz(waypoint.Position), Xz(chest.Object.Position)) < 1 &&
                (chest.Kind is FloorChestKind.Gold or FloorChestKind.Silver || HohWorkEnabled && chest.Kind == FloorChestKind.Bronze))
                _finishedSupplyChests.Add(chest.Object.GameObjectId);
    }

    private unsafe bool SkipBacktrackingDrop(InstanceContentDeepDungeon* dd, RoomWaypoint waypoint)
    {
        if (FortuneFateSupply && HasSupplyTime(dd) || ObjectEvidence.Current is not { } evidence) return false;
        foreach (var chest in evidence.Chests)
            if (!_optionalChestBudget.Contains(chest.Object.GameObjectId) &&
                _lateSupplyChests.Contains(chest.Object.GameObjectId) &&
                Vector2.DistanceSquared(Xz(waypoint.Position), Xz(chest.Object.Position)) < 1 &&
                !_supplyEligibility.GetValueOrDefault(chest.Object.GameObjectId))
            {
                FinishSupplyChest(waypoint);
                return true;
            }
        return false;
    }

    private unsafe void TryUseCleansingCharm(InstanceContentDeepDungeon* dd)
    {
        if (_ctx.FarmingPlan?.IsProgression != true || dd->DeepDungeonId != 4 ||
            dd->Floor is < 71 or > 79 || Service.LocalPlayer is not { } player ||
            DateTime.UtcNow < _nextCharm || DateTime.UtcNow < _nextCharmScan) return;
        _nextCharmScan = DateTime.UtcNow.AddMilliseconds(250);
        bool threat = false;
        foreach (var obj in Service.GameObjects)
            if (obj is IBattleChara { NameId: 14194 } wolf)
            {
                _stoneEaters.Add(wolf.EntityId);
                threat |= !wolf.IsDead && wolf.TargetObjectId == player.GameObjectId &&
                    Vector2.DistanceSquared(Xz(wolf.Position), Xz(player.Position)) <= 25 * 25;
            }
        var sheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Status>();
        string? poisonName = sheet?.GetRow(18).Name.ExtractText();
        bool poison = false, shield = false;
        foreach (var status in player.StatusList)
        {
            shield |= status.StatusId == 4589; // Status.csv: 净化护符, blocks one ailment.
            if (_stoneEaters.Contains(status.SourceId) && sheet != null)
            {
                var data = sheet.GetRow(status.StatusId);
                poison |= data.CanDispel && !string.IsNullOrEmpty(poisonName) && data.Name.ExtractText() == poisonName;
            }
        }
        if ((!shield && threat || poison) && Items.TryUsePomander(FloorInitPlanner.CleansingCharmSlotIndex,
                dd, poison ? "stone eater poison cleanse" : "stone eater protection", FloorItemUsePurpose.CleansingCharm))
            _nextCharm = DateTime.UtcNow.AddSeconds(2);
    }

    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);
}
