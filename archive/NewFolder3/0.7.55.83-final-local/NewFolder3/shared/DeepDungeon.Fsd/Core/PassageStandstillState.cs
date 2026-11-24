using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>Latch only an exact passage after a transition decision, with horizontal hysteresis.</summary>
public sealed class PassageStandstillState
{
    public const float EnterRadius = 0.5f;
    public const float LeaveRadius = 1.25f;
    private Vector3 _passage;
    private DateTime _started, _retryAt;
    public bool Holding { get; private set; }

    public bool Update(bool eligible, Vector3 player, Vector3 passage, DateTime now)
    {
        if (!float.IsFinite(player.X) || !float.IsFinite(player.Y) || !float.IsFinite(player.Z) ||
            !float.IsFinite(passage.X) || !float.IsFinite(passage.Y) || !float.IsFinite(passage.Z))
        { Reset(); return false; }
        bool changed = Vector3.DistanceSquared(_passage, passage) > 0.25f;
        float dx = player.X - passage.X, dz = player.Z - passage.Z;
        float distanceSquared = dx * dx + dz * dz;
        if (!eligible || changed || !float.IsFinite(distanceSquared) || Math.Abs(player.Y - passage.Y) > 3f ||
            distanceSquared > LeaveRadius * LeaveRadius)
        {
            Reset();
            _passage = passage;
        }
        if (!eligible || !float.IsFinite(distanceSquared) || Math.Abs(player.Y - passage.Y) > 3f) return false;
        if (Holding && (now - _started).TotalSeconds >= 8)
        {
            Holding = false;
            _retryAt = now.AddSeconds(1); // Recenter once if native passage activation did not happen.
        }
        if (!Holding && now >= _retryAt && distanceSquared <= EnterRadius * EnterRadius)
        {
            Holding = true;
            _passage = passage;
            _started = now;
        }
        return Holding;
    }

    public void Reset() { Holding = false; _started = _retryAt = default; }
}
