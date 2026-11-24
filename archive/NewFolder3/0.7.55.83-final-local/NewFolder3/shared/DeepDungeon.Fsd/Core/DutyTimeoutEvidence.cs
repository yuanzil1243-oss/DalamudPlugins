namespace DeepDungeon.Fsd.Core;

public enum DutyTimeoutDecision { None, WaitingForExitEvidence, Expired }

/// <summary>Attempt-scoped evidence. A missing duty or a disconnect alone never proves expiry.</summary>
public sealed class DutyTimeoutEvidence
{
    public DateTime SampleAtUtc { get; private set; }
    public int RemainingSeconds { get; private set; }
    public byte LastFloor { get; private set; }
    public uint DungeonId { get; private set; }
    private DateTime? _outsideSince;
    private bool _positiveObserved;
    private bool _expired;

    public void Observe(uint dungeonId, byte floor, int remaining, uint maximum, DateTime now)
    {
        if (dungeonId == 0 || floor == 0 || maximum < 30 || remaining < 0 || remaining > maximum) return;
        if (DungeonId != 0 && DungeonId != dungeonId) Reset();
        DungeonId = dungeonId; LastFloor = floor;
        _positiveObserved |= remaining > 0;
        SampleAtUtc = now; RemainingSeconds = remaining;
        _outsideSince = null;
    }

    public DutyTimeoutDecision Evaluate(bool inDuty, bool stable, bool completed, bool failed, DateTime now)
    {
        if (completed || failed) return DutyTimeoutDecision.None;
        if (_expired) return DutyTimeoutDecision.Expired;
        if (!stable) { _outsideSince = null; return DutyTimeoutDecision.WaitingForExitEvidence; }
        if (!_positiveObserved || SampleAtUtc == default) return DutyTimeoutDecision.None;
        if (inDuty)
        {
            _outsideSince = null;
            // Classify only after a stable exit; an in-duty zero is not an ejection.
            return DutyTimeoutDecision.None;
        }
        _outsideSince ??= now;
        if ((now - _outsideSince.Value).TotalSeconds < 2) return DutyTimeoutDecision.WaitingForExitEvidence;
        // If an exit happens between the final frames, a recent <=5s sample plus the
        // crossed deadline is acceptable. Old timers and ordinary early exits are not.
        double age = (now - SampleAtUtc).TotalSeconds;
        if (age is >= 0 and <= 30 && RemainingSeconds <= 5)
        {
            if (now < SampleAtUtc.AddSeconds(RemainingSeconds + 1))
                return DutyTimeoutDecision.WaitingForExitEvidence;
            _expired = true;
            return DutyTimeoutDecision.Expired;
        }
        return DutyTimeoutDecision.None;
    }

    public void Reset()
    {
        SampleAtUtc = default; RemainingSeconds = 0; LastFloor = 0; DungeonId = 0;
        _positiveObserved = _expired = false; _outsideSince = null;
    }
}
