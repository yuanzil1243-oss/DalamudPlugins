using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>Bounded search of verified color-floor interiors intersected with live BMR safety.</summary>
public sealed class Pt99ColorRouteSearch
{
    private Pt99Color _color;
    private Vector2 _origin;
    private Vector2[] _candidates = [];
    private int _cursor;
    public int LastProbeCount { get; private set; }

    public void Reset() { _color = Pt99Color.None; _candidates = []; _cursor = 0; }

    public bool TryFind(Vector3 start, Pt99Color color, Func<Vector3, Vector3, bool> safe,
        Func<Vector3, Vector3, bool> visible, out Vector3 destination, int candidateBudget = 6, int probeBudget = 384)
    {
        destination = default; LastProbeCount = 0;
        if (candidateBudget <= 0 || probeBudget <= 0 || color == Pt99Color.None ||
            !Pt99Arena.Contains(new(start.X, start.Z)) || !float.IsFinite(start.Y)) return false;
        bool Probe(Vector3 from, Vector3 to) => LastProbeCount < probeBudget && ++LastProbeCount > 0 && safe(from, to);
        // BMR must first finish escaping an unsafe current point. A stationary
        // player or safe endpoint alone is never proof of a traversable safe area.
        if (!Probe(start, start)) return false;
        var origin = new Vector2(start.X, start.Z);
        if (_color != color || _candidates.Length == 0 || Vector2.DistanceSquared(_origin, origin) > 1)
        {
            _color = color; _origin = origin; _cursor = 0;
            _candidates = Pt99ColorFloor.Candidates(color).OrderBy(p => Vector2.DistanceSquared(p, origin)).ToArray();
        }
        int count = Math.Min(candidateBudget, _candidates.Length);
        for (int i = 0; i < count && LastProbeCount < probeBudget; i++)
        {
            var point = _candidates[_cursor];
            _cursor = (_cursor + 1) % _candidates.Length;
            var goal = new Vector3(point.X, start.Y, point.Y);
            if (!visible(start, goal) || !Pt99WalkSafety.IsSafe(start, goal, Probe)) continue;
            // Retain margin inside the safe endpoint, independently of color-floor clearance.
            bool margin = true;
            foreach (var offset in new[] { new Vector3(.35f,0,0), new(-.35f,0,0), new(0,0,.35f), new(0,0,-.35f) })
                if (!Pt99Arena.Contains(new(goal.X+offset.X,goal.Z+offset.Z)) || !Probe(start,goal+offset)) { margin = false; break; }
            if (!margin) continue;
            destination = goal;
            return true;
        }
        return false;
    }
}
