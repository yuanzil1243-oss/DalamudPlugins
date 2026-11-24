using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Hooking;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Owns PT50 island movement while sand is active. Other floors retain BMR control.</summary>
internal sealed unsafe class Pt50IslandMovementController : IDisposable
{
    private readonly Func<bool, bool> _setOverride;
    private readonly Action<bool> _setRotationSuppressed;
    private readonly Action<string, object> _record;
    private readonly Pt50MechanicState _state = new();
    private readonly ConcurrentQueue<Effect> _effects = new();
    private Hook<ActionEffectHandler.Delegates.Receive>? _hook;
    private MovementInputController? _movement;
    private bool _unavailable, _watching, _ownsMovement, _disposed;
    private Pt50RockPoint? _landing;
    private Vector2? _windSand;
    private int _plannedRevision = -1;
    private DateTime _nextDiagnostic, _nextPlan, _nextQuickPlan;
    private Task<PlanResult>? _planning;
    private readonly Queue<Pt50RockPoint> _route = new();
    private bool _fallbackRequested;
    private DateTime _skipUntil;
    private int _epoch;
    private sealed record PlanResult(int Epoch, int Revision, Vector2 From, Pt50Route? Route);
    private bool _wasSinking;
    private string _stage = "inactive";
    public bool IsActive => _ownsMovement;
    public bool IsChasing => _state.ChaseActive;
    public string Status => "PT50 - " + _stage;
    private readonly record struct Effect(uint Action, uint Sequence, Vector2 Position, DateTime At);

    public Pt50IslandMovementController(Func<bool, bool> setOverride, Action<bool> setRotationSuppressed,
        Action<string, object> record)
    {
        _setOverride = setOverride;
        _setRotationSuppressed = setRotationSuppressed;
        _record = record;
    }

