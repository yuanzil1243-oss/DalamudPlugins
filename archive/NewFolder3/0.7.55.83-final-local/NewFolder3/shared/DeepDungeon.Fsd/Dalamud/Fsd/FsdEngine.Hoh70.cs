using System.Collections.Concurrent;
using System.Numerics;
using Dalamud.Hooking;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Runtime;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private Hoh70PuddleController? _hoh70Puddle;
    private MovementInputController? _hoh70Input;
    private Hook<ActionEffectHandler.Delegates.Receive>? _hoh70Hook;
    private readonly ConcurrentQueue<(uint Boss, uint Action, uint Sequence)> _hoh70Effects = new();
    private volatile uint _hoh70WatchedBoss;
    private bool _hoh70ObserverUnavailable;
    private DateTime _nextHoh70Snapshot;

    private unsafe void UpdateHoh70Puddle()
    {
        var state = _currentDeepDungeonState;
        var player = Service.LocalPlayer;
        bool scoped = IsRunActive && _configuration.BossMechanics.UsesBmrBossHandling() &&
            state is { IsInDuty: true, DungeonId: 2, Floor: 70 } && player is { IsDead: false } &&
            Service.Condition[ConditionFlag.InCombat] && !Service.Condition[ConditionFlag.BetweenAreas] &&
            !Service.Condition[ConditionFlag.BetweenAreas51];
        IBattleNpc? boss = scoped ? Service.GameObjects.OfType<IBattleNpc>().FirstOrDefault(n =>
            n.BaseId == Hoh70PuddleController.BossBaseId && !n.IsDead && n.CurrentHp > 0) : null;
        if (!scoped || boss == null || player == null)
        {
            _hoh70WatchedBoss = 0; _hoh70Effects.Clear(); _hoh70Puddle?.Reset(); return;
        }
        if (!EnsureHoh70Observer()) return; // Never acquire without a working resolution observer.
        _hoh70WatchedBoss = boss.EntityId;
        _hoh70Puddle ??= new(SetBossMovementOverride, point => {
            _ddHost?.Context?.Navigator.CancelAll();
            _hoh70Input ??= new MovementInputController { IsAutoMove = false, Precision = .1f };
            _hoh70Input.DesiredPosition = point; _hoh70Input.Enabled = true;
        }, StopHoh70Input, p => _hohOrbitGeometry.GroundPoint(p, .15f, .25f),
            (from, to) => CombatPositionNavigation.Sight(from, to, out _) == CombatSight.Clear,
            reason => {
                Service.Log.Info("[HOH70.Puddle] " + reason);
                _ddHost?.Context?.RecordDiagnostic?.Invoke("hoh70-puddle-stage", new { reason });
            });
        var puddles = Service.GameObjects.Where(o => o.BaseId == Hoh70PuddleController.PuddleBaseId &&
                o.Address != IntPtr.Zero && ((GameObject*)o.Address)->EventState != 7)
            .Select(o => new Hoh70Puddle(o.GameObjectId, o.Position)).ToArray();
        while (_hoh70Effects.TryDequeue(out var effect))
        {
            _hoh70Puddle.ObserveAction(effect.Boss, effect.Action);
            _ddHost?.Context?.RecordDiagnostic?.Invoke("hoh70-puddle-action", new { effect.Boss, effect.Action, effect.Sequence });
        }
        bool mini = player.StatusList.Any(s => s.StatusId == Hoh70PuddleController.MiniStatusId);
        var now = DateTime.UtcNow;
        try
        {
            _hoh70Puddle.Update(new(true, state.DungeonId, state.Floor, true, true, boss.GameObjectId,
                boss.BaseId, true, player.Position, boss.IsCasting ? boss.CastActionId : 0,
                boss.CastTargetObjectId == player.GameObjectId, mini, puddles), now);
            if (_hoh70Puddle.OwnsMovement && now >= _nextHoh70Snapshot)
            {
                _nextHoh70Snapshot = now.AddMilliseconds(500);
                var p = player.Position; var target = _hoh70Puddle.Destination;
                _ddHost?.Context?.RecordDiagnostic?.Invoke("hoh70-puddle-snapshot", new {
                    stage = _hoh70Puddle.Stage, mini, position = new { p.X, p.Y, p.Z },
                    destination = target is { } t ? new { t.X, t.Y, t.Z } : null,
                    castActionId = boss.IsCasting ? boss.CastActionId : 0,
                    castRemaining = boss.IsCasting ? boss.TotalCastTime - boss.CurrentCastTime : 0,
                    puddles = puddles.Select(p => new { p.Id, p.Position.X, p.Position.Y, p.Position.Z }).ToArray(),
                    owner = "NewFolder3", bmr = "suspended" });
            }
        }
        catch (Exception error)
        {
            _hoh70Puddle.Reset();
            Service.Log.Error(error, "[HOH70.Puddle] Handoff failed; returned to BMR");
        }
    }

    private unsafe bool EnsureHoh70Observer()
    {
        if (_hoh70ObserverUnavailable) return false;
        if (_hoh70Hook != null) return true;
        try
        {
            _hoh70Hook = Service.GameInteropProvider.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                ActionEffectHandler.Addresses.Receive.Value, ReceiveHoh70Effect);
            NativeDeepDungeonLogMessageSource.EnableHook(_hoh70Hook, "HOH70.Puddle.ActionEffect");
            return true;
        }
        catch (Exception error)
        {
            _hoh70Hook?.Dispose(); _hoh70Hook = null; _hoh70ObserverUnavailable = true;
            Service.Log.Error(error, "[HOH70.Puddle] Observer unavailable; retaining BMR");
            return false;
        }
    }
    private unsafe void ReceiveHoh70Effect(uint source, Character* caster, Vector3* position,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        try
        {
            if (_hoh70WatchedBoss != 0 && source == _hoh70WatchedBoss && header != null &&
                header->ActionType == 1 && header->ActionId is 12204 or 12206 or 12208 && _hoh70Effects.Count < 32)
                _hoh70Effects.Enqueue((source, header->ActionId, header->GlobalSequence));
        }
        finally { _hoh70Hook!.Original(source, caster, position, header, effects, targets); }
    }
    private void StopHoh70Input() { if (_hoh70Input != null) _hoh70Input.Enabled = false; }
    private void DisposeHoh70Puddle()
    {
        _hoh70WatchedBoss = 0;
        try { _hoh70Puddle?.Dispose(); }
        finally
        {
            _hoh70Hook?.Dispose(); _hoh70Hook = null;
            _hoh70Input?.Dispose(); _hoh70Input = null; _hoh70Effects.Clear();
        }
    }
}
