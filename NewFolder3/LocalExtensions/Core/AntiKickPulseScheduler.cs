namespace DeepDungeon.Fsd.Core;

public readonly record struct AntiKickInputTimers(float Afk, float Content, float Input)
{
    public bool Valid => float.IsFinite(Afk) && float.IsFinite(Content) && float.IsFinite(Input) &&
        Afk >= 0 && Content >= 0 && Input >= 0;
    public float Maximum => Math.Max(Afk, Math.Max(Content, Input));
    public bool ObservedResetSince(AntiKickInputTimers before) => Valid && before.Valid &&
        before.Maximum >= AntiKickPulseScheduler.IdleSeconds &&
        (before.Afk < AntiKickPulseScheduler.IdleSeconds || Afk < before.Afk) &&
        (before.Content < AntiKickPulseScheduler.IdleSeconds || Content < before.Content) &&
        (before.Input < AntiKickPulseScheduler.IdleSeconds || Input < before.Input);
}

public enum AntiKickPulseCommand { None, Press, Release }

/// <summary>Frame-driven short input pulse; independent from character motion and combat.</summary>
public sealed class AntiKickPulseScheduler
{
    public const float IdleSeconds = 60;
    public const long PulseMilliseconds = 200;
    public const long RepeatMilliseconds = 60_000;
    private long _lastTick, _releaseAt, _nextAttempt;
    public bool Holding { get; private set; }

    public AntiKickPulseCommand Update(long now, bool eligible, AntiKickInputTimers timers, bool modifierHeld)
    {
        bool backwards = now < _lastTick;
        _lastTick = now;
        if (Holding && (!eligible || backwards || now >= _releaseAt))
        {
            Holding = false;
            if (!eligible || backwards) _nextAttempt = 0;
            return AntiKickPulseCommand.Release;
        }
        if (!eligible || backwards)
        {
            _nextAttempt = 0;
            return AntiKickPulseCommand.None;
        }
        if (Holding || modifierHeld || !timers.Valid || timers.Maximum < IdleSeconds || now < _nextAttempt)
            return AntiKickPulseCommand.None;
        Holding = true;
        _releaseAt = now + PulseMilliseconds;
        _nextAttempt = now + RepeatMilliseconds;
        return AntiKickPulseCommand.Press;
    }

    // Failed dispatch preserves retry backoff. The host releases any possibly delivered key.
    public void CancelPulse() => Holding = false;
    public void Reset() { Holding = false; _lastTick = _releaseAt = _nextAttempt = 0; }
}
