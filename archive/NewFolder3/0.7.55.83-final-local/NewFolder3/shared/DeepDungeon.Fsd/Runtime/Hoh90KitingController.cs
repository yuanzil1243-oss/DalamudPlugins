using System.Numerics;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public readonly record struct Hoh90KitingSphere(Vector3 Position, float Radius);
public readonly record struct Hoh90MeteorCast(ulong Caster, Vector3 Origin, float Remaining);
public readonly record struct Hoh90MechanicResolution(DateTime At, ulong Caster, uint BaseId,
    uint Action, uint Sequence, Vector3 Origin);
public readonly record struct Hoh90KitingFrame(DeepDungeonStateSnapshot State, FarmingMode? Mode,
    bool RunActive, bool PlayerReady, bool InCombat, bool BmrConfigured, bool ManualOrbit,
    ulong BossId, uint BossBaseId, bool BossAlive, Vector3 Player, Vector3 Boss, Vector3 Arena,
    float BossRadius, float PlayerRadius, bool BossCasting, uint Action, float Remaining,
    IReadOnlyList<Hoh90KitingSphere> Spheres)
{
    public Vector3? CastLocation { get; init; }
    public IReadOnlyList<Hoh90MeteorCast> Meteors { get; init; } = [];
    public IReadOnlyList<Hoh90MechanicResolution> Resolutions { get; init; } = [];
    public bool MechanicObserverAvailable { get; init; }
}

