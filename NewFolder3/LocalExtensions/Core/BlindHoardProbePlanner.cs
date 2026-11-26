using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct BlindHoardPoint(string Key, int Room, Vector3 Position);

/// <summary>One immutable stop covering every source point, not a transitive overlap chain.</summary>
public sealed class BlindHoardProbeGroup
{
    public const float CoverageRadius = 1.8f;
    public const float HeightTolerance = 0.5f;
    public int Floor { get; }
    public int Room { get; }
    public string Key { get; }
    public Vector3 Position { get; }
    public IReadOnlyList<BlindHoardPoint> Points { get; }

    internal BlindHoardProbeGroup(int floor, BlindHoardPoint[] points, Vector3 position)
    {
        Floor = floor; Room = points[0].Room; Position = position;
        Points = Array.AsReadOnly(points);
        // Length-prefixing prevents different source sets from aliasing one work token.
        Key = "blind:" + string.Concat(points.Select(p => $"{p.Key.Length}:{p.Key}"));
    }

    public bool Covers(Vector3 position) => BlindHoardProbePlanner.IsFinite(position) &&
        Points.All(p => Math.Abs(p.Position.Y - position.Y) <= HeightTolerance &&
            Vector2.DistanceSquared(new(position.X, position.Z), new(p.Position.X, p.Position.Z))
                <= CoverageRadius * CoverageRadius);
}

public static class BlindHoardProbePlanner
{
    // Reserve the existing navigation tolerance inside the user's 1.8m region.
    private const float PlanningRadius = BlindHoardProbeGroup.CoverageRadius - NavigationRange.MovementTolerance;

    public static IReadOnlyList<BlindHoardProbeGroup> Build(int floor,
        IEnumerable<BlindHoardPoint> points, Func<int, Vector3, bool> canStand)
    {
        var pending = points.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        var result = new List<BlindHoardProbeGroup>();
        while (pending.Count > 0)
        {
            var seed = pending[0]; pending.RemoveAt(0);
            var members = new List<BlindHoardPoint> { seed };
            var stand = seed.Position;
            foreach (var candidate in pending.ToArray())
            {
                if (seed.Room < 0 || candidate.Room != seed.Room ||
                    !IsFinite(seed.Position) || !IsFinite(candidate.Position) ||
                    Math.Abs(candidate.Position.Y - seed.Position.Y) > BlindHoardProbeGroup.HeightTolerance ||
                    Vector2.DistanceSquared(Xz(seed.Position), Xz(candidate.Position)) > 4 * PlanningRadius * PlanningRadius)
                    continue;
                members.Add(candidate);
                if (TryStand(members, canStand, out var common))
                { stand = common; pending.Remove(candidate); }
                else members.RemoveAt(members.Count - 1);
            }
            result.Add(new(floor, members.OrderBy(p => p.Key, StringComparer.Ordinal).ToArray(), stand));
        }
        return result;
    }

    private static bool TryStand(List<BlindHoardPoint> points,
        Func<int, Vector3, bool> canStand, out Vector3 stand)
    {
        // A common disk intersection contains a center or a pair of boundary
        // intersections. The minimum enclosing center gives the largest slack.
        // Only validated, walkable candidates are admitted; no extra probe leg.
        var positions = points.Select(p => Xz(p.Position)).ToArray();
        var options = new List<Vector2> { EnclosingCenter(positions), positions.Aggregate(Vector2.Zero, (a, b) => a + b) / positions.Length };
        options.AddRange(positions);
        for (int i = 0; i < positions.Length; i++)
            for (int j = i + 1; j < positions.Length; j++)
            {
                var delta = positions[j] - positions[i];
                var length = delta.Length();
                if (length <= 1e-6f || length > 2 * PlanningRadius) continue;
                var midpoint = (positions[i] + positions[j]) / 2;
                var offset = new Vector2(-delta.Y, delta.X) / length *
                    MathF.Sqrt(MathF.Max(0, PlanningRadius * PlanningRadius - length * length / 4));
                options.Add(midpoint); options.Add(midpoint + offset); options.Add(midpoint - offset);
            }
        foreach (var option in options.OrderBy(c => positions.Max(p => Vector2.DistanceSquared(c, p)))
                     .ThenBy(c => c.X).ThenBy(c => c.Y))
        {
            var position = new Vector3(option.X, points[0].Position.Y, option.Y);
            if (IsFinite(position) && points.All(p =>
                    Math.Abs(p.Position.Y - position.Y) <= BlindHoardProbeGroup.HeightTolerance &&
                    Vector2.DistanceSquared(option, Xz(p.Position)) <= PlanningRadius * PlanningRadius) &&
                canStand(points[0].Room, position))
            { stand = position; return true; }
        }
        stand = default; return false;
    }

    private static Vector2 EnclosingCenter(Vector2[] points)
    {
        var center = points[0]; var radius2 = 0f;
        for (int i = 1; i < points.Length; i++)
        {
            if (Vector2.DistanceSquared(center, points[i]) <= radius2 + 1e-6f) continue;
            center = points[i]; radius2 = 0;
            for (int j = 0; j < i; j++)
            {
                if (Vector2.DistanceSquared(center, points[j]) <= radius2 + 1e-6f) continue;
                center = (points[i] + points[j]) / 2;
                radius2 = Vector2.DistanceSquared(center, points[i]);
                for (int k = 0; k < j; k++)
                {
                    if (Vector2.DistanceSquared(center, points[k]) <= radius2 + 1e-6f) continue;
                    var a = points[j] - points[i]; var b = points[k] - points[i];
                    var cross = 2 * (a.X * b.Y - a.Y * b.X);
                    if (Math.Abs(cross) < 1e-6f) continue;
                    var relative = new Vector2(b.Y * a.LengthSquared() - a.Y * b.LengthSquared(),
                        a.X * b.LengthSquared() - b.X * a.LengthSquared()) / cross;
                    center = points[i] + relative;
                    radius2 = Vector2.DistanceSquared(center, points[i]);
                }
            }
        }
        return center;
    }

    private static Vector2 Xz(Vector3 p) => new(p.X, p.Z);
    internal static bool IsFinite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
