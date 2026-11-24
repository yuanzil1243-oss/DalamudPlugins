namespace DeepDungeon.Fsd.Core;

/// <summary>Floor-owned budgets, independent of objective/task instances. Call Tick even when a goal changes.</summary>
public sealed class OptionalChestBudget
{
    private sealed class Attempt(DateTime now, float distance)
    {
        public DateTime Last = now;
        public float BestDistance = distance;
        public double ActiveSeconds, IdleSeconds;
        public int Failures;
        public bool Paused;
        public string? Abandoned;
    }
    private readonly Dictionary<ulong, Attempt> _attempts = [];
    public void Select(ulong id, float distance, DateTime now)
    {
        if (id != 0 && float.IsFinite(distance)) _attempts.TryAdd(id, new(now, distance));
    }
    public bool Contains(ulong id) => _attempts.ContainsKey(id);
    public string? Reason(ulong id) => _attempts.GetValueOrDefault(id)?.Abandoned;
    public object? Diagnostics(ulong id) => _attempts.TryGetValue(id, out var a) ?
        new { activeSeconds = a.ActiveSeconds, noProgressSeconds = a.IdleSeconds,
            failedRecoveryRounds = a.Failures, closestDistance = a.BestDistance, reason = a.Abandoned } : null;
    public void Tick(ulong id, DateTime now, bool paused, float? distance = null, bool confirmedProgress = false)
    {
        if (!_attempts.TryGetValue(id, out var a) || a.Abandoned != null) return;
        double dt = Math.Max(0, (now - a.Last).TotalSeconds);
        a.Last = now;
        // Exclude both ends of a suspension interval. A combat/avoidance frame cannot expire a box.
        if (!paused && !a.Paused) { a.ActiveSeconds += dt; a.IdleSeconds += dt; }
        a.Paused = paused;
        if (paused) return;
        if (confirmedProgress || distance is { } d && float.IsFinite(d) &&
            (d <= a.BestDistance - .5f || d <= 1.5f && a.BestDistance > 1.5f))
        {
            a.IdleSeconds = 0;
            if (distance.HasValue) a.BestDistance = Math.Min(a.BestDistance, distance.Value);
        }
        if (a.ActiveSeconds >= 60) a.Abandoned = "active-time-limit";
        else if (a.IdleSeconds >= 30) a.Abandoned = "no-real-progress";
    }
    public void FailedRecovery(ulong id)
    {
        if (_attempts.TryGetValue(id, out var a) && ++a.Failures >= 3) a.Abandoned ??= "recovery-limit";
    }
}
