using System.Numerics;

namespace DeepDungeon.Fsd.Runtime;

/// <summary>Bounded full-segment checks; the BMR dash IPC alone validates only its endpoint.</summary>
public static class Hoh90RouteSafety
{
    public const int ProbeBudget = 256;
    public static bool Check(Vector3 from, Vector3 to, Func<Vector3, Vector3?> ground,
        Func<Vector3, Vector3, bool> safeSegment, ref int probes)
    {
        if (!Finite(from) || !Finite(to)) return false;
        int samples = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(from, to) / .75f));
        if (samples > 64) return false;
        var previous = from;
        for (int i = 0; i <= samples; i++)
        {
            if (probes >= ProbeBudget) return false;
            probes++;
            var seed = Vector3.Lerp(from, to, i / (float)samples);
            var point = ground(seed);
            if (point is not { } p || !Finite(p) || Vector3.DistanceSquared(p, seed) > .16f ||
                !safeSegment(previous, p)) return false;
            previous = p;
        }
        return true;
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
