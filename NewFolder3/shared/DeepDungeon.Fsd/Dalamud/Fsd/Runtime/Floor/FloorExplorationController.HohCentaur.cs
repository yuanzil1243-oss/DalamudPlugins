using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Objects.Enums;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private readonly HashSet<int> _hohVisitedCombatRooms = [];
    private readonly HashSet<ulong> _hohCentaurRetreated = [];
    private readonly Queue<(int Room, Vector3 Position)> _hohCentaurRooms = new();
    private readonly CombatPositionNavigation _hohCentaurGeometry = new();
    private int _hohPreviousCombatRoom = -1, _hohCentaurFromRoom = -1, _hohCentaurToRoom = -1;
    private Vector3 _hohCentaurDestination;
    private DateTime _hohCentaurDeadline;
    private CancellationTokenSource? _hohCentaurCancel;
    private Task<List<Vector3>>? _hohCentaurPath;
    private List<Vector3>? _hohCentaurMovingPath;
    private readonly Action _stopHohCentaurNativePath = moveHelper.VNav.Path.Stop;
    private bool _hohCentaurMovementOwned;

    private static IBattleChara[] LiveHohRetreatEnemies() => Service.GameObjects.OfType<IBattleChara>()
        .Where(e => e.ObjectKind == ObjectKind.BattleNpc && (BattleNpcSubKind)e.SubKind == BattleNpcSubKind.Combatant &&
            e.IsTargetable && !e.IsDead && e.CurrentHp > 0).ToArray();

    private unsafe bool TryUpdateHohCentaurRetreat(InstanceContentDeepDungeon* dd, bool mechanics)
    {
        if (!Hoh70FloorPolicy.Enabled(_ctx.FarmingPlan?.Mode, DungeonId, Floor) || dd == null ||
            Service.LocalPlayer is not { IsDead: false } player || NormalGraph is not { } graph) return false;
        var enemies = LiveHohRetreatEnemies();
        var centaurs = enemies.Where(e => Hoh70FloorPolicy.IsCentaur(e.BaseId, e.NameId) &&
            _ctx.IsEngagedCombatTarget(e) && !_ctx.IsHohPetrificationClearTarget(e)).ToArray();
        if (centaurs.Length == 0)
        {
            if (_ctx.HohCentaurRetreatActive) FinishHohCentaurRetreat("centaur-no-longer-engaged");
            return false;
        }
        // Never start the same retreat again every frame while a ranged enemy remains alive.
        if (!_ctx.HohCentaurRetreatActive && centaurs.All(e => _hohCentaurRetreated.Contains(e.GameObjectId))) return false;
        if (mechanics)
        {
            if (_ctx.HohCentaurRetreatActive)
            {
                if (_hohCentaurPath != null || _hohCentaurMovingPath != null)
                    _hohCentaurRooms.Enqueue((_hohCentaurToRoom, _hohCentaurDestination));
                StopHohCentaurOwnedPath(); _navHelper.Cancel();
                _hohCentaurCancel?.Cancel(); _hohCentaurPath = null;
                ReleaseHohCentaurMovement();
            }
            return false;
        }
        var centaur = centaurs.OrderBy(e => e.CurrentHp).First();
        int room = _ctx.ResolveHohCombatRoom(dd, graph.ReachableRooms);
        if (room < 0 && _hohVisitedCombatRooms.Contains(LastObservedRoomIndex)) room = LastObservedRoomIndex;
        var now = DateTime.UtcNow;
        if (!_ctx.HohCentaurRetreatActive)
        {
            foreach (var e in centaurs) _hohCentaurRetreated.Add(e.GameObjectId);
            _ctx.HohCentaurRetreatActive = true; _hohCentaurFromRoom = room;
            _hohCentaurDeadline = now.AddSeconds(12);
            _navDriver.Cancel(); ActiveExecution?.Recovery.YieldMovement(); _heldRecovery?.YieldMovement();
            _hohPetrifiedContact.Reset(); _taskRunner?.SetSuspended(true);
            var rooms = graph.ReachableRooms.Where(r => r != room && r >= 0 && room >= 0 &&
                    room < graph.RoomDistances.GetLength(0) && r < graph.RoomDistances.GetLength(1))
                .Select(r => new HohRetreatRoom(r, _hohVisitedCombatRooms.Contains(r), graph.RoomDistances[room, r],
                    enemies.Count(e => RoomGraph.GetRoomIndexForPosition(dd, e.Position, graph.ReachableRooms, -1) == r),
                    MapPos.TryGetRoomCenter(dd, r, out var center) ? Vector3.DistanceSquared(player.Position, center) : float.PositiveInfinity));
            foreach (int candidate in HohCentaurRetreatPolicy.OrderRooms(rooms, _hohPreviousCombatRoom))
                if (CombatPositionRecoveryController.TryResolveRoomDestination(dd, candidate, out var destination))
                    _hohCentaurRooms.Enqueue((candidate, destination));
            RecordReplayEvent("hoh-centaur-retreat-started", new { floor = Floor, targetId = centaur.GameObjectId,
                fromRoom = room, candidates = _hohCentaurRooms.Select(r => r.Room).ToArray(), aeSuppressed = false });
        }
        _ctx.SetPreferredAggroTarget(centaur.GameObjectId, centaur.Position);
        _ctx.EnsureHohCombatTarget(centaur);
        _taskRunner?.SetSuspended(true);
        if (now >= _hohCentaurDeadline)
        { FinishHohCentaurRetreat("bounded-retreat-expired-keep-centaur-priority"); return false; }
        if (!_hohCentaurMovementOwned && _ctx.Configuration.BossMechanics.UsesBmrBossHandling())
        {
            if (_ctx.SetBossMovementOverride?.Invoke(true) != true)
            { FinishHohCentaurRetreat("movement-override-unavailable"); return false; }
            _hohCentaurMovementOwned = true;
        }
        try
        {
            if (!_hohCentaurGeometry.Ready)
            { FinishHohCentaurRetreat("navigation-unavailable"); return false; }
            var start = new Vector3(player.Position.X, centaur.Position.Y, player.Position.Z);
            var idle = enemies.Where(e => !_ctx.IsEngagedCombatTarget(e)).Select(e => e.Position).ToArray();
            if (_hohCentaurMovingPath is { } moving)
            {
                // Followers entering the retreat room are expected; a newly encountered idle mob is not.
                bool newEnemy = enemies.Any(e => !_ctx.IsEngagedCombatTarget(e) &&
                    RoomGraph.GetRoomIndexForPosition(dd, e.Position, graph.ReachableRooms, -1) == _hohCentaurToRoom);
                int nearest = Enumerable.Range(0, moving.Count).MinBy(i => Vector3.DistanceSquared(start, moving[i]));
                var remaining = moving.Skip(nearest).ToArray();
                bool safe = !newEnemy && HohCentaurRetreatPolicy.SafeRoute(remaining, start, idle,
                    _hohCentaurFromRoom, _hohCentaurToRoom,
                    p => RoomGraph.GetRoomIndexForPosition(dd, p, graph.ReachableRooms, -1));
                if (!safe)
                { StopHohCentaurOwnedPath(); _navHelper.Cancel(); return true; }
                if (Vector3.DistanceSquared(start, _hohCentaurDestination) <= 1.5f * 1.5f)
                { FinishHohCentaurRetreat("empty-room-arrived-fight-centaur-first"); return false; }
                _status = $"人马接战：退回空房 {_hohCentaurToRoom}，优先处理人马";
                return true;
            }
            if (_hohCentaurPath is { IsCompleted: true })
            {
                var path = _hohCentaurPath.GetAwaiter().GetResult(); _hohCentaurPath = null;
                bool empty = !enemies.Any(e => !_ctx.IsEngagedCombatTarget(e) &&
                    RoomGraph.GetRoomIndexForPosition(dd, e.Position, graph.ReachableRooms, -1) == _hohCentaurToRoom);
                bool endpoint = CombatPositionPolicy.ValidatePathEndpoint(start, _hohCentaurDestination, path,
                    out float length, out string reason, HohCentaurRetreatPolicy.MaximumPathLength);
                bool safeRoute = endpoint && HohCentaurRetreatPolicy.TrySafeRoute(path, start, idle,
                    _hohCentaurFromRoom, _hohCentaurToRoom,
                    p => RoomGraph.GetRoomIndexForPosition(dd, p, graph.ReachableRooms, -1), out reason);
                if (empty && safeRoute)
                {
                    // Track ownership before a native call that could partially dispatch and throw.
                    _hohCentaurMovingPath = path; _hohCentaurGeometry.Move(path);
                    RecordReplayEvent("hoh-centaur-retreat-path-accepted", new { floor = Floor,
                        targetId = centaur.GameObjectId, fromRoom = _hohCentaurFromRoom, toRoom = _hohCentaurToRoom,
                        length, reason, path = path.Select(p => new { p.X, p.Y, p.Z }).ToArray() });
                    return true;
                }
                RecordReplayEvent("hoh-centaur-retreat-path-rejected", new { floor = Floor,
                    targetId = centaur.GameObjectId, fromRoom = _hohCentaurFromRoom, toRoom = _hohCentaurToRoom,
                    empty, length, reason = empty ? reason : "destination-room-has-idle-enemy",
                    maximumLength = HohCentaurRetreatPolicy.MaximumPathLength,
                    start = new { start.X, start.Y, start.Z },
                    destination = new { _hohCentaurDestination.X, _hohCentaurDestination.Y, _hohCentaurDestination.Z },
                    path = path?.Select(p => new { p.X, p.Y, p.Z }).ToArray() });
            }
            if (_hohCentaurPath == null)
            {
                if (_hohCentaurRooms.Count == 0)
                { FinishHohCentaurRetreat("no-confirmed-empty-safe-adjacent-room"); return false; }
                var next = _hohCentaurRooms.Dequeue(); _hohCentaurToRoom = next.Room;
                _hohCentaurDestination = next.Position;
                _hohCentaurCancel?.Dispose(); _hohCentaurCancel = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Token);
                _hohCentaurCancel.CancelAfter(_hohCentaurDeadline > now ? _hohCentaurDeadline - now : TimeSpan.FromMilliseconds(1));
                _hohCentaurPath = _hohCentaurGeometry.FindPath(start, next.Position, _hohCentaurCancel.Token);
                _hohCentaurPath.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
            _status = "人马接战：检查回退空房的完整路线，停止补拉";
        }
        catch (Exception error)
        { FinishHohCentaurRetreat("retreat-unavailable:" + error.Message); return false; }
        return true;
    }

    private void ReleaseHohCentaurMovement()
    {
        if (!_hohCentaurMovementOwned) return;
        _hohCentaurMovementOwned = false; _ctx.SetBossMovementOverride?.Invoke(false);
    }
    private void StopHohCentaurOwnedPath()
    {
        if (_hohCentaurMovingPath == null) return;
        _hohCentaurMovingPath = null;
        // Geometry.Move bypasses NavigatorVNavAdapter's _cancelPending flag.
        // Its owner must stop the native path directly, before yielding to BMR.
        try { _stopHohCentaurNativePath(); }
        catch (Exception error)
        { RecordReplayEvent("hoh-centaur-retreat-path-stop-failed", new { floor = Floor, error = error.Message }); }
    }
    private void DisposeHohCentaurRetreat()
    {
        StopHohCentaurOwnedPath();
        if (_ctx.HohCentaurRetreatActive) _navHelper.Cancel();
        _ctx.HohCentaurRetreatActive = false; _hohCentaurMovingPath = null; _hohCentaurPath = null;
        _hohCentaurCancel?.Cancel(); _hohCentaurCancel?.Dispose(); _hohCentaurCancel = null;
        _hohCentaurRooms.Clear(); ReleaseHohCentaurMovement();
    }
    private void FinishHohCentaurRetreat(string reason)
    {
        DisposeHohCentaurRetreat(); _taskRunner?.SetSuspended(false);
        RecordReplayEvent("hoh-centaur-retreat-ended", new { floor = Floor, reason, toRoom = _hohCentaurToRoom,
            centaurPriorityRetained = true, aeSuppressed = false });
    }
}
