using System.Numerics;

namespace DeepDungeon.Fsd.Runtime;

public readonly record struct Hoh90Cast(ulong ActorId, uint BaseId, uint ActionId, uint Sequence,
    // Native Character.CastRotation, never CastInfo.Rotation or target-ring positionals.
    Vector3 Location, Vector3 Position, float Rotation, float Remaining);
public readonly record struct Hoh90Sphere(ulong Id, Vector3 Position);
public readonly record struct Hoh90Frame(bool Enabled, uint DungeonId, int Floor, bool PlayerAlive,
    bool InCombat, ulong BossId, uint BossBaseId, bool BossAlive, Vector3 Player, Vector3 ArenaCenter,
    IReadOnlyList<Hoh90Cast> Casts, IReadOnlyList<Hoh90Sphere> Spheres, Vector3? BossPosition = null,
    Vector3? BmrDestination = null, bool? BmrNavigating = null);
public readonly record struct Hoh90Danger(Vector3 Position, DateTime At, float Radius,
    float Rotation = 0, bool Rectangle = false);
public readonly record struct Hoh90Step(Vector3 Position, DateTime Arrival, bool Wait = false);
public sealed record Hoh90Route(IReadOnlyList<Hoh90Step> Steps, float DistanceFromMeteor, double Eta, string Reason);

