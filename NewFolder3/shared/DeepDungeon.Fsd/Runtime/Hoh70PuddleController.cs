using System.Numerics;

namespace DeepDungeon.Fsd.Runtime;

public readonly record struct Hoh70Puddle(ulong Id, Vector3 Position);
public readonly record struct Hoh70Frame(bool Enabled, uint DungeonId, int Floor, bool PlayerAlive,
    bool InCombat, ulong BossId, uint BossBaseId, bool BossAlive, Vector3 Player,
    uint CastActionId, bool ChargeTargetsPlayer, bool Mini, IReadOnlyList<Hoh70Puddle> Puddles);

/// <summary>Kenko only: wait outside until the charge cast, stay small through Devour, then leave.</summary>
public sealed class Hoh70PuddleController(Func<bool, bool> ownership, Action<Vector3> move,
    Action stopInput, Func<Vector3, Vector3?> ground, Func<Vector3, Vector3, bool> clear,
    Action<string> report) : IDisposable
{
    public const uint BossBaseId = 9195, PuddleBaseId = 2005033, MiniStatusId = 438;
    public const uint ChargeActionId = 12206, DevourActionId = 12204, UlulationActionId = 12208;
    private const float OutsideRadius = 4.5f, Clearance = 3.7f;
    private ulong _bossId, _completedPuddle;
    private Hoh70Puddle? _puddle;
    private Vector3? _outside;
    private bool _owns, _awaitDevour;
    private DateTime _started;
    public string Stage { get; private set; } = "inactive";
    public bool OwnsMovement => _owns;
    public Vector3? Destination { get; private set; }

    // Called on the framework thread after draining the native observer queue.
    public void ObserveAction(ulong bossId, uint action)
    {
        if (!_owns || bossId != _bossId || !_awaitDevour || action != DevourActionId) return;
        _awaitDevour = false;
        SetStage("exit-after-devour-event");
    }

    public void Update(Hoh70Frame f, DateTime now)
    {
        if (!f.Enabled || f.DungeonId != 2 || f.Floor != 70 || !f.PlayerAlive || !f.InCombat ||
            !f.BossAlive || f.BossId == 0 || f.BossBaseId != BossBaseId || !Finite(f.Player))
        { Reset(); return; }
        if (_bossId != f.BossId) { Reset(); _bossId = f.BossId; }
        if (_puddle == null)
        {
            var available = f.Puddles.Where(p => p.Id != 0 && p.Id != _completedPuddle && Finite(p.Position))
                .OrderBy(p => Vector3.DistanceSquared(f.Player, p.Position)).ToArray();
            if (available.Length == 0) return;
            _puddle = available[0]; _started = now;
            SetStage("wait-outside-for-charge");
        }
        var puddle = _puddle.Value;
        if (now - _started > TimeSpan.FromSeconds(30))
        { Finish("stale-sequence-returned-to-bmr"); return; }
        // Disappearing puddles outside an active charge are no longer a reason to own movement.
        if (!_awaitDevour && !Stage.StartsWith("exit-") && !f.Puddles.Any(p => p.Id == puddle.Id))
        { Finish("puddle-disappeared"); return; }

        if (f.CastActionId == ChargeActionId && f.ChargeTargetsPlayer &&
            Stage == "wait-outside-for-charge")
        {
            _awaitDevour = true;
            SetStage("enter-on-charge-cast");
        }
        // A later Ululation is also positive evidence the previous Devour has ended.
        // The first Ululation, before Charge, must NEVER authorize entering the puddle.
        if (_awaitDevour && f.CastActionId == UlulationActionId)
        {
            _awaitDevour = false;
            SetStage("exit-on-next-ululation");
        }
        bool entering = _awaitDevour;
        if (entering && f.Mini) SetStage("hold-mini-until-devour");

        if (!entering && Stage.StartsWith("exit-") && !f.Mini && Distance(f.Player, puddle.Position) >= Clearance)
        { Finish("left-puddle-returned-to-bmr"); return; }

        Vector3? destination;
        if (entering)
        {
            // Keep the cached puddle through charge/stun even if its object briefly disappears.
            destination = CheckedPoint(puddle.Position);
        }
        else
        {
            _outside ??= FindOutside(f.Player, puddle.Position);
            destination = _outside;
        }
        if (destination is not { } point || !SegmentGrounded(f.Player, point) || !clear(f.Player, point))
        { Finish("no-validated-puddle-path-returned-to-bmr"); return; }
        if (!_owns)
        {
            if (!ownership(true)) { _puddle = null; _outside = null; _awaitDevour = false; return; }
            _owns = true;
            report("acquired-bmr-movement");
        }
        Destination = point;
        if (Distance(f.Player, point) <= .25f) stopInput();
        else move(point);
    }

    private Vector3? FindOutside(Vector3 player, Vector3 puddle)
    {
        var direction = player - puddle; direction.Y = 0;
        if (direction.LengthSquared() < .01f) direction = new(1, 0, 0);
        direction = Vector3.Normalize(direction);
        Vector3? best = null; float score = float.MaxValue;
        for (int i = 0; i < 32; i++)
        {
            float angle = i * MathF.Tau / 32;
            var dir = new Vector3(direction.X * MathF.Cos(angle) - direction.Z * MathF.Sin(angle), 0,
                direction.X * MathF.Sin(angle) + direction.Z * MathF.Cos(angle));
            var candidate = CheckedPoint(puddle + dir * OutsideRadius);
            if (candidate is not { } point || !SegmentGrounded(player, point) || !clear(player, point)) continue;
            // Never cut across the shrinking puddle while approaching an outside waiting point.
            if (Distance(player, puddle) >= Clearance && SegmentDistance(player, point, puddle) < Clearance) continue;
            float cost = Vector3.DistanceSquared(player, point);
            if (cost < score) { score = cost; best = point; }
        }
        return best;
    }
    private Vector3? CheckedPoint(Vector3 seed)
    {
        var point = ground(seed);
        return point is { } p && Finite(p) && Vector3.DistanceSquared(seed, p) <= .09f ? p : null;
    }
    private bool SegmentGrounded(Vector3 from, Vector3 to)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(from, to) / .5f));
        if (steps > 160) return false;
        for (int i = 0; i <= steps; i++) if (CheckedPoint(Vector3.Lerp(from, to, i / (float)steps)) == null) return false;
        return true;
    }
    private static float SegmentDistance(Vector3 a, Vector3 b, Vector3 p)
    {
        var av = new Vector2(a.X, a.Z); var bv = new Vector2(b.X, b.Z); var pv = new Vector2(p.X, p.Z);
        var delta = bv - av; float sq = delta.LengthSquared();
        return Vector2.Distance(pv, av + delta * (sq > .001f ? Math.Clamp(Vector2.Dot(pv - av, delta) / sq, 0, 1) : 0));
    }
    private static float Distance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private void SetStage(string value) { if (Stage != value) { Stage = value; report(value); } }
    private void Finish(string reason)
    {
        var completed = _puddle?.Id ?? _completedPuddle;
        Release(); _puddle = null; _outside = null; _awaitDevour = false;
        _completedPuddle = completed; SetStage(reason);
    }
    private void Release()
    {
        Destination = null;
        if (!_owns) return;
        try { stopInput(); }
        finally { _owns = false; ownership(false); }
    }
    public void Reset()
    {
        Release(); _puddle = null; _outside = null; _awaitDevour = false;
        _bossId = _completedPuddle = 0; _started = default; Stage = "inactive";
    }
    public void Dispose() => Reset();
}
