namespace DeepDungeon.Fsd.Runtime;

public enum FortunePreparation { Ready, Wait, RequestFortune }

/// <summary>Serializes Fortune confirmation before Fate without blocking missing-item floors.</summary>
public sealed class FortuneBeforeFateState
{
    public DateTime? StartedAt { get; private set; }
    public bool RequestSent { get; private set; }

    public FortunePreparation Decide(bool active, bool pending, int stock, bool allowed, DateTime now)
    {
        if (active) return FortunePreparation.Ready;
        if (pending) return FortunePreparation.Wait;
        if (!allowed || !RequestSent && stock <= 0) return FortunePreparation.Ready;
        StartedAt ??= now;
        if (now - StartedAt.Value > TimeSpan.FromSeconds(8)) return FortunePreparation.Ready;
        return RequestSent ? FortunePreparation.Wait : FortunePreparation.RequestFortune;
    }

    public void RecordDispatch() => RequestSent = true;
}
