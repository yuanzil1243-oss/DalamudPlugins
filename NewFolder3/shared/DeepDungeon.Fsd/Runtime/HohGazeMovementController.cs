using System.Numerics;

namespace DeepDungeon.Fsd.Runtime;

/// <summary>Bounded gaze handoff: one checked short step, then hold facing until the output gate opens.</summary>
public sealed class HohGazeMovementController(Func<bool, bool> ownership, Action<Vector3> move,
    Action stopInput, Action<Vector3> faceAway, Func<Vector3, Vector3?> ground,
    Func<Vector3, Vector3, bool> safe, Action<string> report) : IDisposable
{
    private bool _owns, _stepping;
    private Vector3 _start, _away, _destination;
    private DateTime _stepUntil;
    public bool OwnsMovement => _owns;

    public void Update(bool active, Vector3 player, Vector3 boss, DateTime now)
    {
        if (!active || !Finite(player) || !Finite(boss)) { Reset(); return; }
        if (!_owns)
        {
            var away = player - boss; away.Y = 0;
            if (away.LengthSquared() < .01f) return; // Do not invent a direction at an overlapping center.
            away = Vector3.Normalize(away);
            var seed = player + away;
            var point = ground(seed);
            bool canStep = point is { } p && Finite(p) && Vector3.DistanceSquared(p, seed) <= .09f &&
                SegmentGrounded(player, p) && safe(player, p);
            if (!ownership(true)) return; // Never issue movement or facing while BMR still owns it.
            _owns = true; _start = player; _away = away;
            _stepping = canStep;
            _destination = point ?? player;
            _stepUntil = now.AddMilliseconds(600);
            report(canStep ? "acquired-short-step" : "acquired-face-in-place-no-safe-step");
        }
        if (_stepping && (now >= _stepUntil || Vector3.Dot(player - _start, _away) >= .8f ||
            Vector3.DistanceSquared(player, _destination) <= .04f)) _stepping = false;
        if (_stepping) move(_destination);
        else stopInput();
        // Keep looking away even after the one short step; do not keep walking toward an arena edge.
        var awayNow = player - boss; awayNow.Y = 0;
        faceAway(player + (awayNow.LengthSquared() >= .01f ? Vector3.Normalize(awayNow) : _away) * 10);
    }

    private bool SegmentGrounded(Vector3 from, Vector3 to)
    {
        for (int i = 0; i <= 4; i++)
        {
            var seed = Vector3.Lerp(from, to, i / 4f);
            var point = ground(seed);
            if (point is not { } p || !Finite(p) || Vector3.DistanceSquared(p, seed) > .09f) return false;
        }
        return true;
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    public void Reset()
    {
        if (!_owns) return;
        try { stopInput(); }
        finally { _owns = _stepping = false; ownership(false); report("released-to-bmr"); }
    }
    public void Dispose() => Reset();
}
