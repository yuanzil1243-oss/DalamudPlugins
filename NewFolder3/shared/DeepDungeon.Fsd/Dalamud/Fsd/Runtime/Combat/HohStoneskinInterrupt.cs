namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

/// <summary>At most one accepted request per native cast sequence; rejected calls may retry briefly.</summary>
internal sealed class HohStoneskinInterrupt
{
    private (ulong Actor, uint Sequence) _accepted;
    private DateTime _retryAt;
    private ulong _castingActor;
    private float _lastElapsed;
    internal void ObserveCast(ulong actor, bool casting, float elapsed)
    {
        if (!casting || actor != _castingActor || elapsed + .2f < _lastElapsed) _accepted = default;
        _castingActor = casting ? actor : 0; _lastElapsed = elapsed;
    }
    internal static uint ActionFor(uint job, int level) => job switch
    {
        1 or 3 or 19 or 21 or 32 or 37 when level >= 18 => 7538,
        5 or 23 or 31 or 38 when level >= 24 => 7551,
        _ => 0
    };
    internal bool CanAttempt(ulong actor, uint sequence, bool interruptible, float remaining, DateTime now) =>
        actor != 0 && interruptible && float.IsFinite(remaining) && remaining > .2f &&
        _accepted != (actor, sequence) && now >= _retryAt;
    internal void Record(ulong actor, uint sequence, bool accepted, DateTime now)
    { _retryAt = now.AddMilliseconds(250); if (accepted) _accepted = (actor, sequence); }
    internal void Reset() { _accepted = default; _retryAt = default; _castingActor = 0; _lastElapsed = 0; }
}
