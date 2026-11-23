namespace DeepDungeon.Fsd.Core;

/// <summary>Protect one normal cast, not an endless sequence of offensive attempts without verified output.</summary>
public sealed class HohRecoveryCastState
{
    public static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(1);
    private DateTime? _lastSample, _finishedUntil, _retryReadyAt;
    private uint _actionId;
    private float _elapsed;
    private bool _offensiveCast;
    public int UnconfirmedCasts { get; private set; }

    // Cast boundaries are observations, not native action-failure confirmations. Geometry/no-output
    // evidence is still required by the recovery controller before it can interrupt or move.
    public bool Observe(DateTime now, bool casting, bool offensive, uint actionId, float elapsed)
    {
        if (_lastSample is { } last && (now < last || now - last > TimeSpan.FromSeconds(1))) Reset();
        _lastSample = now;
        bool currentOffensive = casting && offensive && actionId != 0 && float.IsFinite(elapsed) && elapsed >= 0;
        bool newCast = currentOffensive && (!_offensiveCast || _actionId != actionId || elapsed + .15f < _elapsed);
        if (_offensiveCast && (!currentOffensive || newCast))
        {
            UnconfirmedCasts++;
            _finishedUntil = now + OutputGrace;
            if (UnconfirmedCasts >= 2) _retryReadyAt ??= _finishedUntil;
        }
        _offensiveCast = currentOffensive;
        _actionId = currentOffensive ? actionId : 0;
        _elapsed = currentOffensive ? elapsed : 0;
        // Buff/heal/unknown casts never count as failed offensive casts and are never cancelled.
        if (casting && !currentOffensive) return true;
        if (_retryReadyAt is { } ready && now >= ready) return false;
        return casting || _finishedUntil is { } until && now < until;
    }

    public void Reset()
    {
        _lastSample = _finishedUntil = _retryReadyAt = null;
        _actionId = 0; _elapsed = 0; _offensiveCast = false; UnconfirmedCasts = 0;
    }
}
