namespace DeepDungeon.Fsd.Core;

/// <summary>A single recovery request per target/stall, rearmed only by observed GCD progress.</summary>
public sealed class GcdStallWatchdog
{
    public const long TimeoutMilliseconds = 6000;
    private ulong _target;
    private long? _since;
    private long _lastTick;
    public bool Attempted { get; private set; }
    public long IdleMilliseconds { get; private set; }

    public bool Update(long now, ulong target, bool eligible, bool gcdActive)
    {
        if (target == 0 || now < _lastTick) { Reset(); return false; }
        if (_target != target) { Reset(); _target = target; }
        _lastTick = now;
        if (gcdActive)
        {
            Attempted = false;
            _since = null;
            IdleMilliseconds = 0;
            return false;
        }
        if (!eligible) { _since = null; IdleMilliseconds = 0; return false; }
        if (Attempted) return false;
        _since ??= now;
        IdleMilliseconds = now - _since.Value;
        if (IdleMilliseconds < TimeoutMilliseconds) return false;
        Attempted = true; // Includes rejected native calls: no repeated blind requests.
        return true;
    }

    public void Reset()
    {
        _target = 0;
        _since = null;
        _lastTick = IdleMilliseconds = 0;
        Attempted = false;
    }
}

public static class GcdRecoveryActions
{
    // Base single-target GCDs only. Native adjustment resolves traits/level sync.
    // No ActionDefinitions singleton: diagnostics must also work if that initializer failed.
    public static uint ForJob(uint job) => job switch
    {
        1 or 19 => 9, 2 or 20 => 53, 3 or 21 => 31, 4 or 22 => 75,
        5 or 23 => 97, 6 or 24 => 119, 7 or 25 => 142, 26 or 27 => 163,
        28 => 17869, 29 or 30 => 2240, 31 => 2866, 32 => 3617,
        33 => 3596, 34 => 7477, 35 => 7503, 37 => 16137,
        38 => 15989, 39 => 24373, 40 => 24283, 41 => 34606, 42 => 34650,
        _ => 0 // Unsupported jobs, BLU, crafters and gatherers never guess a spell.
    };
}
