using System.Numerics;

namespace DeepDungeon.Fsd.Runtime;

public enum HohPetrifiedContactStep { Approach, Attack, ApproachStalled, OutputStalled }

/// <summary>Approach stone-curse targets while output stays enabled; bound arrival and output stalls separately.</summary>
public sealed class HohPetrifiedContactState
{
    public const float FootRadius = 1.2f;
    public ulong TargetId { get; private set; }
    private Vector3 _lastPosition;
    private DateTime _lastMoveAt, _lastOutputAt;
    private uint _hp;
    private long _hit;
    public static bool AtFeet(Vector3 player, Vector3 target) =>
        Vector3.DistanceSquared(player, target) <= FootRadius * FootRadius;
    public HohPetrifiedContactStep Update(ulong target, Vector3 player, Vector3 destination,
        uint hp, long hit, DateTime now)
    {
        if (TargetId != target)
        {
            Reset(); TargetId = target; _lastPosition = player; _lastMoveAt = now;
            _hp = hp; _hit = hit;
        }
        if (Vector3.DistanceSquared(player, _lastPosition) >= .5f * .5f)
        { _lastPosition = player; _lastMoveAt = now; }
        bool progress = hp < _hp || hit > _hit;
        _hp = hp; _hit = hit;
        if (!AtFeet(player, destination))
        {
            _lastOutputAt = default;
            return now - _lastMoveAt >= TimeSpan.FromSeconds(3)
                ? HohPetrifiedContactStep.ApproachStalled : HohPetrifiedContactStep.Approach;
        }
        if (_lastOutputAt == default || progress) _lastOutputAt = now;
        return now - _lastOutputAt >= TimeSpan.FromSeconds(4)
            ? HohPetrifiedContactStep.OutputStalled : HohPetrifiedContactStep.Attack;
    }
    public void Reset()
    { TargetId = 0; _lastPosition = default; _lastMoveAt = _lastOutputAt = default; _hp = 0; _hit = 0; }
}
