using System.Numerics;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private Hoh90KitingController? _hoh90Kiting;
    private MovementInputController? _hoh90KitingInput;
    private ICallGateSubscriber<Vector2>? _hoh90KitingArena;
    private ICallGateSubscriber<bool>? _hoh90KitingNavigating;
    private DateTime _nextHoh90KitingSnapshot;
    private string? _hoh90KitingStage;
    private int _hoh90RouteProbes;

    private bool SetHoh90KitingLease(bool active)
    {
        if (active)
        {
            _hoh90KitingNavigating ??= Service.PluginInterface.GetIpcSubscriber<bool>("BossMod.AI.IsNavigating");
            if (!_hoh90KitingNavigating.HasFunction || !SetBossMovementOverride(true)) return false;
            return !_hoh90KitingNavigating.InvokeFunc();
        }
        bool accepted = SetBossMovementOverride(false);
        _bossMechanics.Update(IsRunActive, _currentDeepDungeonState, _configuration.BossMechanics,
            _ddHost?.Context?.FarmingPlan?.Mode);
        _configuration.BossMechanicsActive = _bossMechanics.IsEnabled;
        return accepted;
    }

    private unsafe void UpdateHoh90Kiting()
    {
        var state = _currentDeepDungeonState;
        var player = Service.LocalPlayer;
        bool scoped = IsRunActive && state is { IsValid: true, IsInDuty: true, DungeonId: 2, Floor: 90,
                IsTransitioning: false } && _configuration.BossMechanics.UsesBmrBossHandling() &&
            player is { IsDead: false, CurrentHp: > 0 } && Service.Condition[ConditionFlag.InCombat] &&
            !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51] &&
            _ddHost?.Context?.NormalCombatOutputSuppressed != true && _hohBossOrbit?.Enabled != true;
        if (!scoped) { ResetHoh90Kiting(); return; }
        var boss = Service.GameObjects.OfType<IBattleNpc>().FirstOrDefault(n =>
            n.BaseId == Hoh90KitingController.BossBaseId && n is { IsDead: false, IsTargetable: true, CurrentHp: > 0 });
        if (boss == null) { ResetHoh90Kiting(); return; }
        try
        {
            _hoh90KitingArena ??= Service.PluginInterface.GetIpcSubscriber<Vector2>("BossMod.Hints.ArenaCenter");
            if (!_hoh90KitingArena.HasFunction) { ResetHoh90Kiting(); return; }
            var center = _hoh90KitingArena.InvokeFunc();
            if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) ||
                Vector2.DistanceSquared(center, new(-300, -300)) > 4)
            { ResetHoh90Kiting(); return; }
            var spheres = new List<Hoh90KitingSphere>();
            var meteors = new List<Hoh90MeteorCast>();
            var recorder = _ddHost?.Context?.DeathRecorder;
            var resolutions = recorder?.Hoh90Resolutions ?? [];
            foreach (var npc in Service.GameObjects.OfType<IBattleNpc>())
            {
                if (npc.BaseId == 9235 && npc.Address != 0 && ((GameObject*)npc.Address)->EventState != 7 &&
                    !Hoh90KitingController.SphereResolved(npc.EntityId, resolutions))
                    // Installed DD90Onra.Subduction: 8 + sphere R1 = 9m. Add player/movement margin in policy.
                    spheres.Add(new(npc.Position, 8 + Math.Max(1, npc.HitboxRadius)));
                if (npc.BaseId == 8865 && npc.Address != 0 && npc.IsCasting && npc.CastActionId == 12212)
                {
                    var cast = ((Character*)npc.Address)->GetCastInfo();
                    if (cast != null) meteors.Add(new(npc.GameObjectId, cast->TargetLocation,
                        Math.Max(0, npc.TotalCastTime - npc.CurrentCastTime)));
                }
            }
            Vector3? castLocation = null;
            if (boss.IsCasting && boss.CastActionId == 12211 && boss.Address != 0)
            {
                var cast = ((Character*)boss.Address)->GetCastInfo();
                if (cast != null) castLocation = cast->TargetLocation;
            }
            _hoh90Kiting ??= new(SetHoh90KitingLease, MoveHoh90Kiting, StopHoh90KitingInput,
                IsHoh90KitingRouteSafe);
            var now = DateTime.UtcNow;
            _hoh90RouteProbes = 0;
            _hoh90Kiting.Update(new(state, _ddHost?.Context?.FarmingPlan?.Mode, IsRunActive, true, true, true,
                false, boss.GameObjectId, boss.BaseId, true, player!.Position, boss.Position,
                new(center.X, player.Position.Y, center.Y), boss.HitboxRadius, player.HitboxRadius,
                boss.IsCasting, boss.IsCasting ? boss.CastActionId : 0,
                boss.IsCasting ? Math.Max(0, boss.TotalCastTime - boss.CurrentCastTime) : 0, spheres) {
                    CastLocation = castLocation, Meteors = meteors, Resolutions = resolutions,
                    MechanicObserverAvailable = recorder?.Hoh90ObserverAvailable == true }, now);
            if (now >= _nextHoh90KitingSnapshot || _hoh90KitingStage != _hoh90Kiting.Stage)
            {
                _nextHoh90KitingSnapshot = now.AddSeconds(1); _hoh90KitingStage = _hoh90Kiting.Stage;
                var destination = _hoh90Kiting.Destination;
                _ddHost?.Context?.RecordDiagnostic?.Invoke("hoh90-distance-kiting", new {
                    stage = _hoh90Kiting.Stage, ownsMovement = _hoh90Kiting.OwnsMovement,
                    distance = _hoh90Kiting.Distance, bossId = boss.GameObjectId,
                    bossCasting = boss.IsCasting, action = boss.IsCasting ? boss.CastActionId : 0,
                    player = new { player.Position.X, player.Position.Y, player.Position.Z },
                    boss = new { boss.Position.X, boss.Position.Y, boss.Position.Z },
                    destination = destination is { } p ? new { p.X, p.Y, p.Z } : null,
                    sphereCount = spheres.Count, lastFailure = _hoh90Kiting.LastFailure,
                    meteorPending = _hoh90Kiting.MeteorPending, meteorHelper = _hoh90Kiting.MeteorHelper,
                    meteorOrigin = _hoh90Kiting.MeteorOrigin is {} origin ? new { origin.X, origin.Y, origin.Z } : null,
                    meteorGoal = _hoh90Kiting.MeteorGoal is {} goal ? new { goal.X, goal.Y, goal.Z } : null,
                    meteorHelperCasts = meteors.Select(m => new { caster = m.Caster, remaining = m.Remaining,
                        origin = new { m.Origin.X, m.Origin.Y, m.Origin.Z } }).ToArray(),
                    routeProbes = _hoh90RouteProbes, nativeMechanicObserver = recorder?.Hoh90ObserverAvailable == true,
                    nativeBmrNavigating = _hoh90KitingNavigating?.HasFunction == true
                        ? _hoh90KitingNavigating.InvokeFunc() : (bool?)null });
            }
        }
        catch (Exception error)
        {
            ResetHoh90Kiting();
            Service.Log.Warning(error, "[HOH90.DistanceKiting] Keeping BMR after movement read failure");
        }
    }

    private bool IsHoh90KitingRouteSafe(Vector3 from, Vector3 to)
    {
        // IsDashSafe only tests the endpoint: check hints/ground/sight along the entire walk.
        return Hoh90RouteSafety.Check(from, to, p => _hohOrbitGeometry.GroundPoint(p, .15f, .4f),
            IsHohOrbitSegmentSafe, ref _hoh90RouteProbes);
    }
    private void MoveHoh90Kiting(Vector3 point)
    {
        _ddHost?.Context?.Navigator.CancelAll();
        _hoh90KitingInput ??= new MovementInputController { IsAutoMove = false, Precision = .1f };
        _hoh90KitingInput.DesiredPosition = point; _hoh90KitingInput.Enabled = true;
    }
    private void StopHoh90KitingInput() { if (_hoh90KitingInput != null) _hoh90KitingInput.Enabled = false; }
    private void ResetHoh90Kiting()
    {
        _hoh90Kiting?.Reset(); StopHoh90KitingInput();
        _hoh90KitingStage = null; _nextHoh90KitingSnapshot = default;
    }
    private void DisposeHoh90Kiting()
    {
        try { ResetHoh90Kiting(); }
        finally { _hoh90KitingInput?.Dispose(); _hoh90KitingInput = null; }
    }
}
