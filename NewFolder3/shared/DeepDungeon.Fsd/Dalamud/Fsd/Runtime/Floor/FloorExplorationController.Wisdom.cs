using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private readonly CombatPositionNavigation _wisdomGeometry = new();
    private CancellationTokenSource? _wisdomCancel;
    private Task<List<Vector3>>? _wisdomPath;
    private readonly Queue<Vector3> _wisdomCandidates = new();
    private bool _wisdomPlanned, _wisdomMoving, _wisdomMovementOwned;
    private Vector3 _wisdomDestination;
    private DateTime _wisdomArrivedAt;

    private void ConfirmWisdom()
    {
        _ctx.WisdomCombat.Sync(DungeonId, Floor);
        if (_ctx.WisdomCombat.Confirmed) return;
        _ctx.WisdomCombat.Confirm(DateTime.UtcNow);
        ReleasePassageStandstill();
        _navDriver.Cancel();
        _chaseHelper.Reset();
        _ctx.ClearPreferredAggroTarget();
        _taskRunner?.SetSuspended(true);
        ActiveExecution?.Recovery.YieldMovement();
        _heldRecovery?.YieldMovement();
        _ctx.EnforceWisdomTargetGate();
        RecordReplayEvent("wisdom-combat-gate-confirmed", new { floor = Floor,
            engaged = _ctx.MobCombat.Engaged.Count, nativePassageOpen = _ctx.Duty.PassageOpen,
            nextAction = "stop-output-short-local-retreat-then-clear-existing-hate" });
    }

    private unsafe bool UpdateWisdomRetreat(InstanceContentDeepDungeon* dd, bool mechanics)
    {
        var state = _ctx.WisdomCombat;
        if (!state.Retreating) return false;
        if (mechanics)
        {
            StopWisdomPath();
            ReleaseWisdomMovement();
            return false; // The normal mechanics owner remains above all combat movement.
        }
        if (_ctx.MobCombat.Engaged.Count == 0 || state.RetreatExpired(DateTime.UtcNow))
        {
            FinishWisdomRetreat(_ctx.MobCombat.Engaged.Count == 0 ? "no-existing-hate" : "bounded-wait-expired");
            return false;
        }
        if (Service.LocalPlayer is not { } player) return true;
        _ctx.EnforceWisdomTargetGate();
        _taskRunner?.SetSuspended(true);
        _status = "慧敏已开门：停手，短撤后清理旧仇恨";
        if (!_wisdomMovementOwned && _ctx.Configuration.BossMechanics.UsesBmrBossHandling())
        {
            if (_ctx.SetBossMovementOverride?.Invoke(true) != true)
            { FinishWisdomRetreat("movement-override-unavailable"); return false; }
            _wisdomMovementOwned = true;
        }
        try
        {
            if (!_wisdomGeometry.Ready)
            { FinishWisdomRetreat("navigation-unavailable"); return false; }
            float groundY = _ctx.MobCombat.Engaged[0].Position.Y;
            var start = new Vector3(player.Position.X, groundY, player.Position.Z);
            if (!_wisdomPlanned)
            {
                _wisdomPlanned = true;
                _wisdomCancel = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Token);
                _wisdomCancel.CancelAfter(TimeSpan.FromSeconds(8));
                int room = RoomGraph.GetLocalPlayerRoomIndex(dd);
                if (room >= 0 && MapPos.TryGetRoomCenter(dd, room, out var center))
                {
                    Span<int> neighbors = stackalloc int[4];
                    RoomGraph.EnumerateNeighbors(dd, room, neighbors, out int count);
                    var candidates = new List<Vector3>();
                    for (int i = 0; i < count; i++)
                        if (MapPos.TryGetRoomCenter(dd, neighbors[i], out var other))
                        {
                            var corridor = (center + other) / 2;
                            if (Vector2.Distance(new(start.X, start.Z), new(corridor.X, corridor.Z)) is > 2 and <= 20)
                                candidates.Add(corridor);
                        }
                    foreach (var p in candidates.OrderBy(p => Vector3.DistanceSquared(start, p)))
                        if (_wisdomGeometry.GroundPoint(p, .8f, 2) is { } grounded)
                            _wisdomCandidates.Enqueue(grounded);
                }
            }
            if (_wisdomMoving)
            {
                if (!PassageCombatPolicy.SafeLocalPath([start, _wisdomDestination], start,
                        _ctx.MobCombat.Unengaged.Select(e => e.Position)))
                { FinishWisdomRetreat("new-enemy-on-retreat"); return false; }
                if (Vector2.Distance(new(start.X, start.Z), new(_wisdomDestination.X, _wisdomDestination.Z)) < 1.2f)
                {
                    StopWisdomPath();
                    _wisdomArrivedAt = DateTime.UtcNow;
                }
                return true;
            }
            if (_wisdomArrivedAt != default)
            {
                // Fixed/ranged enemies need not follow: hand them to bounded local LOS recovery.
                if (_ctx.MobCombat.Engaged.Any(e => Vector2.Distance(new(start.X, start.Z), new(e.Position.X, e.Position.Z)) < 8) ||
                    DateTime.UtcNow - _wisdomArrivedAt >= TimeSpan.FromSeconds(2))
                { FinishWisdomRetreat("corridor-arrived-resume-old-hate"); return false; }
                return true;
            }
            if (_wisdomPath is { IsCompleted: true })
            {
                var path = _wisdomPath.GetAwaiter().GetResult(); _wisdomPath = null;
                if (CombatPositionPolicy.ValidatePathEndpoint(start, _wisdomDestination, path, out _, out _) &&
                    PassageCombatPolicy.SafeLocalPath(path, start, _ctx.MobCombat.Unengaged.Select(e => e.Position)))
                {
                    _wisdomGeometry.Move(path); _wisdomMoving = true;
                    RecordReplayEvent("wisdom-retreat-started", new { floor = Floor, start,
                        destination = _wisdomDestination, path, engaged = _ctx.MobCombat.Engaged.Count });
                    return true;
                }
            }
            if (_wisdomPath == null)
            {
                if (_wisdomCandidates.Count == 0)
                { FinishWisdomRetreat("no-safe-nearby-corridor"); return false; }
                _wisdomDestination = _wisdomCandidates.Dequeue();
                _wisdomPath = _wisdomGeometry.FindPath(start, _wisdomDestination, _wisdomCancel!.Token);
                _wisdomPath.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }
        catch (Exception error)
        {
            FinishWisdomRetreat("local-retreat-unavailable:" + error.Message);
            return false;
        }
        return true;
    }

    private void FinishWisdomRetreat(string reason)
    {
        DisposeWisdomRetreat();
        _ctx.WisdomCombat.FinishRetreat();
        _taskRunner?.SetSuspended(false);
        RecordReplayEvent("wisdom-retreat-ended", new { floor = Floor, reason,
            engaged = _ctx.MobCombat.Engaged.Count, newTargetsAllowed = false });
    }
    private void StopWisdomPath()
    {
        if (_wisdomMoving) _navHelper.Cancel();
        _wisdomMoving = false;
        _wisdomCancel?.Cancel();
        _wisdomPath = null;
    }
    private void ReleaseWisdomMovement()
    {
        if (!_wisdomMovementOwned) return;
        _wisdomMovementOwned = false;
        _ctx.SetBossMovementOverride?.Invoke(false);
    }
    private void DisposeWisdomRetreat()
    {
        StopWisdomPath();
        _wisdomCancel?.Dispose(); _wisdomCancel = null;
        ReleaseWisdomMovement();
    }
}
