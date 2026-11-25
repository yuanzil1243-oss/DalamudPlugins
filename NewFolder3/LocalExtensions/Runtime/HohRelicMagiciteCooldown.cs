namespace DeepDungeon.Fsd.Runtime;

/// <summary>A conservative request interval, not a claim to read or change the server cooldown.</summary>
public sealed class HohRelicMagiciteCooldown
{
    public const int MinimumIntervalMilliseconds = 10500;
    public DateTime NextRequestAtUtc { get; private set; } = DateTime.MinValue;
    public bool CanRequest(DateTime now) => now >= NextRequestAtUtc;
    public void ObserveRequestOrConfirmation(DateTime now)
    {
        DateTime next = now.AddMilliseconds(MinimumIntervalMilliseconds);
        if (next > NextRequestAtUtc) NextRequestAtUtc = next;
    }
}