/// <summary>Historical replay model, retained only in the test assembly after the .69 rollback.</summary>
public sealed class Hoh90MeteorController(Func<bool, bool> ownership, Action<Vector3> move,
    Action stopInput, Func<Vector3, Vector3?> ground, Func<Vector3, Vector3, bool> clear,
    Action<string> report) : IDisposable
{
    public const uint BossBaseId = 9199, SphereBaseId = 9235, HelperBaseId = 8865;
    public const uint VisualActionId = 12211, MeteorActionId = 12212, SubductionActionId = 12210;
    public const uint BurningRaveActionId = 12213, AncientQuagaActionId = 12214,
        AuraCannonActionId = 12215, KnucklePressActionId = 12216;
    public const float HandoffLeadSeconds = 1.5f, EdgeRadius = 23.5f;
    private readonly Hoh90MeteorPlanner _geometry = new(ground, clear);
    private readonly Dictionary<(ulong, uint), Hoh90Danger> _dangers = new();
    private ulong _boss, _visualActor, _meteorActor, _completedActor;
    private uint _visualSequence, _meteorSequence, _completedSequence;
    private bool _visualPresent, _escapeActive, _owns, _leaseRequested, _moving;
    private Vector3? _visualOrigin;
    private DateTime _impactAt, _expiresAt, _lastProgress;
    private Vector3 _lastPlayer;
    private Hoh90Route? _route;
    public bool UrgentMovement => _owns && _moving;
    public bool SuppressOutput => false;
    public bool OwnsMovement => _owns;
    public string Stage { get; private set; } = "inactive";
    public string RouteReason { get; private set; } = "none";
    public string HandoffEvidence { get; private set; } = "none";
    public Vector3? MeteorOrigin { get; private set; }
    public Vector3? PlanningOrigin => MeteorOrigin ?? _visualOrigin;
    public Vector3? EscapeEdge { get; private set; }
    public Vector3? Destination => _owns ? EscapeEdge : null;
    public bool ReachedEdge { get; private set; }
    public DateTime? HandoffDeadline { get; private set; }
    public DateTime ImpactAt => _impactAt;
    public double Eta => _route?.Eta ?? 0;
    public IReadOnlyList<Hoh90Step> Route => _route?.Steps ?? [];
    public bool CompleteMeteorRoute => MeteorOrigin is { } m && EscapeEdge is { } e &&
        Vector2.Distance(Hoh90MeteorPlanner.XZ(m), Hoh90MeteorPlanner.XZ(e)) >= Hoh90MeteorPlanner.MinimumMeteorDistance;

    public void ObserveAction(ulong actor, uint action, uint sequence)
    {
        _dangers.Remove((actor, action));
        if (action != MeteorActionId || _meteorActor == 0 || actor != _meteorActor) return;
        _completedActor = _meteorActor; _completedSequence = _meteorSequence;
        if (_escapeActive) Finish("meteor-effect-observed-returned-to-bmr");
        MeteorOrigin = null; _meteorActor = 0;
    }

    public void Update(Hoh90Frame f, DateTime now)
    {
        if (!f.Enabled || f.DungeonId != 2 || f.Floor != 90 || !f.PlayerAlive || !f.InCombat ||
            !f.BossAlive || f.BossBaseId != BossBaseId || f.BossId == 0 ||
            !Hoh90MeteorPlanner.Finite(f.Player) || !Hoh90MeteorPlanner.Finite(f.ArenaCenter))
        { Reset(); return; }
        if (_boss != f.BossId) { Reset(); _boss = f.BossId; }
        var visual = f.Casts.FirstOrDefault(c => c.ActorId == f.BossId && c.BaseId == BossBaseId &&
            c.ActionId == VisualActionId && float.IsFinite(c.Remaining) && c.Remaining is > 0 and <= 3.5f);
        bool newVisual = visual.ActorId != 0 && (!_visualPresent || visual.ActorId != _visualActor || visual.Sequence != _visualSequence);
        _visualPresent = visual.ActorId != 0;
        if (newVisual)
        {
            Release(); _dangers.Clear(); MeteorOrigin = null; _meteorActor = 0;
            _visualActor = visual.ActorId; _visualSequence = visual.Sequence;
            _visualOrigin = visual.Position; EscapeEdge = null; _route = null; ReachedEdge = false; HandoffDeadline = null;
            _escapeActive = true; _expiresAt = now.AddSeconds(visual.Remaining + 10);
            _impactAt = _expiresAt; _lastProgress = now; _lastPlayer = f.Player;
            // "Opposite edge" is across the arena from the player's cast-start position.
            // A boss-relative ray can instead graze a nearby sphere on the recorded .65 pull.
            var away = Hoh90MeteorPlanner.XZ(f.ArenaCenter - f.Player);
            if (away.LengthSquared() < .25f) away = Hoh90MeteorPlanner.XZ(f.ArenaCenter - visual.Position);
            if (away.LengthSquared() < .25f && float.IsFinite(visual.Rotation))
                away = -new Vector2(MathF.Sin(visual.Rotation), MathF.Cos(visual.Rotation));
            if (!Hoh90MeteorPlanner.Finite(visual.Position) || !float.IsFinite(away.LengthSquared()) || away.LengthSquared() < .01f)
            { Finish("invalid-visual-direction-retaining-bmr"); return; }
            away = Vector2.Normalize(away);
            var seed = f.ArenaCenter + new Vector3(away.X, 0, away.Y) * EdgeRadius;
            var edge = ground(seed);
            _geometry.SetArena(f.ArenaCenter);
            if (edge is not { } target || !Hoh90MeteorPlanner.Finite(target) || Vector3.DistanceSquared(target, seed) > .16f ||
                !_geometry.CurrentEdgeClear(f.Player, target))
            { Finish("opposite-edge-path-unavailable-retaining-bmr"); return; }
            EscapeEdge = target;
            double eta = Vector3.Distance(f.Player, target) / Hoh90MeteorPlanner.Speed;
            _route = new([new(target, now.AddSeconds(eta))], Vector3.Distance(visual.Position, target), eta, "opposite-edge-on-visual-cast");
            RouteReason = _route.Reason;
            report($"visual-start-immediate-far-edge-escape, edge={target}, eta={eta:F2}, bmrLead={HandoffLeadSeconds:F1}s");
        }
        var meteor = f.Casts.FirstOrDefault(c => c.BaseId == HelperBaseId && c.ActionId == MeteorActionId &&
            float.IsFinite(c.Remaining) && c.Remaining is > 0 and < 8 &&
            !(c.ActorId == _completedActor && c.Sequence == _completedSequence));
        if (meteor.ActorId != 0 && Hoh90MeteorPlanner.Finite(meteor.Location) &&
            Vector2.Distance(Hoh90MeteorPlanner.XZ(meteor.Location), Hoh90MeteorPlanner.XZ(f.ArenaCenter)) < 26 &&
            (_meteorActor != meteor.ActorId || _meteorSequence != meteor.Sequence))
        {
            _meteorActor = meteor.ActorId; _meteorSequence = meteor.Sequence; MeteorOrigin = meteor.Location;
            _impactAt = now.AddSeconds(meteor.Remaining + .3); _expiresAt = _impactAt.AddSeconds(2);
            report($"locked-helper={_meteorActor}, origin={meteor.Location}, impact={_impactAt:o}");
        }
        if (!_escapeActive)
        {
            Release();
            if (Stage == "inactive") { HandoffEvidence = "ordinary-phase-no-local-movement-override"; SetStage("ordinary-mechanics-owned-by-bmr"); }
            return;
        }
        if (now >= _expiresAt) { Finish("meteor-effect-missing-timeout-returned-to-bmr"); return; }
        if (EscapeEdge is not { } goal) { Finish("missing-opposite-edge-retaining-bmr"); return; }
        ReachedEdge = Vector2.Distance(Hoh90MeteorPlanner.XZ(f.Player), Hoh90MeteorPlanner.XZ(goal)) <= .5f;
        ObserveDangers(f, now);
        HandoffDeadline = null;
        // Only actual readbars establish danger timing, never sphere birth or a repeating pulse guess.
        foreach (var danger in _dangers.Values.OrderBy(d => d.At))
        {
            double seconds = (danger.At - now).TotalSeconds;
            if (seconds < -.1 || seconds > 4) continue;
            var delta = goal - f.Player; float distance = delta.Length();
            var projected = distance <= .001f ? f.Player : f.Player + delta / distance * Math.Min(distance, (float)Math.Max(0, seconds) * Hoh90MeteorPlanner.Speed);
            if (_geometry.SegmentSafe(f.Player, projected, now, danger.At > now ? danger.At : now,
                [danger], f.ArenaCenter, DateTime.MaxValue.AddDays(-1))) continue;
            var deadline = danger.At.AddSeconds(-HandoffLeadSeconds - .3);
            HandoffDeadline = deadline;
            if (now < deadline) break;
            report($"early-bmr-handoff-before-actual-threat, lead={seconds:F2}, reachedEdge={ReachedEdge}, meteorDistance={DistanceFromOrigin(f.Player):F2}");
            Finish("actual-threat-margin-returned-to-bmr"); return;
        }
        if (ReachedEdge && MeteorOrigin != null && (_impactAt - now).TotalSeconds <= 1.3)
        {
            // Keep a travel buffer: BMR can immediately move toward the boss once released.
            float buffer = 6 * (float)Math.Max(0, (_impactAt - now).TotalSeconds + .25);
            if (DistanceFromOrigin(f.Player) - buffer >= Hoh90MeteorPlanner.MinimumMeteorDistance)
            { Finish("far-edge-late-meteor-cast-returned-to-bmr"); return; }
        }
        if (!_owns)
        {
            _leaseRequested = true;
            if (!ownership(true)) { SetStage("bmr-handoff-pending-or-rejected"); return; }
            _owns = true; _lastProgress = now; _lastPlayer = f.Player;
            HandoffEvidence = "local-lease-after-bmr-navigation-quiescence";
        }
        if (ReachedEdge)
        {
            stopInput(); _moving = false;
            SetStage("far-edge-brief-hold-awaiting-safe-bmr-handoff");
        }
        else
        {
            _moving = true; move(goal); SetStage("immediate-opposite-edge-escape");
            if (Vector2.DistanceSquared(Hoh90MeteorPlanner.XZ(f.Player), Hoh90MeteorPlanner.XZ(_lastPlayer)) > .04f)
            { _lastProgress = now; _lastPlayer = f.Player; }
            else if (now - _lastProgress > TimeSpan.FromSeconds(2))
            { Finish("opposite-edge-movement-stalled-returned-to-bmr"); }
        }
    }
    private void ObserveDangers(Hoh90Frame f, DateTime now)
    {
        foreach (var c in f.Casts.Where(c => float.IsFinite(c.Remaining) && c.Remaining is > 0 and < 8))
        {
            var at = now.AddSeconds(c.Remaining + .3);
            Hoh90Danger? danger = c.BaseId == SphereBaseId && c.ActionId == SubductionActionId
                ? new(c.Position, at, 9.6f) : c.BaseId != BossBaseId ? null : c.ActionId switch {
                    BurningRaveActionId => new(c.Location, at, 8.6f),
                    KnucklePressActionId => new(c.Position, at, 9.6f),
                    AuraCannonActionId when float.IsFinite(c.Rotation) => new(c.Position, at, 5.6f, c.Rotation, true),
                    AuraCannonActionId => new(c.Position, at, 63.6f), _ => null };
            if (danger is { } d && Hoh90MeteorPlanner.Finite(d.Position)) _dangers[(c.ActorId, c.ActionId)] = d;
        }
        foreach (var key in _dangers.Keys.Where(k => _dangers[k].At < now.AddSeconds(-.6)).ToArray()) _dangers.Remove(key);
    }
    private float DistanceFromOrigin(Vector3 player) => PlanningOrigin is { } origin
        ? Vector2.Distance(Hoh90MeteorPlanner.XZ(player), Hoh90MeteorPlanner.XZ(origin)) : 0;
    private void Finish(string stage)
    { _escapeActive = false; Release(); HandoffEvidence = stage; SetStage(stage); }
    private void SetStage(string value) { if (Stage != value) { Stage = value; report(value); } }
    private void Release()
    {
        _moving = false;
        if (!_owns && !_leaseRequested) return;
        try { stopInput(); } finally { _owns = false; _leaseRequested = !ownership(false); }
    }
    public void Reset()
    {
        Release(); _geometry.Reset(); _dangers.Clear(); _route = null;
        _boss = _visualActor = _meteorActor = _completedActor = 0;
        _visualSequence = _meteorSequence = _completedSequence = 0;
        _visualPresent = _escapeActive = ReachedEdge = false;
        MeteorOrigin = _visualOrigin = EscapeEdge = null; HandoffDeadline = null;
        _impactAt = _expiresAt = _lastProgress = default;
        Stage = "inactive"; RouteReason = "none"; HandoffEvidence = "none";
    }
    public void Dispose() => Reset();
}

