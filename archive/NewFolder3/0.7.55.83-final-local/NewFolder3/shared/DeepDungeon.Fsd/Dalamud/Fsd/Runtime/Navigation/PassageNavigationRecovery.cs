using System.Numerics;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

/// <summary>One floor's passage recovery; helper resets and room changes cannot refill its budget.</summary>
internal sealed class PassageNavigationRecovery(
    Func<bool> ready,
    Func<bool> simpleMovePending,
    Func<Vector3, Vector3?> ground,
    Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>> findPath,
    Action<List<Vector3>> move,
    Action cancelNormal,
    Action stopPath,
    Action<string, object> record,
    Func<DateTime>? clock = null) : IDisposable
{
    private readonly Func<DateTime> _clock = clock ?? (() => DateTime.UtcNow);
    private Vector3 _anchor, _destination, _queryFrom, _queryTo;
    private DateTime? _lastTick;
    private DateTime _queryAt;
    private double _stalledSeconds, _recoverySeconds;
    private CancellationTokenSource? _cancel;
    private Task<List<Vector3>>? _pending;
    private bool _started, _recovering, _ownsPath, _failed, _suspended, _staging;
    private int _queries;
    internal int QueryCount => _queries;
    internal bool Recovering => _recovering;

    public NavDriveResult Tick(Vector3 destination, Vector3 player, Func<NavDriveResult> normal)
    {
        var now = _clock();
        if (_failed) return NavDriveResult.Failed;
        if (!CombatPositionPolicy.IsFinite(destination) || !CombatPositionPolicy.IsFinite(player)) return Fail("invalid-position");
        if (!_started)
        { _started = true; _anchor = player; _destination = destination; }
        // The actor may replace its room centre. It does not grant a fresh attempt budget.
        if (Vector3.DistanceSquared(_destination, destination) > 1)
        { CancelQuery(); StopOwnedPath(); _destination = destination; }
        double elapsed = _lastTick.HasValue ? Math.Max(0, (now - _lastTick.Value).TotalSeconds) : 0;
        _lastTick = now; _suspended = false;
        _stalledSeconds += elapsed;
        if (_recovering) _recoverySeconds += elapsed;
        if (Vector2.Distance(new(player.X, player.Z), new(_anchor.X, _anchor.Z)) >= 1.5f)
        { _anchor = player; _stalledSeconds = 0; }
        if (Vector3.DistanceSquared(player, destination) <= .5f * .5f)
        { CancelQuery(); StopOwnedPath(); return NavDriveResult.Arrived; }
        if (!_recovering)
        {
            var result = normal();
            if (result != NavDriveResult.Failed && _stalledSeconds < 12) return result;
            cancelNormal(); _recovering = true; _stalledSeconds = 0;
            record("passage-recovery-started", new { destination, player, result = result.ToString() });
        }
        if (_recoverySeconds >= 60) return Fail("active-recovery-budget-exhausted");
        // Do not let a late SimpleMove task overwrite a validated recovery path.
        // Never reload the global mesh (Nav.PathfindCancelAll) to cancel another owner's query.
        if (simpleMovePending())
        {
            if (_stalledSeconds >= 10) return Fail("previous-path-query-did-not-finish");
            return NavDriveResult.StuckRetrying;
        }
        if (_pending != null)
        {
            if (!_pending.IsCompleted)
            {
                if ((now - _queryAt).TotalSeconds >= 8) Reject("path-query-timeout");
                return NavDriveResult.StuckRetrying;
            }
            try
            {
                var path = _pending.GetAwaiter().GetResult();
                if (!ValidPath(path, _queryFrom, _queryTo) || Vector3.DistanceSquared(player, _queryFrom) > 4)
                { Reject("invalid-or-stale-path"); return NavDriveResult.StuckRetrying; }
                CancelQuery(); move(path); _ownsPath = true; _stalledSeconds = 0; _anchor = player;
                record("passage-recovery-path-accepted", new { attempt = _queries, from = _queryFrom, to = _queryTo, points = path.Count, staging = _staging });
            }
            catch (Exception ex) { Reject("path-query-failed: " + ex.Message); }
            return NavDriveResult.StuckRetrying;
        }
        if (_ownsPath)
        {
            if (_staging && Vector3.DistanceSquared(player, _queryTo) < .8f * .8f)
            { StopOwnedPath(); _stalledSeconds = 0; }
            else if (_stalledSeconds >= 10) Reject("validated-path-no-progress");
            else return NavDriveResult.Moving;
        }
        if (_queries >= 5) return Fail("path-attempts-exhausted");
        if (!ready())
        {
            if (_stalledSeconds >= 10) return Fail("navmesh-not-ready");
            return NavDriveResult.StuckRetrying;
        }
        // Retry near the circle on either side, then approach it via a short staging leg.
        int attempt = ++_queries;
        var offset = attempt switch { 2 => new Vector3(.3f, 0, 0), 3 => new Vector3(-.3f, 0, 0), _ => Vector3.Zero };
        _staging = attempt == 4;
        if (_staging)
        {
            var delta = player - destination; delta.Y = 0;
            if (delta.LengthSquared() > .01f) offset = Vector3.Normalize(delta) * 3;
        }
        var point = ground(destination + offset);
        if (point is not { } p || !CombatPositionPolicy.IsFinite(p) ||
            Vector3.DistanceSquared(p, destination + offset) > 2.5f * 2.5f)
        { record("passage-recovery-path-rejected", new { attempt, reason = "no-ground-point" }); return NavDriveResult.StuckRetrying; }
        _queryFrom = player; _queryTo = p; _queryAt = now;
        _cancel = new CancellationTokenSource();
        try
        {
            _pending = findPath(player, p, _cancel.Token);
            _pending.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }
        catch (Exception ex) { Reject("path-dispatch-failed: " + ex.Message); }
        return NavDriveResult.StuckRetrying;
    }

    internal static bool ValidPath(IReadOnlyList<Vector3>? path, Vector3 from, Vector3 to)
    {
        if (path == null || path.Count is < 1 or > 4096 ||
            path.Any(p => !CombatPositionPolicy.IsFinite(p)) ||
            Vector3.DistanceSquared(path[^1], to) > .5f * .5f ||
            Vector3.DistanceSquared(path[0], from) > 4 * 4) return false;
        float length = 0;
        for (int i = 1; i < path.Count; i++) length += Vector3.Distance(path[i - 1], path[i]);
        return length <= Math.Max(500, Vector3.Distance(from, to) * 10);
    }
    private void Reject(string reason)
    { CancelQuery(); StopOwnedPath(); record("passage-recovery-path-rejected", new { attempt = _queries, reason }); }
    private NavDriveResult Fail(string reason)
    {
        if (!_failed) { _failed = true; CancelQuery(); StopOwnedPath(); cancelNormal();
            record("passage-recovery-exhausted", new { reason, attempts = _queries, activeSeconds = _recoverySeconds }); }
        return NavDriveResult.Failed;
    }
    private void CancelQuery()
    { _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null; _pending = null; }
    private void StopOwnedPath() { if (_ownsPath) { _ownsPath = false; stopPath(); } }
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true; _lastTick = null; CancelQuery(); StopOwnedPath();
        // Evidence and the query budget survive a combat/mechanic interruption.
    }
    public void Dispose() { CancelQuery(); StopOwnedPath(); }
}
