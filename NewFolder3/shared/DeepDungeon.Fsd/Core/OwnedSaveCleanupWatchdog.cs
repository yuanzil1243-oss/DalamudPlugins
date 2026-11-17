namespace DeepDungeon.Fsd.Core;

public enum CleanupWatchdogDecision { Continue, Recover, Fail }

/// <summary>Only observed monotonic UI milestones or real approach movement renew the budget.</summary>
public sealed class OwnedSaveCleanupWatchdog
{
    public const int MaximumRecoveries = 3;
    public static readonly TimeSpan NoProgressTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(120);
    private DateTime _started, _progress, _lastTick;
    private int _milestone;
    private float _bestDistance = float.PositiveInfinity;
    public int Recoveries { get; private set; }
    public void Reset(DateTime now)
    { _started = _progress = _lastTick = now; _milestone = 0; Recoveries = 0; _bestDistance = float.PositiveInfinity; }
    public void Observe(int milestone, DateTime now, float? npcDistance = null)
    {
        if (milestone > _milestone) { _milestone = milestone; _progress = now; }
        if (npcDistance is { } distance && float.IsFinite(distance) && distance >= 0 && distance < _bestDistance - 1)
        { _bestDistance = distance; _progress = now; }
    }
    public CleanupWatchdogDecision Tick(DateTime now, bool paused = false)
    {
        var delta = now > _lastTick ? now - _lastTick : TimeSpan.Zero;
        _lastTick = now;
        if (paused) { _started += delta; _progress += delta; return CleanupWatchdogDecision.Continue; }
        if (now - _started >= TotalTimeout) return CleanupWatchdogDecision.Fail;
        if (now - _progress < NoProgressTimeout) return CleanupWatchdogDecision.Continue;
        if (Recoveries >= MaximumRecoveries) return CleanupWatchdogDecision.Fail;
        Recoveries++; _progress = now;
        return CleanupWatchdogDecision.Recover;
    }
}
