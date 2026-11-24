using System.Numerics;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

/// <summary>HoH-only movement: never follow an unverified partial route to an island edge.</summary>
internal sealed class HohValidatedNavigation(
    Func<bool> ready, Func<Vector3, Vector3?> ground,
    Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>> findPath,
    Action<List<Vector3>> move, Action stop, Action<string> log,
    Func<DateTime>? clock = null, Func<bool>? simpleMovePending = null) : IDisposable
{
    private sealed class Budget(Vector3 goal, Vector3 anchor)
    { public Vector3 Goal = goal, Anchor = anchor; public int Failures, LocalFailures; public bool MapRechecked; }
    private readonly List<Budget> _budgets = [];
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private Budget? _budget;
    private Vector3 _destination, _queryFrom, _queryTo, _anchor;
    private DateTime _queryAt, _progressAt, _readyAt;
    private CancellationTokenSource? _cancel;
    private Task<List<Vector3>>? _pending;
    private bool _active, _ownsPath;

    public NavDriveResult Tick(Vector3 destination, Vector3 player, float radius)
    {
        var now = _clock();
        if (!CombatPositionPolicy.IsFinite(destination) || !CombatPositionPolicy.IsFinite(player)) return NavDriveResult.Failed;
        if (Vector3.DistanceSquared(player, destination) <= radius * radius) { Cancel(); return NavDriveResult.Arrived; }
        if (!_active || Vector3.DistanceSquared(destination, _destination) > .25f)
        {
            Cancel(); _active = true; _destination = destination; _anchor = player; _progressAt = _readyAt = now;
            _budget = _budgets.FirstOrDefault(b => Vector3.DistanceSquared(b.Goal, destination) <= .25f);
            if (_budget == null) { _budget = new(destination, player); _budgets.Add(_budget); }
        }
        if (_pending != null)
        {
            if (!_pending.IsCompleted)
            {
                if ((now - _queryAt).TotalSeconds >= 8) Reject("query-timeout");
                return NavDriveResult.StuckRetrying;
            }
            try
            {
                var path = _pending.GetAwaiter().GetResult();
                if (Vector3.DistanceSquared(player, _queryFrom) > 4)
                { CancelQuery(); return NavDriveResult.StuckRetrying; }
                if (!PassageNavigationRecovery.ValidPath(path, _queryFrom, _queryTo) ||
                    Vector3.DistanceSquared(path[^1], _destination) > radius * radius)
                { Reject("partial-invalid-or-stale-path"); return NavDriveResult.StuckRetrying; }
                CancelQuery(); move(path); _ownsPath = true; _anchor = player; _progressAt = now;
                log($"[HOH navigation] Accepted complete path: from={_queryFrom}, goal={_queryTo}, points={path.Count}");
            }
            catch (Exception ex) { Reject("query-or-move-failed: " + ex.Message); }
            return NavDriveResult.Moving;
        }
        if (_ownsPath)
        {
            if (Vector3.DistanceSquared(_anchor, player) >= 1) { _anchor = player; _progressAt = now; }
            if ((now - _progressAt).TotalSeconds < 8) return NavDriveResult.Moving;
            Reject("validated-path-no-progress");
        }
        if (Vector3.DistanceSquared(_budget!.Anchor, player) >= 2 * 2)
        { _budget.Anchor = player; _budget.LocalFailures = 0; }
        if (_budget.Failures >= (_budget.MapRechecked ? 12 : 9) || _budget.LocalFailures >= 3) return NavDriveResult.Failed;
        // Await an old SimpleMove query before issuing a path that it could overwrite.
        try
        {
            if (simpleMovePending?.Invoke() == true || !ready())
                return (now - _readyAt).TotalSeconds >= 10 ? NavDriveResult.Failed : NavDriveResult.StuckRetrying;
        }
        catch (Exception ex) { log("[HOH navigation] Navigation service unavailable: " + ex.Message); return NavDriveResult.Failed; }
        // Only observed failures consume the budget. Success and owner cancellation do not.
        int attempt = _budget.LocalFailures + 1;
        float offset = Math.Min(.3f, Math.Max(0, radius * .5f));
        var seed = destination + new Vector3(attempt == 2 ? offset : attempt == 3 ? -offset : 0, 0, 0);
        try
        {
            var point = ground(seed);
            if (point is not { } goal || !CombatPositionPolicy.IsFinite(goal) ||
                Vector2.DistanceSquared(new(goal.X, goal.Z), new(destination.X, destination.Z)) > radius * radius ||
                Math.Abs(goal.Y - destination.Y) > 1.6f)
            { Reject("destination-has-no-local-ground"); return NavDriveResult.StuckRetrying; }
            _queryFrom = player; _queryTo = goal; _queryAt = now;
            _cancel = new(); _pending = findPath(player, goal, _cancel.Token);
            _pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) { Reject("dispatch-failed: " + ex.Message); }
        return NavDriveResult.StuckRetrying;
    }
    private void Reject(string reason)
    {
        if (_budget != null) { _budget.Failures++; _budget.LocalFailures++; }
        CancelQuery(); StopOwnedPath();
        log($"[HOH navigation] Rejected route: goal={_destination}, failure={_budget?.Failures}, reason={reason}");
    }
    private void CancelQuery() { _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null; _pending = null; }
    private void StopOwnedPath() { if (_ownsPath) { _ownsPath = false; stop(); } }
    // Task interruptions preserve real failures; cancelling a successful query costs nothing.
    public void Cancel() { CancelQuery(); StopOwnedPath(); _active = false; }
    public void RecheckAfterMapRefresh()
    {
        Cancel();
        if (_budget == null || _budget.MapRechecked) return;
        _budget.MapRechecked = true;
        _budget.LocalFailures = 0;
    }
    public void ResetBudget() { Cancel(); _budgets.Clear(); _budget = null; }
    public void Dispose() => Cancel();
}