    private bool EnsureObserver()
    {
        if (_unavailable || _disposed) return false;
        if (_hook != null) return true;
        try
        {
            _hook = Service.GameInteropProvider.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.Addresses.Receive.Value, ReceiveEffect);
            NativeDeepDungeonLogMessageSource.EnableHook(_hook, "PT50.ActionEffect");
            _movement = new MovementInputController { IsAutoMove = false, Precision = Pt50IslandRoutePlanner.ArrivalRadius };
            return true;
        }
        catch (Exception ex)
        {
            _hook?.Dispose(); _hook = null;
            _movement?.Dispose(); _movement = null;
            _unavailable = true;
            Service.Log.Error($"[PT50.Islands] Observer unavailable; retaining BMR movement: {ex}");
            _record("pt50-island-unavailable", new { error = ex.Message });
            return false;
        }
    }

    private void ReceiveEffect(uint casterId, Character* caster, Vector3* targetPosition,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        // Observe only; never alter actions, damage, targeting or animation lock.
        try
        {
            if (_watching && header != null && caster != null &&
                header->ActionType == 1 && header->ActionId is 43531 or 43534 or 43535 or 43536 or 43537 or 43538)
            {
                Vector3 pos = header->AnimationTargetId == casterId || targetPosition == null
                    ? caster->GameObject.Position : *targetPosition;
                _effects.Enqueue(new(header->ActionId, header->GlobalSequence, new(pos.X, pos.Z), DateTime.UtcNow));
            }
        }
        catch (Exception ex) { Service.Log.Error($"[PT50.Islands] Observation failed: {ex.Message}"); }
        _hook!.Original(casterId, caster, targetPosition, header, effects, targets);
    }

    public bool Update(InstanceContentDeepDungeon* dd, bool enabled)
    {
        var player = Service.LocalPlayer;
        if (!enabled || dd == null || dd->DeepDungeonId != 4 || dd->Floor != 50 || player == null || player.IsDead ||
            Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51] ||
            !Service.Condition[ConditionFlag.InCombat])
        { Reset(); return false; }
        if (!EnsureObserver()) return false;
        var now = DateTime.UtcNow;
        if (now < _skipUntil) return false;
        _watching = true;
        IBattleChara? boss = null;
        var dangers = new List<Pt50DangerCircle>();
        foreach (var obj in Service.GameObjects)
        {
            if (obj is not IBattleChara battle || battle.IsDead) continue;
            if (battle.BaseId == 18546) boss = battle;
            if (battle.IsCasting && battle.CastActionId is 43537 or 43538)
                _state.ObserveCast(battle.CastActionId, XZ(battle.Position), battle.TotalCastTime - battle.CurrentCastTime, now);
            if (battle.IsCasting && battle.CastActionId == 43532)
                dangers.Add(new(XZ(battle.Position), 5, now.AddSeconds(Math.Max(0, battle.TotalCastTime - battle.CurrentCastTime))));
        }
        if (boss == null) { Reset(); return false; }
        if (boss.IsCasting)
            _state.ObserveCast(boss.CastActionId, XZ(boss.Position), boss.TotalCastTime - boss.CurrentCastTime, now);
        while (_effects.TryDequeue(out var effect))
        {
            if (_state.ObserveAction(effect.Action, effect.Sequence, effect.Position, effect.At))
                _record("pt50-island-action", new { effect.Action, effect.Sequence, position = Coordinates(effect.Position), effect.At,
                    _state.RemainingHits, _state.FirstHit });
        }
        if (!_state.SandActive) return false;
        if (_state.IsStale(now) || (_state.WindAt != default && now > _state.WindAt.AddSeconds(1)))
        {
            // Do not continue blind when the event observer loses the mechanic sequence.
            _record("pt50-island-fallback", new { reason = "missing-resolution-event", _state.RemainingHits });
            Reset(); return false;
        }
        if (!_ownsMovement)
        {
            if (!_setOverride(true)) return false;
            _ownsMovement = true;
            _record("pt50-island-ownership", new { active = true });
        }
        bool sinking = false, windProtected = false; double sinkingElapsed = 0;
        foreach (var status in player.StatusList)
        {
            if (status.StatusId == 567) { sinking = true; sinkingElapsed = Math.Clamp(4 - Math.Abs(status.RemainingTime), 0, 4); }
            if (status.StatusId == 4625) windProtected = true;
        }
        var position = XZ(player.Position);
        if (_wasSinking && !sinking)
            _record("pt50-island-sinking-cleared", new { position = Coordinates(position) });
        _wasSinking = sinking;
        _setRotationSuppressed(_state.ChaseActive);

        if (_state.WindAt != default && (_state.WindAt - now).TotalSeconds <= 1.15)
        {
            if (_windSand is { } oldSand && !Pt50IslandRoutePlanner.PathAvoids(position, oldSand, now,
                    Pt50IslandRoutePlanner.TravelTime(position, oldSand, sinkingElapsed), dangers)) _windSand = null;
            _windSand ??= Pt50IslandRoutePlanner.WindSandPoint(position, now, dangers);
            if (_windSand is { } sand) MoveTo(sand, player.Position.Y);
            else StopInput();
            _stage = windProtected ? "强风无效已确认，等待烈风结算" : "烈风前短暂进沙，等待强风无效";
        }
        else if (_state.ChaseActive)
        {
            _windSand = null;
            UpdateChase(position, player.Position.Y, sinking, sinkingElapsed, dangers, now);
            if (_fallbackRequested)
            {
                Reset();
                _skipUntil = now.AddSeconds(2);
                return false;
            }
        }
        else
        {
            if (_windSand != null) _landing = null;
            _windSand = null;
            // After wind/chase, land on rock and wait. Falling rocks can invalidate the landing.
            if (_landing == null || !Pt50IslandRoutePlanner.PathAvoids(position, _landing.Value.Position, now,
                    Pt50IslandRoutePlanner.TravelTime(position, _landing.Value.Position, sinkingElapsed), dangers))
                _landing = Pt50IslandRoutePlanner.PreparationPoint(position, now, dangers, _state.ReducedArena, sinkingElapsed);
            if (_landing is { } landing)
            {
                MoveTo(landing.Position, player.Position.Y);
                _stage = sinking ? "返回石头并等待下陷消失" : "石头上等待机制";
            }
            else
            {
                _record("pt50-island-fallback", new { reason = "no-safe-recovery-rock", position = Coordinates(position), sinkingElapsed });
                Reset(); _skipUntil = now.AddSeconds(2); return false;
            }
        }
        if (now >= _nextDiagnostic)
        {
            _nextDiagnostic = now.AddSeconds(0.5);
            _record("pt50-island-snapshot", new { stage = _stage, position = Coordinates(position), landing = LandingCoordinates(_landing),
                _state.RemainingHits, circle = Coordinates(_state.Circle), _state.NextHitAt, _state.WindAt,
                sinking, sinkingElapsed, windProtected, _state.ReducedArena,
                movementOwner = "NewFolder3", bmr = "suspended" });
        }
        return true;
    }

    private void UpdateChase(Vector2 position, float y, bool sinking, double sinkingElapsed,
        IReadOnlyList<Pt50DangerCircle> dangers, DateTime now)
    {
        if (_planning?.IsCompleted == true)
        {
            var task = _planning;
            _planning = null;
            if (task.IsCompletedSuccessfully)
            {
                var result = task.Result;
                if (result.Epoch == _epoch && result.Revision == _state.Revision)
                {
                    double budget = (_state.NextHitAt - now).TotalSeconds;
                    string rejection = ValidateRoute(result.Route, position, sinkingElapsed, dangers, now);
                    var route = result.Route;
                    if (rejection.Length != 0)
                    {
                        _record("pt50-island-route-rejected", new { reason = rejection,
                            position = Coordinates(position), plannedFrom = Coordinates(result.From),
                            circle = Coordinates(_state.Circle), remaining = budget, _state.RemainingHits,
                            plannedSteps = route?.Steps.Count ?? 0, sinkingElapsed });
                        // A complete route can fail even when the next hit is avoidable.
                        // Recompute just that hit from the actual position and current hazards.
                        route = Pt50IslandRoutePlanner.Plan(position, _state.Circle, _state.FirstHit, 1,
                            budget, now, dangers, sinkingElapsed, _state.ReducedArena);
                    }
                    if (ValidateRoute(route, position, sinkingElapsed, dangers, now).Length == 0)
                    {
                        _landing = route!.Steps[0];
                        _route.Clear();
                        foreach (var step in route.Steps.Skip(1)) _route.Enqueue(step);
                        _plannedRevision = result.Revision;
                        _record("pt50-island-route", new { position = Coordinates(position),
                            circle = Coordinates(_state.Circle), remaining = budget, _state.RemainingHits,
                            steps = route.Steps.Select(p => LandingCoordinates(p)).ToArray(),
                            route.TravelSeconds, route.Clearance,
                            complete = route.Steps.Count == _state.RemainingHits, recovered = rejection.Length != 0 });
                    }
                    else
                    {
                        _route.Clear();
                        // No safe chase dodge: still prefer a validated rock over handing
                        // movement back toward the sand/central hole during the sequence.
                        _landing = Pt50IslandRoutePlanner.PreparationPoint(position, now, dangers,
                            _state.ReducedArena, sinkingElapsed);
                        _record("pt50-island-emergency-recovery", new { reason = "no-reachable-chase-dodge",
                            position = Coordinates(position), landing = LandingCoordinates(_landing),
                            circle = Coordinates(_state.Circle), remaining = budget, _state.RemainingHits });
                        if (_landing == null) { _fallbackRequested = true; return; }
                        _nextPlan = now.AddMilliseconds(150);
                    }
                }
            }
            else { _fallbackRequested = true; Service.Log.Error($"[PT50.Islands] Route planning failed: {task.Exception}"); return; }
        }
        bool newHit = _plannedRevision != _state.Revision;
        bool invalidated = _landing is { } old && !Pt50IslandRoutePlanner.PathAvoids(position, old.Position,
            now, Pt50IslandRoutePlanner.TravelTime(position, old.Position, sinkingElapsed), dangers);
        if (newHit && !invalidated && _landing is { } pending && sinking &&
            Pt50IslandRoutePlanner.TravelTime(position, pending.Position, sinkingElapsed) + 0.4 <
                (_state.NextHitAt - now).TotalSeconds &&
            Vector2.Distance(pending.Position, _state.FirstHit ? _state.Circle :
                Pt50IslandRoutePlanner.Pursue(_state.Circle, pending.Position)) >=
                Pt50IslandRoutePlanner.ChaseRadius + Pt50IslandRoutePlanner.HitMargin)
        {
            MoveTo(pending.Position, y);
            _stage = "先完成落脚并清除下陷";
            return;
        }
        if (newHit && !invalidated && !sinking && _route.Count == _state.RemainingHits && _route.Count > 0)
        {
            var next = _route.Peek();
            double travel = Pt50IslandRoutePlanner.TravelTime(position, next.Position);
            float clearance = Vector2.Distance(next.Position, Pt50IslandRoutePlanner.Pursue(_state.Circle, next.Position));
            if (clearance >= Pt50IslandRoutePlanner.ChaseRadius + Pt50IslandRoutePlanner.HitMargin &&
                (!_state.ReducedArena || Pt50IslandRoutePlanner.InsideWindArena(position, next.Position)) &&
                travel + Pt50IslandRoutePlanner.TimingMargin < (_state.NextHitAt - now).TotalSeconds &&
                Pt50IslandRoutePlanner.PathAvoids(position, next.Position, now, travel, dangers))
            {
                _landing = _route.Dequeue();
                _plannedRevision = _state.Revision;
                newHit = false;
                _record("pt50-island-route-advance", new { position = Coordinates(position), landing = LandingCoordinates(_landing),
                    _state.RemainingHits, travel, clearance });
            }
            else _route.Clear();
        }
        if (invalidated)
        {
            _route.Clear();
            _landing = null; // Do not move to an unvalidated chase landing while replanning.
        }
        Pt50Route? immediate = null;
        if ((newHit || invalidated) && !_state.FirstHit && now < _state.NextHitAt && now >= _nextQuickPlan)
        {
            // Do not spend a pursuit interval standing still while the full beam
            // search runs. Commit a validated next step, then extend from that step.
            _nextQuickPlan = now.AddMilliseconds(150);
            immediate = Pt50IslandRoutePlanner.Plan(position, _state.Circle, false, 1,
                (_state.NextHitAt - now).TotalSeconds, now, dangers, sinkingElapsed, _state.ReducedArena);
            if (ValidateRoute(immediate, position, sinkingElapsed, dangers, DateTime.UtcNow).Length == 0)
            {
                _landing = immediate!.Steps[0];
                _route.Clear();
                _plannedRevision = _state.Revision;
                newHit = false;
                _record("pt50-island-immediate-step", new { position = Coordinates(position),
                    landing = LandingCoordinates(_landing), _state.RemainingHits, immediate.TravelSeconds });
            }
            else immediate = null;
        }
        if (_planning == null && (immediate != null || now >= _nextPlan) &&
            (newHit || invalidated || _landing == null || immediate != null && _state.RemainingHits > 1))
        {
            _nextPlan = now.AddMilliseconds(500);
            double remaining = (_state.NextHitAt - now).TotalSeconds;
            var circle = _state.Circle; bool first = _state.FirstHit; int hits = _state.RemainingHits;
            bool reducedArena = _state.ReducedArena;
            int revision = _state.Revision, epoch = _epoch;
            var dangerCopy = dangers.ToArray();
            if (immediate is { } committed)
            {
                var committedLanding = committed.Steps[0];
                var hitCircle = Pt50IslandRoutePlanner.Pursue(circle, committedLanding.Position);
                var nextHitAt = _state.NextHitAt;
                _planning = Task.Run(() =>
                {
                    var extension = Pt50IslandRoutePlanner.Plan(committedLanding.Position, hitCircle, false, hits - 1,
                        Pt50IslandRoutePlanner.Interval, nextHitAt, dangerCopy, reducedArena: reducedArena);
                    return new PlanResult(epoch, revision, position, new Pt50Route(
                        [committedLanding, .. extension?.Steps ?? []], committed.TravelSeconds + (extension?.TravelSeconds ?? 0),
                        Math.Min(committed.Clearance, extension?.Clearance ?? committed.Clearance)));
                });
            }
            else
                _planning = Task.Run(() => new PlanResult(epoch, revision, position,
                    Pt50IslandRoutePlanner.Plan(position, circle, first, hits, remaining - 0.15,
                        now, dangerCopy, sinkingElapsed, reducedArena)));
        }
        if (_landing is { } landing)
        {
            // Finishing an existing crossing is preferable to stopping in sand.
            MoveTo(landing.Position, y);
            _stage = Vector2.Distance(position, landing.Position) <= Pt50IslandRoutePlanner.ArrivalRadius
                ? sinking ? "落脚等待下陷消失" : "石头上等待下一次追踪结算"
                : $"锁定石头 {landing.Rock + 1}，剩余 {_state.RemainingHits} 击";
        }
        else { StopInput(); _stage = "追踪没有可行路线"; }
    }

    private string ValidateRoute(Pt50Route? route, Vector2 position, double sinking,
        IReadOnlyList<Pt50DangerCircle> dangers, DateTime now) =>
        Pt50IslandRoutePlanner.ValidateNextStep(route, position, _state.Circle, _state.FirstHit,
            (_state.NextHitAt - now).TotalSeconds, now, dangers, sinking, _state.ReducedArena);

    private static object Coordinates(Vector2 p) => new { x = p.X, z = p.Y };
    private static object? LandingCoordinates(Pt50RockPoint? p) => p is { } point
        ? new { rock = point.Rock, x = point.Position.X, z = point.Position.Y } : null;

    private void MoveTo(Vector2 destination, float y)
    {
        var p = Service.LocalPlayer;
        if (p == null || Vector2.Distance(XZ(p.Position), destination) <= Pt50IslandRoutePlanner.ArrivalRadius)
        { StopInput(); return; }
        _movement!.DesiredPosition = new(destination.X, y, destination.Y);
        _movement.IsAutoMove = false; // Manual movement retains priority.
        _movement.Enabled = true;
    }

    private void StopInput() { if (_movement?.Enabled == true) _movement.Enabled = false; }
    private static Vector2 XZ(Vector3 p) => new(p.X, p.Z);

    public void Reset()
    {
        _watching = false;
        StopInput();
        _state.Reset();
        ++_epoch;
        _effects.Clear();
        _route.Clear();
        _fallbackRequested = false;
        _landing = null; _windSand = null; _plannedRevision = -1;
        _wasSinking = false; _stage = "inactive";
        _nextPlan = _nextQuickPlan = default;
        if (_ownsMovement)
        {
            _ownsMovement = false;
            _setRotationSuppressed(false);
            _setOverride(false);
            _record("pt50-island-ownership", new { active = false });
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Reset();
        _hook?.Dispose(); _hook = null;
        _movement?.Dispose(); _movement = null;
        _disposed = true;
    }
}