/// <summary>HoH90 survival movement. Never controls output, items, targeting or another dungeon.</summary>
public sealed class Hoh90KitingController(Func<bool, bool> ownership, Action<Vector3> move,
    Action stop, Func<Vector3, Vector3, bool> routeSafe) : IDisposable
{
    public const uint BossBaseId = 9199, MeteorVisual = 12211;
    public const float ArenaRadius = 23f, PreferredDistance = 16f, MeteorDistance = 20.5f;
    private ulong _boss;
    private uint _retreatAction;
    private bool _leaseRequested, _owns, _retreatCast, _castDelegated;
    private DateTime _nextTick, _yieldUntil, _leaseStarted, _lastProgress;
    private Vector3 _lastPlayer, _castOrigin;
    private Vector3? _castGoal;
    private int _direction = 1;
    private bool _meteorActive, _meteorVisualLatched, _meteorTimedOut;
    private DateTime _meteorStarted;
    private ulong _meteorHelper;
    private Vector3 _meteorOrigin, _meteorArena;
    private Vector2 _meteorAway;
    private Vector3? _meteorGoal;
    public string Stage { get; private set; } = "inactive";
    public Vector3? Destination { get; private set; }
    public bool OwnsMovement => _owns;
    public float Distance { get; private set; }
    public string? LastFailure { get; private set; }
    public bool MeteorPending => _meteorActive;
    public ulong MeteorHelper => _meteorHelper;
    public Vector3? MeteorOrigin => _meteorActive ? _meteorOrigin : null;
    public Vector3? MeteorGoal => _meteorGoal;

    public void Update(in Hoh90KitingFrame f, DateTime now)
    {
        if (!Eligible(f)) { Reset(); return; }
        if (_boss != f.BossId) { Reset(); _boss = f.BossId; }
        Distance = XZDistance(f.Player, f.Boss);
        bool visual = f.BossCasting && f.Action == MeteorVisual && f.Remaining > 0;
        if (visual && !_meteorVisualLatched)
            BeginMeteor(f, f.CastLocation ?? f.Boss, now);
        _meteorVisualLatched = visual;
        // A helper may become visible between ticks, or after attaching mid-mechanic.
        if (!_meteorActive)
            foreach (var helper in f.Meteors)
                if (!f.Resolutions.Any(e => e.Action == 12212 && e.Caster == helper.Caster))
                { BeginMeteor(f, helper.Origin, now); break; }
        if (_meteorActive) { UpdateMeteor(f, now); return; }
        bool retreat = f.BossCasting && f.Remaining > 0;
        // Cast boundaries are handled before the movement throttle: never retain stale input.
        if (_retreatCast && !retreat)
        {
            _retreatCast = _castDelegated = false; _castGoal = null;
            Yield(now, "boss-cast-ended-bmr-pursuit"); return;
        }
        if (_retreatCast && XZDistance(f.Boss, _castOrigin) > .75f)
        {
            _castDelegated = true;
            Yield(now, "boss-moved-after-readbar-bmr"); return;
        }
        if (retreat && (!_retreatCast || _retreatAction != f.Action))
        {
            _retreatCast = true; _retreatAction = f.Action; _castDelegated = false; _castOrigin = f.Boss;
            _castGoal = null; _nextTick = default;
        }
        if (_castDelegated) { Release(); Stage = "cast-no-safe-retreat-bmr"; return; }
        if (now < _yieldUntil) { Release(); return; }
        if (now < _nextTick) return;
        _nextTick = now.AddMilliseconds(150);
        if (_owns && !TryOwnership(true))
        { Yield(now, "bmr-navigation-resumed-release-local-input"); return; }

        Vector3? goal;
        if (retreat)
        {
            if (_castGoal is not { } locked || !RetreatPointSafe(locked, f) ||
                !SafeRoute(f.Player, locked))
                _castGoal = FindRetreatGoal(f);
            goal = _castGoal;
            if (goal == null)
            {
                _castDelegated = true;
                Yield(now, "cast-no-safe-retreat-bmr"); return;
            }
        }
        else goal = FindKitingStep(f);
        if (goal is not { } destination)
        { Yield(now, "no-safe-kiting-route-bmr"); return; }
        Destination = destination;
        if (!_leaseRequested) { _leaseRequested = true; _leaseStarted = now; }
        if (!_owns)
        {
            if (!TryOwnership(true))
            {
                Stage = "awaiting-bmr-navigation-stop";
                if (now - _leaseStarted >= TimeSpan.FromSeconds(1))
                    Yield(now, "movement-lease-timeout-bmr");
                return;
            }
            _owns = true; _lastProgress = now; _lastPlayer = f.Player;
        }
        if (XZDistance(f.Player, _lastPlayer) >= .3f)
        { _lastProgress = now; _lastPlayer = f.Player; }
        if (retreat && XZDistance(f.Player, destination) <= .5f)
        { stop(); Stage = "boss-cast-safe-distant-position"; return; }
        if (now - _lastProgress >= TimeSpan.FromSeconds(2))
        { Yield(now, "no-movement-progress-bmr"); return; }
        move(destination);
        Stage = retreat ? "boss-cast-retreat-away-from-boss-and-spheres" : "continuous-distance-kiting";
    }

    private void BeginMeteor(in Hoh90KitingFrame f, Vector3 origin, DateTime now)
    {
        Release(); _retreatCast = _castDelegated = false; _castGoal = null;
        _meteorActive = true; _meteorTimedOut = false; _meteorStarted = now; _meteorHelper = 0;
        _meteorArena = f.Arena; _meteorOrigin = Finite(origin) && XZDistance(origin, f.Arena) <= 27 ? origin : f.Boss;
        var radial = XZ(_meteorOrigin - _meteorArena);
        // A central boss has no unique near half; preserve the player's outward side.
        _meteorAway = radial.LengthSquared() >= 1 ? -Vector2.Normalize(radial) : XZ(f.Player - f.Arena);
        if (_meteorAway.LengthSquared() < .01f) _meteorAway = new(1, 0);
        _meteorAway = Vector2.Normalize(_meteorAway);
        _meteorGoal = null; _nextTick = _yieldUntil = default;
    }

    private void UpdateMeteor(in Hoh90KitingFrame f, DateTime now)
    {
        foreach (var helper in f.Meteors)
            if (helper.Caster != 0 && Finite(helper.Origin) && XZDistance(helper.Origin, _meteorOrigin) <= 5 &&
                float.IsFinite(helper.Remaining) && helper.Remaining >= 0)
            {
                _meteorHelper = helper.Caster;
                // The native ground target is authoritative; keep the start half fixed.
                if (XZDistance(helper.Origin, _meteorOrigin) > .1f) _meteorGoal = null;
                _meteorOrigin = helper.Origin;
                break;
            }
        foreach (var effect in f.Resolutions)
            if (effect.Action == 12212 && effect.BaseId == 8865 && effect.At >= _meteorStarted &&
                effect.At <= now && (_meteorHelper != 0 ? effect.Caster == _meteorHelper :
                    Finite(effect.Origin) && XZDistance(effect.Origin, _meteorOrigin) <= 5))
            {
                _meteorActive = false; _meteorGoal = null;
                Yield(now, "meteor-impact-confirmed-bmr"); return;
            }
        // This is an observation watchdog, never a synthetic impact or permission to resume orbiting.
        if (now - _meteorStarted > TimeSpan.FromSeconds(30))
            _meteorTimedOut = true;
        if (_meteorTimedOut || !f.MechanicObserverAvailable)
        { Release(); Stage = "meteor-unconfirmed-impact-bmr"; return; }
        if (now < _yieldUntil || now < _nextTick) return;
        _nextTick = now.AddMilliseconds(150);
        if (_owns && !TryOwnership(true))
        { Yield(now, "meteor-bmr-navigation-resumed"); return; }
        if (_meteorGoal is not { } locked || !MeteorPointSafe(locked, f))
            _meteorGoal = FindMeteorGoal(f);
        if (_meteorGoal is not { } goal)
        { Yield(now, "meteor-no-safe-far-half-goal-bmr"); return; }
        var step = FindMeteorStep(f.Player, goal, f);
        if (step is not { } destination)
        { _meteorGoal = null; Yield(now, "meteor-no-safe-far-half-route-bmr"); return; }
        Destination = destination;
        if (!_leaseRequested) { _leaseRequested = true; _leaseStarted = now; }
        if (!_owns)
        {
            if (!TryOwnership(true))
            {
                Stage = "meteor-awaiting-bmr-navigation-stop";
                if (now - _leaseStarted >= TimeSpan.FromSeconds(1)) Yield(now, "meteor-movement-lease-timeout-bmr");
                return;
            }
            _owns = true; _lastProgress = now; _lastPlayer = f.Player;
        }
        if (XZDistance(f.Player, _lastPlayer) >= .3f)
        { _lastProgress = now; _lastPlayer = f.Player; }
        if (XZDistance(f.Player, goal) <= .5f && MeteorPointSafe(f.Player, f))
        { stop(); Stage = "meteor-holding-far-half-until-impact"; return; }
        if (now - _lastProgress >= TimeSpan.FromSeconds(2))
        { Yield(now, "meteor-no-movement-progress-bmr"); return; }
        move(destination); Stage = "meteor-crossing-to-far-half";
    }

    private Vector3? FindMeteorGoal(in Hoh90KitingFrame f)
    {
        var candidates = new List<(Vector3 Point, float Score)>();
        foreach (float radius in new[] { ArenaRadius, 20f })
            for (int i = 0; i < 96; i++)
            {
                float angle = i * MathF.Tau / 96;
                var p = _meteorArena + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                p.Y = f.Player.Y;
                if (!MeteorPointSafe(p, f)) continue;
                float projection = Vector2.Dot(XZ(p - _meteorArena), _meteorAway);
                candidates.Add((p, projection * 2 + XZDistance(p, _meteorOrigin) - XZDistance(f.Player, p) * .2f));
            }
        var best = candidates.OrderByDescending(p => p.Score).Take(6).ToArray();
        foreach (var p in best)
            if (SafeRoute(f.Player, p.Point)) return p.Point;
        foreach (var p in best.Take(2))
            if (FindMeteorStep(f.Player, p.Point, f) != null) return p.Point;
        return null;
    }

    private bool MeteorPointSafe(Vector3 p, in Hoh90KitingFrame f) =>
        InsideArena(p, _meteorArena) && XZDistance(p, _meteorArena) >= 19.5f &&
        // Hard constraint: a side edge in the boss half is never an acceptable fallback.
        Vector2.Dot(XZ(p - _meteorArena), _meteorAway) >= 8 &&
        XZDistance(p, _meteorOrigin) >= MeteorDistance && OutsideSpheres(p, f);

    private Vector3? FindMeteorStep(Vector3 from, Vector3 goal, in Hoh90KitingFrame f)
    {
        if (SafeRoute(from, goal)) return goal;
        // Validate both legs before taking a waypoint around a live hazard.
        var waypoints = new List<Vector3>();
        foreach (float radius in new[] { 12f, 20f, ArenaRadius })
            for (int i = 0; i < 24; i++)
            {
                float angle = i * MathF.Tau / 24;
                var p = _meteorArena + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                p.Y = from.Y;
                if (XZDistance(from, p) > 1 && OutsideSpheres(p, f)) waypoints.Add(p);
            }
        foreach (var p in waypoints.OrderBy(p => XZDistance(from, p) + XZDistance(p, goal)).Take(8))
            if (SafeRoute(from, p) && SafeRoute(p, goal)) return p;
        return null;
    }

    private Vector3? FindKitingStep(in Hoh90KitingFrame f)
    {
        var delta = XZ(f.Player - f.Boss);
        if (delta.LengthSquared() < .01f) delta = XZ(f.Player - f.Arena);
        if (delta.LengthSquared() < .01f) delta = new(1, 0);
        var away = Vector2.Normalize(delta);
        float desired = Math.Max(PreferredDistance, f.BossRadius + f.PlayerRadius + 8);
        float radial = Math.Clamp((desired - Distance) / 3, -.6f, 2);
        var tangent = new Vector2(-away.Y, away.X) * _direction;
        var preferred = Vector2.Normalize(tangent + away * radial);
        var candidates = new List<(Vector3 Point, float Score, int Direction)>();
        for (int i = 0; i < 32; i++)
        {
            float angle = i * MathF.Tau / 32;
            var heading = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
            var p = f.Player + new Vector3(heading.X, 0, heading.Y) * 2.5f;
            float distance = XZDistance(p, f.Boss);
            if (!InsideArena(p, f.Arena) ||
                distance < f.BossRadius + f.PlayerRadius + 1 && distance <= Distance + .1f ||
                Distance < desired - 2 && distance < Distance - .1f) continue;
            float score = Vector2.Dot(heading, preferred) * 3 - Math.Abs(desired - distance) * .4f;
            int direction = Vector2.Dot(heading, tangent) >= 0 ? _direction : -_direction;
            candidates.Add((p, score, direction));
        }
        foreach (var candidate in candidates.OrderByDescending(p => p.Score).Take(8))
            if (SafeRoute(f.Player, candidate.Point))
            { _direction = candidate.Direction; return candidate.Point; }
        return null;
    }

    private Vector3? FindRetreatGoal(in Hoh90KitingFrame f)
    {
        var candidates = new List<(Vector3 Point, float Score)>();
        // The boss stands still during its visual cast. Keep that cast-start origin fixed.
        if (RetreatPointSafe(f.Player, f)) candidates.Add((f.Player, 1000));
        foreach (float radius in new[] { 12f, 16f, 20f, ArenaRadius })
            for (int i = 0; i < 96; i++)
            {
                float angle = i * MathF.Tau / 96;
                var p = f.Arena + new Vector3(MathF.Cos(angle) * radius, 0, MathF.Sin(angle) * radius);
                p.Y = f.Player.Y;
                float travel = XZDistance(f.Player, p);
                // Do not choose a far destination that cannot be reached before the handback.
                if (travel > Math.Max(0, f.Remaining - .2f) * 5.5f || !RetreatPointSafe(p, f)) continue;
                float separation = XZDistance(p, _castOrigin);
                candidates.Add((p, separation - travel * .8f));
            }
        foreach (var p in candidates.OrderByDescending(p => p.Score).Take(12))
            if (SafeRoute(f.Player, p.Point)) return p.Point;
        return null;
    }

    private bool RetreatPointSafe(Vector3 p, in Hoh90KitingFrame f)
    {
        if (!InsideArena(p, f.Arena) || XZDistance(p, _castOrigin) <
                Math.Max(PreferredDistance, f.BossRadius + f.PlayerRadius + 8)) return false;
        return OutsideSpheres(p, f);
    }
    private static bool OutsideSpheres(Vector3 p, in Hoh90KitingFrame f)
    {
        foreach (var sphere in f.Spheres)
            if (!Finite(sphere.Position) || !float.IsFinite(sphere.Radius) || sphere.Radius < 0 ||
                XZDistance(p, sphere.Position) < sphere.Radius + f.PlayerRadius + .6f) return false;
        return true;
    }
    private bool SafeRoute(Vector3 from, Vector3 to)
    {
        try { return routeSafe(from, to); }
        catch (Exception e) { LastFailure = e.Message; return false; }
    }
    private void Yield(DateTime now, string stage)
    {
        Release(); Stage = stage; _yieldUntil = now.AddMilliseconds(800);
    }
    private void Release()
    {
        stop(); Destination = null; _owns = false;
        if (_leaseRequested && TryOwnership(false)) _leaseRequested = false;
    }
    private bool TryOwnership(bool active)
    {
        try { return ownership(active); }
        catch (Exception e) { LastFailure = e.Message; return false; }
    }
    public void Reset()
    {
        Release(); _boss = 0; _retreatAction = 0; _retreatCast = _castDelegated = false; _castGoal = null;
        _nextTick = _yieldUntil = default; _direction = 1; Stage = "inactive";
        _meteorActive = _meteorVisualLatched = _meteorTimedOut = false; _meteorHelper = 0; _meteorGoal = null;
    }
    public void Dispose() => Reset();
    public static bool SphereResolved(ulong caster, IReadOnlyList<Hoh90MechanicResolution> effects) =>
        effects.Where(e => e.Caster == caster && e.BaseId == 9235 && e.Action == 12210)
            .Select(e => e.Sequence).Distinct().Take(3).Count() == 3;
    public static bool Eligible(in Hoh90KitingFrame f) => f.RunActive && f.PlayerReady && f.InCombat &&
        f.BmrConfigured && !f.ManualOrbit && f.Mode == FarmingMode.FreeExploration &&
        f.State is { IsValid: true, IsInDuty: true, IsInDeepDungeonTerritory: true, IsTransitioning: false,
            DungeonId: 2, Floor: 90, FloorKind: DeepDungeonFloorKind.Boss } &&
        f.BossId != 0 && f.BossBaseId == BossBaseId && f.BossAlive &&
        Finite(f.Player) && Finite(f.Boss) && Finite(f.Arena) &&
        float.IsFinite(f.BossRadius) && f.BossRadius >= 0 && float.IsFinite(f.PlayerRadius) && f.PlayerRadius >= 0 &&
        (!f.BossCasting || f.Action != 0 && float.IsFinite(f.Remaining) && f.Remaining >= 0);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static Vector2 XZ(Vector3 v) => new(v.X, v.Z);
    public static float XZDistance(Vector3 a, Vector3 b) => Vector2.Distance(XZ(a), XZ(b));
    private static bool InsideArena(Vector3 p, Vector3 arena) => Finite(p) && XZDistance(p, arena) <= ArenaRadius + .01f;
}
