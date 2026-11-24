namespace DeepDungeon.Fsd.Core;

/// <summary>Shared retry cadence. Each interaction keeps its own pending state and deadline.</summary>
public static class InteractionTiming
{
    private static readonly TimeSpan NormalRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TransitionRetryInterval = TimeSpan.FromMilliseconds(400);

    public static TimeSpan GetRetryInterval(bool aggressive = false) =>
        aggressive ? TimeSpan.Zero : NormalRetryInterval;

    public static int GetRetryMilliseconds(bool aggressive = false) =>
        (int)GetRetryInterval(aggressive).TotalMilliseconds;

    public static TimeSpan GetTransitionRetryInterval() => TransitionRetryInterval;

    public static int GetTransitionRetryMilliseconds() =>
        (int)TransitionRetryInterval.TotalMilliseconds;
}
