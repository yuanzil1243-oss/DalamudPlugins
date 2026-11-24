using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public enum CombatRepositionRetry { Ready, Waiting, Changed, TimedOut }

/// <summary>A bounded handoff after an avoidance move; unchanged rejected paths are not retried each frame.</summary>
public sealed class CombatRepositionState
{
    public static readonly TimeSpan DodgeGrace = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan BlockedWait = TimeSpan.FromSeconds(8);
    public static readonly TimeSpan ReplanInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaximumUnproductiveGrace = TimeSpan.FromSeconds(30);
    private Vector3? _lastOutput;
    private DateTime _lastOutputAt, _firstResume, _graceUntil, _waitUntil, _retryAt;
    private Vector3 _failedPlayer, _failedTarget;
    private ulong _failedEnemies;
    private bool _yielded, _waiting;

    public bool InDodgeGrace(DateTime now) => now < _graceUntil;
    public void ObserveOutput(Vector3 position, DateTime now)
    {
        if (!CombatPositionPolicy.IsFinite(position)) return;
        _lastOutput = position; _lastOutputAt = now;
        _firstResume = _graceUntil = default; _waiting = _yielded = false;
    }
    public void YieldToMechanics() => _yielded = true;
    public bool ResumeAfterMechanics(DateTime now)
    {
        if (!_yielded) return false;
        _yielded = false; _waiting = false;
        if (_firstResume == default) _firstResume = now;
        var maximum = _firstResume + MaximumUnproductiveGrace;
        _graceUntil = now + DodgeGrace < maximum ? now + DodgeGrace : maximum;
        return true;
    }
    public Vector3? ReturnPosition(Vector3 player, DateTime now)
    {
        if (!InDodgeGrace(now) || _lastOutput is not { } point ||
            now - _lastOutputAt > TimeSpan.FromSeconds(60)) return null;
        float distance = Vector3.DistanceSquared(player, point);
        return distance is > 1 and <= 484 ? point : null;
    }
    public void WaitForChange(Vector3 player, Vector3 target, ulong enemies, DateTime now)
    {
        _failedPlayer = player; _failedTarget = target; _failedEnemies = enemies;
        _retryAt = now + ReplanInterval;
        _waitUntil = InDodgeGrace(now) ? _graceUntil : now + BlockedWait;
        _waiting = true;
    }
    public CombatRepositionRetry Retry(Vector3 player, Vector3 target, ulong enemies, DateTime now)
    {
        if (!_waiting) return CombatRepositionRetry.Ready;
        if (now >= _waitUntil) { _waiting = false; return CombatRepositionRetry.TimedOut; }
        if (now >= _retryAt && (Vector3.DistanceSquared(player, _failedPlayer) >= 1 ||
            Vector3.DistanceSquared(target, _failedTarget) >= 4 || enemies != _failedEnemies))
        { _waiting = false; return CombatRepositionRetry.Changed; }
        return CombatRepositionRetry.Waiting;
    }
    public void Reset()
    {
        _lastOutput = null; _lastOutputAt = _firstResume = _graceUntil = _waitUntil = _retryAt = default;
        _waiting = _yielded = false;
    }
}
