using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

/// <summary>Directional boss balance, mechanic-first BMR handoff and actual-color targeting.</summary>
internal sealed class Pt99CombatController
{
    private DateTime _nextScan, _nextLog;
    private bool _suppressed;
    private bool _lastLight, _lastDark, _hasColorObservation, _allowPrepullFloorRoute;
    private ulong _appliedTargetId;
    private Pt99TargetKind? _lastKind;
    private IBattleChara? _target;
    private readonly Pt99MechanicGuard _mechanics = new();
    private readonly Pt99ColorMovementController _colors = new();
    private DateTime _nextAutoStop;
    internal Pt99TargetDecision Decision { get; private set; }

    internal void Reset(RunContext? context)
    {
        _colors.Reset(context);
        _mechanics.Reset();
        if (_suppressed) context?.SetBossRotationSuppressed?.Invoke(false);
        _suppressed = false;
        _target = null;
        _lastKind = null;
        _nextScan = _nextLog = _nextAutoStop = default;
        _hasColorObservation = _lastLight = _lastDark = _allowPrepullFloorRoute = false;
        _appliedTargetId = 0;
        Decision = default;
    }

    internal void Tick(RunContext context, out string status)
    {
        var player = Service.LocalPlayer;
        if (context.StatusIsError) { status = context.StatusLine; return; }
        if (player == null || player.IsDead ||
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas] ||
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
        { Reset(context); status = "PT99 - waiting for a ready player"; return; }
        var now = DateTime.UtcNow;
        bool inCombat = Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat];
        bool light = false, dark = false, movementForbidden = false;
        foreach (var buff in player.StatusList)
        {
            light |= buff.StatusId == Pt99TargetPolicy.LightStatusId;
            dark |= buff.StatusId == Pt99TargetPolicy.DarkStatusId;
            movementForbidden |= buff.StatusId is 4510 or 4562;
        }
        bool colorChanged = !_hasColorObservation || light != _lastLight || dark != _lastDark;
        if (colorChanged)
        {
            _hasColorObservation = true; _lastLight = light; _lastDark = dark;
            context.RecordDiagnostic?.Invoke("pt99-color-observed", new { light, dark, observedUtc = now });
        }
        // A changed actual buff bypasses the HP scan throttle: apply its target in this tick.
        if (now >= _nextScan || colorChanged)
        {
            _nextScan = now.AddMilliseconds(100);
            IBattleChara? grief = null, eater = null, minion = null;
            bool anyEnemyCasting = false;
            float minionDistance = float.MaxValue;
            foreach (var obj in Service.GameObjects)
            {
                if (obj is not IBattleNpc enemy || enemy.IsDead ||
                    Vector3.DistanceSquared(player.Position, enemy.Position) > 100 * 100) continue;
                if (enemy.IsCasting)
                {
                    anyEnemyCasting = true;
                    _mechanics.ObserveCast(enemy.CastActionId, enemy.TotalCastTime - enemy.CurrentCastTime, now);
                }
                switch (enemy.BaseId)
                {
                    case Pt99TargetPolicy.GriefBaseId: grief = enemy; break;
                    case Pt99TargetPolicy.EaterBaseId: eater = enemy; break;
                    case Pt99TargetPolicy.MinionBaseId when enemy.IsTargetable:
                        float distance = Vector3.DistanceSquared(player.Position, enemy.Position);
                        if (distance < minionDistance) { minion = enemy; minionDistance = distance; }
                        break;
                }
            }
            _allowPrepullFloorRoute = Pt99TargetPolicy.CanUsePrepullFloorRoute(inCombat,
                grief?.CurrentHp ?? 0, grief?.MaxHp ?? 0, eater?.CurrentHp ?? 0, eater?.MaxHp ?? 0, anyEnemyCasting);
            Decision = Pt99TargetPolicy.Decide(light, dark, grief?.CurrentHp ?? 0, grief?.MaxHp ?? 0,
                eater?.CurrentHp ?? 0, eater?.MaxHp ?? 0, minion != null, inCombat);
            _target = Decision.Target switch
            {
                Pt99TargetKind.Minion => minion,
                Pt99TargetKind.Grief when grief?.IsTargetable == true => grief,
                Pt99TargetKind.Eater when eater?.IsTargetable == true => eater,
                _ => null
            };
            bool suppress = Decision.SuppressOutput || _target == null;
            if (_lastKind != Decision.Target || now >= _nextLog)
            {
                _lastKind = Decision.Target;
                _nextLog = now.AddSeconds(2);
                Service.Log.Info($"[PT99.Target] choice={Decision.Target}, target={_target?.GameObjectId}, light={light}, dark={dark}, grief={grief?.CurrentHp}/{grief?.MaxHp}, eater={eater?.CurrentHp}/{eater?.MaxHp}, hpDifference={Decision.HpDifference:F2}, requestedColor={Decision.RequestedColor}, suppressed={suppress}");
                context.RecordDiagnostic?.Invoke("pt99-target-decision", new { choice = Decision.Target.ToString(),
                    requestedColor = Decision.RequestedColor.ToString(), targetId = _target?.GameObjectId,
                    light, dark, griefHp = grief?.CurrentHp, griefMaxHp = grief?.MaxHp,
                    eaterHp = eater?.CurrentHp, eaterMaxHp = eater?.MaxHp,
                    Decision.HpDifference, suppressed = suppress, observedUtc = now,
                    mechanicBlockedUntil = _mechanics.BlockedUntil, spatialMechanicUntil = _mechanics.SpatialUntil, movementForbidden });
            }
        }
        Service.TargetManager.Target = _target is { IsDead: false, IsTargetable: true } ? _target : null;
        ulong appliedTargetId = Service.TargetManager.Target?.GameObjectId ?? 0;
        if (appliedTargetId != _appliedTargetId || colorChanged)
        {
            _appliedTargetId = appliedTargetId;
            context.RecordDiagnostic?.Invoke("pt99-target-applied", new { targetId = appliedTargetId,
                choice = Decision.Target.ToString(), light, dark, appliedUtc = DateTime.UtcNow });
        }
        // Target is committed before output eligibility. There is no artificial delay for in-flight actions.
        bool outputSuppressed = Decision.SuppressOutput || _target == null || appliedTargetId == 0 ||
            appliedTargetId != _target.GameObjectId;
        if (outputSuppressed != _suppressed || colorChanged)
        {
            _suppressed = outputSuppressed;
            context.SetBossRotationSuppressed?.Invoke(outputSuppressed);
            context.RecordDiagnostic?.Invoke("pt99-output-eligibility", new { suppressed = outputSuppressed,
                targetId = appliedTargetId, light, dark, observedUtc = DateTime.UtcNow });
        }
        if (_suppressed && now >= _nextAutoStop)
        {
            _nextAutoStop = now.AddMilliseconds(500);
            OmenTools.Interop.Game.ExecuteCommand.Implementations.AutoAttackCommand.Disable();
        }
        _colors.Update(context, Decision.RequestedColor, _mechanics.IsBlocked(now) || movementForbidden,
            Decision.SuppressOutput, _allowPrepullFloorRoute && !inCombat, now);
        if (context.StatusIsError) { status = context.StatusLine; return; }
        if (!_suppressed && _target != null && !Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat])
            context.PrepareCombat(_target.GameObjectId);
        status = $"PT99 - {Decision.Target} (HP difference {Decision.HpDifference:F1}%)" +
            (Decision.RequestedColor != Pt99Color.None ? $" / {_colors.Status}" : string.Empty);
    }
}
