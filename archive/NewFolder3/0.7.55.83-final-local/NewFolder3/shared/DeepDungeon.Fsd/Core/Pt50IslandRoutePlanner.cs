using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct Pt50RockPoint(int Rock, Vector2 Position);
public readonly record struct Pt50DangerCircle(Vector2 Center, float Radius, DateTime ResolvesAt);
public sealed record Pt50Route(IReadOnlyList<Pt50RockPoint> Steps, double TravelSeconds, float Clearance);

/// <summary>Plans complete island hops against a stationary first circle and 5y pursuit steps.</summary>
public static class Pt50IslandRoutePlanner
{
    // Source action comments give 6y; conservatively use that instead of the generic module's 5y model.
    public const float ChaseRadius = 6f;
    public const float HitMargin = 0.6f;
    public const double Interval = 2.1;
    public const double TimingMargin = 0.25;
    public const float RockMargin = 0.4f;
    public const float ArrivalRadius = 0.18f;
    public static IReadOnlyList<Pt50RockPoint> Points { get; } = BuildPoints();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Vector2, Vector2), double> TravelCache = new();

    public static int RockAt(Vector2 p, float margin = 0)
    {
        for (int i = 0; i < Pt50Arena.Rocks.Length; ++i)
            if (Inside(Pt50Arena.Rocks[i], p) && EdgeDistance(Pt50Arena.Rocks[i], p) >= margin) return i;
        return -1;
    }

    public static bool Inside(Vector2[] polygon, Vector2 p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            var a = polygon[i]; var b = polygon[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }

    private static float EdgeDistance(Vector2[] polygon, Vector2 p)
    {
        float best = float.MaxValue;
        for (int i = 0; i < polygon.Length; ++i)
        {
            var a = polygon[i]; var v = polygon[(i + 1) % polygon.Length] - a;
            float t = v.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, v) / v.LengthSquared(), 0, 1) : 0;
            best = Math.Min(best, Vector2.Distance(p, a + t * v));
        }
        return best;
    }

    private static Pt50RockPoint[] BuildPoints()
    {
        var points = new List<Pt50RockPoint>();
        for (int i = 0; i < Pt50Arena.Rocks.Length; ++i)
        {
            var polygon = Pt50Arena.Rocks[i];
            for (float x = polygon.Min(p => p.X); x <= polygon.Max(p => p.X); x += 0.5f)
                for (float z = polygon.Min(p => p.Y); z <= polygon.Max(p => p.Y); z += 0.5f)
                {
                    var p = new Vector2(x, z);
                    if (Inside(polygon, p) && EdgeDistance(polygon, p) >= RockMargin)
                        points.Add(new(i, p));
                }
        }
        return points.ToArray();
    }

    public static Vector2 Pursue(Vector2 previous, Vector2 target)
    {
        var delta = target - previous;
        float length = delta.Length();
        return length > 5f ? previous + delta * (5f / length) : target;
    }

    // Integrate the crossing, including progressive slowdown. A rock clears sinking;
    // the host additionally waits for the actual status-loss acknowledgement.
    public static double TravelTime(Vector2 from, Vector2 to, double initialSinking = 0)
    {
        bool cache = initialSinking == 0;
        if (cache && TravelCache.TryGetValue((from, to), out var cached)) return cached;
        float length = Vector2.Distance(from, to);
        int samples = Math.Max(1, (int)Math.Ceiling(length / 0.2f));
        double time = 0, sinking = initialSinking;
        for (int i = 1; i <= samples; ++i)
        {
            var point = Vector2.Lerp(from, to, (i - 0.5f) / samples);
            bool sand = RockAt(point) < 0;
            if (!sand) sinking = 0;
            double speed = sand ? Math.Max(3, 6 - 0.6 * Math.Floor(sinking)) : 6;
            double dt = length / samples / speed;
            time += dt;
            if (sand && (sinking += dt) >= 3.3) { time = double.PositiveInfinity; break; }
        }
        if (cache)
        {
            if (TravelCache.Count > 200000) TravelCache.Clear();
            TravelCache[(from, to)] = time;
        }
        return time;
    }

    private sealed record Node(Vector2 Position, Vector2 Circle, List<Pt50RockPoint> Steps, double Time, float Clearance);

    public static Pt50Route? Plan(Vector2 player, Vector2 previousCircle, bool firstHit, int remainingHits,
        double untilNextHit, DateTime now, IReadOnlyList<Pt50DangerCircle>? dangers = null, double initialSinking = 0,
        bool reducedArena = false)
    {
        if (remainingHits <= 0 || !float.IsFinite(player.X) || !float.IsFinite(player.Y)) return null;
        var beam = new List<Node> { new(player, previousCircle, [], 0, float.MaxValue) };
        Node? best = null;
        for (int step = 0; step < Math.Min(4, remainingHits); ++step)
        {
            var children = new List<Node>();
            double budget = (step == 0 ? untilNextHit : Interval) - TimingMargin;
            foreach (var node in beam)
            {
                // Keep several landing choices per rock; the farthest edge is not always
                // reachable and a nearer edge can make the following hop impossible.
                var candidates = new List<Node>();
                foreach (var point in Points)
                {
                    var circle = step == 0 && firstHit ? node.Circle : Pursue(node.Circle, point.Position);
                    float clearance = Vector2.Distance(point.Position, circle) - ChaseRadius;
                    if (clearance < HitMargin) continue;
                    double travel = TravelTime(node.Position, point.Position, step == 0 ? initialSinking : 0);
                    if (travel > budget) continue;
                    if (reducedArena && !InsideWindArena(node.Position, point.Position)) continue;
                    var departure = step == 0 ? now : now.AddSeconds(untilNextHit + (step - 1) * Interval);
                    if (!PathAvoids(node.Position, point.Position, departure, travel, dangers)) continue;
                    candidates.Add(new(point.Position, circle, [.. node.Steps, point], node.Time + travel,
                        Math.Min(node.Clearance, clearance)));
                }
                children.AddRange(candidates.GroupBy(n => n.Steps[^1].Rock)
                    .SelectMany(g => g.OrderByDescending(Score).Take(8)));
            }
            if (children.Count == 0) break;
            beam = children.GroupBy(n => string.Join(',', n.Steps.Select(p => p.Rock)))
                .SelectMany(g => g.OrderByDescending(Score).Take(8)).OrderByDescending(Score).Take(2048).ToList();
            best = beam[0];
        }
        return best == null ? null : new(best.Steps, best.Time, best.Clearance);
    }

    private static double Score(Node node) => Math.Min(2, node.Clearance) - 0.12 * node.Time;

    public static string ValidateNextStep(Pt50Route? route, Vector2 player, Vector2 circle, bool firstHit,
        double untilHit, DateTime now, IReadOnlyList<Pt50DangerCircle>? dangers = null,
        double sinking = 0, bool reducedArena = false)
    {
        if (route == null || route.Steps.Count == 0) return "no-reachable-step";
        var next = route.Steps[0].Position;
        if (RockAt(next, RockMargin) < 0) return "not-a-rock";
        if (reducedArena && !InsideWindArena(player, next)) return "wind-arena-or-hole";
        double travel = TravelTime(player, next, sinking);
        if (!PathAvoids(player, next, now, travel, dangers)) return "falling-rock-conflict";
        if (travel + TimingMargin >= untilHit) return "landing-too-late";
        if (!firstHit) circle = Pursue(circle, next);
        return Vector2.Distance(next, circle) < ChaseRadius + HitMargin ? "chase-overlaps-landing" : string.Empty;
    }

    public static Pt50RockPoint? PreparationPoint(Vector2 player, DateTime now, IReadOnlyList<Pt50DangerCircle>? dangers = null,
        bool reducedArena = false, double sinking = 0)
    {
        int currentRock = RockAt(player);
        // Test every point before rejecting an island. A falling rock may cover only
        // its inner edge; moving within the same island avoids another sand crossing.
        return Points.Where(p => (!reducedArena || InsideWindArena(player, p.Position)) &&
                double.IsFinite(TravelTime(player, p.Position, sinking)) &&
                PathAvoids(player, p.Position, now, TravelTime(player, p.Position, sinking), dangers))
            .OrderBy(p => p.Rock == currentRock ? 0 : 1)
            .ThenBy(p => TravelTime(player, p.Position, sinking))
            .Select(p => (Pt50RockPoint?)p).FirstOrDefault();
    }

    public static bool PathAvoids(Vector2 from, Vector2 to, DateTime now, double travel,
        IReadOnlyList<Pt50DangerCircle>? dangers)
    {
        if (dangers == null) return true;
        foreach (var danger in dangers)
        {
            double t = (danger.ResolvesAt - now).TotalSeconds;
            if (t < -0.15) continue;
            var atResolution = Vector2.Lerp(from, to, (float)Math.Clamp(t / Math.Max(0.001, travel), 0, 1));
            if (Vector2.Distance(atResolution, danger.Center) < danger.Radius + HitMargin) return false;
        }
        return true;
    }

    public static Pt50RockPoint? NearestRock(Vector2 player, DateTime now, IReadOnlyList<Pt50DangerCircle>? dangers = null,
        double sinking = 0, bool reducedArena = false)
    {
        return Points.Where(p => (!reducedArena || InsideWindArena(player, p.Position)) &&
                float.IsFinite(p.Position.X) && TravelTime(player, p.Position, sinking) < 3.3 &&
                PathAvoids(player, p.Position, now, TravelTime(player, p.Position, sinking), dangers))
            .OrderBy(p => Vector2.DistanceSquared(player, p.Position)).Select(p => (Pt50RockPoint?)p).FirstOrDefault();
    }

    public static Vector2? WindSandPoint(Vector2 player, DateTime now = default,
        IReadOnlyList<Pt50DangerCircle>? dangers = null)
    {
        // Windraiser reduces the usable arena to 15y and can create a 3y central hole.
        var center = new Vector2(-300, -300);
        Vector2? best = null; float bestDistance = float.MaxValue;
        for (float x = -313.5f; x <= -286.5f; x += 0.5f)
            for (float z = -313.5f; z <= -286.5f; z += 0.5f)
            {
                var p = new Vector2(x, z);
                float radius = Vector2.Distance(p, center);
                if (radius < 4 || radius > 13.8f || !InsideWindArena(player, p) || RockAt(p) >= 0 ||
                    Pt50Arena.Rocks.Any(r => EdgeDistance(r, p) < 0.65f) ||
                    !PathAvoids(player, p, now, TravelTime(player, p), dangers)) continue;
                float distance = Vector2.DistanceSquared(player, p);
                if (distance < bestDistance) { bestDistance = distance; best = p; }
            }
        return best;
    }

    public static bool InsideWindArena(Vector2 from, Vector2 to)
    {
        var center = new Vector2(-300, -300);
        if (Vector2.DistanceSquared(to, center) > 14.4f * 14.4f) return false;
        var v = to - from;
        float t = v.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(center - from, v) / v.LengthSquared(), 0, 1) : 0;
        return Vector2.DistanceSquared(from + t * v, center) >= 3.6f * 3.6f;
    }
}
