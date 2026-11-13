using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private readonly PassageRouteScope _passageRouteScope = new();
    private bool _passageFirstPaused;
    private unsafe bool UpdatePassageRouteScope(InstanceContentDeepDungeon* dd)
    {
        if (!PassageFirst || !_ctx.PassageOpenForCombat || _executor == null) return true;
        // Initialize after old hate is gone: the combat position is the route's actual start.
        if (_ctx.MobCombat.Engaged.Count > 0 || Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
            return true;
        int room = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (!_passageRouteScope.Ready)
        {
            int passage = RoomGraph.GetPassageRoomIndex(dd);
            if (NormalGraph == null || !_passageRouteScope.Initialize(room, passage, NormalGraph.RoomDistances))
            {
                _navDriver.Cancel();
                _status = "91–98层：等待门房间和连通路线证据，暂不探索支路或传送";
                return false;
            }
            _executor.RouteFilter = _passageRouteScope.Filter;
            _executor.SearchRoomAllowed = _passageRouteScope.Allows;
            RecordReplayEvent("passage-exploration-scope", new { floor = Floor,
                rooms = _passageRouteScope.Rooms, rule = "current-room-to-passage-only-no-branches" });
        }
        _passageRouteScope.ObserveRoom(room);
        var old = _executor.SnapshotPlannedRoute();
        var scoped = _passageRouteScope.Filter(old);
        if (!old.SequenceEqual(scoped))
        {
            // An old waypoint from before Wisdom/opening may lie on a branch.
            // Abort it explicitly, retaining the forward route and existing chest budgets.
            if (_executor.RoomContext != null &&
                (scoped.Count == 0 || _executor.CurrentTargetRoomIndex != scoped[0].RoomIndex))
                if (!StopActiveSearchExecution(FloorObjectiveKind.OpenPlannedChest)) return false;
            _executor.ApplyRetainedRoute(scoped);
            RecordReplayEvent("passage-exploration-route-filtered", new { floor = Floor,
                before = old.Select(e => e.RoomIndex).ToArray(), after = scoped.Select(e => e.RoomIndex).ToArray(),
                reason = "off-route-work-excluded-by-policy" });
        }
        return true;
    }

    private unsafe bool IsOnPassageRoute(InstanceContentDeepDungeon* dd, System.Numerics.Vector3 position) =>
        !PassageFirst || !_ctx.PassageOpenForCombat || _passageRouteScope.Ready && NormalGraph != null &&
        _passageRouteScope.Allows(RoomGraph.GetRoomIndexForPosition(dd, position, NormalGraph.ReachableRooms, -1));
}
