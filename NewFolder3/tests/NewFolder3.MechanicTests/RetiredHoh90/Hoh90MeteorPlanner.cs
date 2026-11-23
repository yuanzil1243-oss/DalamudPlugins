using System.Diagnostics;
using System.Numerics;

namespace DeepDungeon.Fsd.Runtime;

/// <summary>Historical replay search, retained only in the test assembly after the .69 rollback.</summary>
public sealed class Hoh90MeteorPlanner(Func<Vector3, Vector3?> ground, Func<Vector3, Vector3, bool> clear)
{
    public const float Speed = 5.5f, ArenaRadius = 23.5f, MinimumMeteorDistance = 20.5f;
    private readonly Dictionary<(Vector3, Vector3), bool> _edges = new();
    private readonly Dictionary<Vector3, Vector3?> _points = new();
    private Vector3 _center;
    public string LastRejection { get; private set; } = "none";
    public static Vector2 XZ(Vector3 p) => new(p.X, p.Z);
    public static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    public void Reset() { _edges.Clear(); _points.Clear(); _center = default; }
    public void SetArena(Vector3 center) { if (_center != center) { Reset(); _center = center; } }

    public Hoh90Route? Plan(Vector3 player, Vector3 center, Vector3 meteor, DateTime impact,
        IReadOnlyList<Hoh90Danger> dangers, DateTime now, float preferredDistance = 22,
        float minimumDistance = MinimumMeteorDistance, bool enforceMeteor = true, bool requireProgress = false)
    {
        LastRejection = "no-safe-time-window-or-route";
        if (!Finite(player) || !Finite(center) || !Finite(meteor) || (impact - now).TotalSeconds <= .75 ||
            Vector2.Distance(XZ(player), XZ(center)) > 25.25f) return null;
        SetArena(center);
        var nodes = new List<Vector3> { player };
        var damageAt = enforceMeteor ? impact : DateTime.MaxValue.AddDays(-1);
        // The 1.5m arena margin also excludes the entrance strip and the inscribed polygon boundary.
        for (int x = -7; x <= 7; x++) for (int z = -7; z <= 7; z++)
        {
            var seed = center + new Vector3(x * 3, 0, z * 3);
            if (Vector2.Distance(XZ(seed), XZ(center)) > ArenaRadius) continue;
            if (!_points.TryGetValue(seed, out var point)) _points[seed] = point = Checked(seed);
            if (point is { } p) nodes.Add(p);
        }
        var frontier = new PriorityQueue<(int Node, int Slot), float>();
        var start = (Node: 0, Slot: 0);
        var arrivals = new Dictionary<(int Node, int Slot), float> { [start] = 0 };
        var previous = new Dictionary<(int Node, int Slot), (int Node, int Slot)>();
        frontier.Enqueue(start, 0);
        float deadline = (float)(impact - now).TotalSeconds - .75f;
        (int Node, int Slot)? fallback = null;
        var timer = Stopwatch.StartNew();
        int expanded = 0;
        while (frontier.TryDequeue(out var state, out var priority))
        {
            if (++expanded > 14000 || timer.ElapsedMilliseconds > 45)
            { LastRejection = "planner-budget-exhausted-cached-geometry-for-retry"; break; }
            float elapsed = arrivals[state];
            if (priority > elapsed + Heuristic(nodes[state.Node], meteor, preferredDistance) + .001f) continue;
            var from = nodes[state.Node]; float distance = Vector2.Distance(XZ(from), XZ(meteor));
            bool progressed = !requireProgress || Vector2.DistanceSquared(XZ(player), XZ(from)) >= 1;
            if (progressed && distance >= minimumDistance && SegmentSafe(from, from, now.AddSeconds(elapsed),
                    impact.AddSeconds(.6), dangers, meteor, damageAt))
            {
                if (distance >= preferredDistance) return Build(state, distance,
                    enforceMeteor ? "preferred-22m-plus" : "provisional-progress-away-from-visual", arrivals, previous, nodes, now);
                if (fallback == null || requireProgress && distance > Vector2.Distance(XZ(nodes[fallback.Value.Node]), XZ(meteor)) + .1f)
                    fallback = state;
            }
            for (int i = 0; i < nodes.Count; i++)
            {
                var to = nodes[i]; float length = Vector3.Distance(from, to);
                if (i != state.Node && (length < .1f || (state.Node != 0 && length > 4.3f) ||
                    state.Node == 0 && length > 7)) continue;
                float dt = i == state.Node ? .25f : length / Speed + .025f;
                float at = elapsed + dt;
                if (at > deadline) continue;
                var next = (Node: i, Slot: (int)MathF.Ceiling(at * 4));
                if (arrivals.TryGetValue(next, out var known) && known <= at) continue;
                if (!SegmentSafe(from, to, now.AddSeconds(elapsed), now.AddSeconds(at), dangers, meteor, damageAt)) continue;
                if (i != state.Node && !Geometry(from, to)) continue;
                arrivals[next] = at; previous[next] = state;
                frontier.Enqueue(next, at + Heuristic(to, meteor, preferredDistance));
            }
        }
        if (fallback is { } f)
            return Build(f, Vector2.Distance(XZ(nodes[f.Node]), XZ(meteor)),
                requireProgress ? "temporary-progress-not-complete-meteor-escape" : "minimum-20.5m-plus", arrivals, previous, nodes, now);
        return null;
    }
    private static float Heuristic(Vector3 p, Vector3 meteor, float goal) => Math.Max(0, goal - Vector2.Distance(XZ(p), XZ(meteor))) / Speed;
    private static Hoh90Route Build((int Node, int Slot) state, float distance, string reason,
        Dictionary<(int Node, int Slot), float> arrivals, Dictionary<(int Node, int Slot), (int Node, int Slot)> previous,
        List<Vector3> nodes, DateTime now)
    {
        float eta = arrivals[state]; var steps = new List<Hoh90Step>();
        while (true)
        {
            steps.Add(new(nodes[state.Node], now.AddSeconds(arrivals[state]), previous.TryGetValue(state, out var before) && before.Node == state.Node));
            if (!previous.TryGetValue(state, out var prev)) break;
            state = prev;
        }
        steps.Reverse();
        if (steps.Count > 1) steps.RemoveAt(0);
        return new(steps, distance, eta, reason);
    }
    public bool SegmentSafe(Vector3 from, Vector3 to, DateTime start, DateTime end,
        IReadOnlyList<Hoh90Danger> dangers, Vector3 meteor, DateTime impact)
    {
        double seconds = Math.Max(0, (end - start).TotalSeconds);
        int samples = Math.Max(1, (int)Math.Ceiling(seconds / .08));
        if (samples > 250 || !Finite(from) || !Finite(to)) return false;
        for (int i = 0; i <= samples; i++)
        {
            float t = i / (float)samples; var p = Vector3.Lerp(from, to, t);
            var at = start.AddSeconds(seconds * t);
            if (at >= impact.AddSeconds(-.65) && Vector2.Distance(XZ(p), XZ(meteor)) < MinimumMeteorDistance) return false;
            foreach (var danger in dangers)
            {
                if (Math.Abs((at - danger.At).TotalSeconds) > .55) continue;
                var delta = XZ(p - danger.Position);
                if (!danger.Rectangle)
                { if (delta.LengthSquared() < danger.Radius * danger.Radius) return false; }
                else
                {
                    var forward = new Vector2(MathF.Sin(danger.Rotation), MathF.Cos(danger.Rotation));
                    float along = Vector2.Dot(delta, forward);
                    float side = Math.Abs(delta.X * forward.Y - delta.Y * forward.X);
                    if (along > -.6f && along < 63.6f && side < danger.Radius) return false;
                }
            }
        }
        return true;
    }
    public bool CurrentEdgeClear(Vector3 from, Vector3 to) => Finite(from) && Finite(to) && Geometry(from, to);
    private Vector3? Checked(Vector3 seed)
    {
        var p = ground(seed);
        return p is { } v && Finite(v) && Vector3.DistanceSquared(seed, v) <= .16f ? v : null;
    }
    private bool Geometry(Vector3 a, Vector3 b)
    {
        if (_edges.TryGetValue((a, b), out var cached)) return cached;
        bool valid = clear(a, b);
        if (valid)
        {
            int count = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(a, b) / .6f));
            for (int i = 0; i <= count; i++)
            {
                var p = Vector3.Lerp(a, b, i / (float)count);
                // A player at the real arena edge can re-enter the conservative planning margin.
                // Convex straight edges may not move farther outside than their existing endpoint.
                float limit = Math.Max(ArenaRadius + .25f, Math.Max(Vector2.Distance(XZ(a), XZ(_center)), Vector2.Distance(XZ(b), XZ(_center))));
                if (Vector2.Distance(XZ(p), XZ(_center)) > limit + .01f || Checked(p) == null)
                { valid = false; break; }
            }
        }
        // Moving player endpoints are not stable cache keys; bound the cache for long fights.
        if (_edges.Count > 12000) _edges.Clear();
        _edges[(a, b)] = valid; _edges[(b, a)] = valid;
        return valid;
    }
}
