using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>IsDashSafe only checks its destination. Walking additionally probes the intervening segment.</summary>
public static class Pt99WalkSafety
{
    public static bool IsSafe(Vector3 start, Vector3 end, Func<Vector3, Vector3, bool> destinationSafe)
    {
        if (!Pt99Arena.Contains(new(start.X, start.Z)) || !Pt99Arena.Contains(new(end.X, end.Z)) ||
            !float.IsFinite(start.Y) || !float.IsFinite(end.Y)) return false;
        // When BMR is actively requiring escape (including its own color guidance), leave
        // movement with BMR rather than pretending an unsafe prefix is a clear walking route.
        if (!destinationSafe(start, start)) return false;
        int steps = Math.Max(1, (int)Math.Ceiling(Vector2.Distance(new(start.X, start.Z), new(end.X, end.Z)) / 0.75f));
        for (int i = 1; i <= steps; i++)
            if (!destinationSafe(start, Vector3.Lerp(start, end, (float)i / steps))) return false;
        return true;
    }
}
