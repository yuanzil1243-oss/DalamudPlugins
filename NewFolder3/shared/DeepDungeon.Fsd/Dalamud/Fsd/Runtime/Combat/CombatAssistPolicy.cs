using System;
using System.Numerics;
using global::Dalamud.Plugin.Ipc;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat
{
/// <summary>
/// Shared combat-assist policy used by both manual banded farming and FullSelfDelving controllers.
/// Encapsulates target selection and out-of-combat attract casting based on configuration.
/// Run-scoped state is maintained between calls.
/// </summary>
internal sealed partial class CombatAssistPolicy
	{
        private readonly Pt99CombatController _pt99 = new();
        private readonly HohStoneskinPriority _hohStoneskin = new();
        internal ulong HohStoneskinTargetId { get; private set; }
        private ulong _lastHohStoneskinTargetId;
        internal DeepDungeon.Fsd.Core.Pt99TargetDecision Pt99Decision => _pt99.Decision;
        internal void Reset(RunContext? context)
        {
            _pt99.Reset(context);
            _hohStoneskin.BeginFloor(null, 0, 0);
            HohStoneskinTargetId = _lastHohStoneskinTargetId = 0;
            context?.HohCombatTargets.ResetSelection();
            ResetHohStoneskinControl();
            context?.HohShieldBreak.Reset();
        }
		private DateTime _nextAttractCastAt = DateTime.MinValue;
		private bool _prevInCombat = false;
		private uint _cachedEngageRangeSkillId = uint.MaxValue;
		private float _cachedEngageRange;
        private DateTime _nextDiagnosticAt;
        private DateTime _lastDiagnosticAt;
        private string _lastDiagnosticStatus = string.Empty;
        private ulong _lastDiagnosticTarget;

    public void Tick(FsdSettings configuration, RunContext? context, bool isBossFloor, bool passageOpen,
        out string status, out string selectStatus, out string attractStatus)
    {
        TickCore(configuration, context, isBossFloor, passageOpen, out status, out selectStatus, out attractStatus);
        UpdateHohStoneskinControl(configuration, context);
        UpdateHohShieldBreak(configuration, context);
        if (context?.RecordDiagnostic == null) return;
        var now = DateTime.UtcNow;
        if (now - _lastDiagnosticAt < TimeSpan.FromSeconds(1)) return;
        var player = Service.LocalPlayer;
        var target = Service.TargetManager.Target as IBattleChara;
        ulong targetId = target?.GameObjectId ?? 0;
        if (now < _nextDiagnosticAt && status == _lastDiagnosticStatus && targetId == _lastDiagnosticTarget) return;
        _nextDiagnosticAt = now.AddSeconds(5);
        _lastDiagnosticAt = now;
        _lastDiagnosticStatus = status;
        _lastDiagnosticTarget = targetId;
        context.RecordDiagnostic("combat-assist-state", new {
            floor = context.Duty.Floor, status, selectStatus, attractStatus,
            configuration.NecromancerBandedAutoSelect, configuration.NecromancerBandedAutoAttract,
            requestedActionId = configuration.NecromancerBandedAttractSkillId,
            inCombat = Service.Condition[ConditionFlag.InCombat], passageOpen,
            playerAvailable = player != null, playerDead = player?.IsDead, casting = player?.IsCasting,
            currentTargetId = targetId, preferredTargetId = context.TryGetPreferredAggroTarget(out var preferred) ? preferred : 0,
            targetHp = target?.CurrentHp, targetMaxHp = target?.MaxHp, targetable = target?.IsTargetable,
            targetCastActionId = target?.IsCasting == true ? target.CastActionId : 0,
            targetStatusIds = target?.StatusList.Where(s => s.StatusId != 0).Select(s => s.StatusId).ToArray(),
            hohStoneskinTargetId = HohStoneskinTargetId,
            playerPosition = player == null ? null : new { player.Position.X, player.Position.Y, player.Position.Z },
            targetPosition = target == null ? null : new { target.Position.X, target.Position.Y, target.Position.Z },
            nextAttemptUtc = _nextAttractCastAt.ToUniversalTime() });
    }

	/// <summary>
	/// Apply combat-assist behavior for the current tick.
	/// </summary>
	/// <param name="configuration">Global configuration (Necromancer* options).</param>
	/// <param name="context">Current run context.</param>
	/// <param name="isBossFloor">Whether the current floor is treated as a boss floor.</param>
	/// <param name="passageOpen">If true, passage is open; combat assist will not engage new targets.</param>
	/// <param name="status">Primary status line (combined select/attract summary).</param>
	/// <param name="selectStatus">Status line focused on target selection.</param>
	/// <param name="attractStatus">Status line focused on attract casting.</param>
	private void TickCore(FsdSettings configuration, RunContext? context, bool isBossFloor, bool passageOpen,
		out string status, out string selectStatus, out string attractStatus)
		{
		status = "Assist: Idle";
		selectStatus = configuration.NecromancerBandedAutoSelect ? "active" : "off";
		attractStatus = configuration.NecromancerBandedAutoAttract ? "active" : "off";

		try
		{
			var player = Service.LocalPlayer;
            _hohStoneskin.BeginFloor(context?.FarmingPlan?.Mode, context?.Duty.DungeonId ?? 0, context?.Duty.Floor ?? 0);
            HohStoneskinTargetId = 0;
			if (player == null || player.IsDead)
			{ _pt99.Reset(context); return; }

            if (configuration.BossMechanics.UsesBmrBossHandling() &&
                context?.Duty is { DungeonId: 4, Floor: 99 })
            {
                _pt99.Tick(context, out status);
                selectStatus = status;
                attractStatus = "PT99 - external rotation only";
                return;
            }
            _pt99.Reset(context);
            if (context?.Duty.DungeonId == 2 && context.HohPetrification.Clearing)
            {
                var stoneTarget = context.SelectHohPetrifiedTarget(Service.GameObjects.OfType<IBattleChara>()
                    .Where(e => e.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                        (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)e.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant &&
                        e.IsTargetable && !e.IsDead && e.CurrentHp > 0 && RunContext.IsHohPetrified(e)), player.Position);
                bool ready = context.PrepareHohPetrifiedOutput(stoneTarget, player.Position);
                status = ready ? "Assist: attacking petrified enemy while approaching" : stoneTarget != null
                    ? "Assist: selecting petrified enemy while approaching" : "Assist: waiting for petrification targets";
                selectStatus = status; attractStatus = "Petrification clear";
                return;
            }

			bool inCombat = Service.Condition[ConditionFlag.InCombat] || context?.HohCombatInProgress == true;
            if (!inCombat) _stickyPt10Add = 0;
			float engageRange = context?.CombatEngageRange ?? GetCachedEngageRange(configuration);
			if (engageRange <= 0f)
			{
				status = "Assist: configured attract action has no valid cast range";
				selectStatus = "Unavailable →invalid attract action range";
				attractStatus = "Unavailable →invalid attract action range";
				return;
			}
			ulong preferredAggroId = 0;
			bool hasPreferredAggro = context?.TryGetPreferredAggroTarget(out preferredAggroId) ?? false;
			bool IsAllowedCombatTarget(IBattleChara target) => context?.IsCombatTargetSuppressed(target.GameObjectId) != true &&
                context?.IsHohPetrificationTargetAllowed(target) != false &&
                (context?.WisdomConfirmed != true || context.IsEngagedCombatTarget(target));
			bool IsAllowedNewTarget(IBattleChara target) => IsAllowedCombatTarget(target) && context?.IsNewCombatTargetAllowed(target) != false;
			IBattleChara? preferredOocTarget = null;
			bool preferredOocTargetWithinRange = false;
			if (!inCombat && hasPreferredAggro)
			{
				var candidate = CombatTargetingHelpers.GetBattleCharaByGameObjectId(preferredAggroId);
				if (candidate is { IsTargetable: true, IsDead: false } &&
				    IsAllowedNewTarget(candidate))
				{
					preferredOocTarget = candidate;
					var dx = candidate.Position.X - player.Position.X;
					var dz = candidate.Position.Z - player.Position.Z;
					preferredOocTargetWithinRange =
						dx * dx + dz * dz <= engageRange * engageRange;
				}
			}

			IBattleChara? TryPickPreferredAggro(float range, out bool withinRange)
			{
				if (!hasPreferredAggro)
				{
					withinRange = false;
					return null;
				}

				var selected = CombatTargetingHelpers.PickAggroedHostile(range, out withinRange, preferredAggroId);
				if (selected != null && !IsAllowedCombatTarget(selected))
				{
					withinRange = false;
					return null;
				}

				return selected;
			}

			IBattleChara? PickMobCombatTarget()
			{
                if (context?.HohTargetingEnabled == true)
                {
                    var chosen = context.ResolveHohCombatTarget(hasPreferredAggro ? preferredAggroId : 0, !passageOpen);
                    HohStoneskinTargetId = chosen != null && HohStoneskinPriority.IsCaster(chosen.BaseId, chosen.NameId)
                        ? chosen.GameObjectId : 0;
                    if (HohStoneskinTargetId != 0 && _lastHohStoneskinTargetId != HohStoneskinTargetId)
                    {
                        _lastHohStoneskinTargetId = HohStoneskinTargetId;
                        context.RecordDiagnostic?.Invoke("hoh-stoneskin-target-changed", new {
                            floor = context.Duty.Floor, targetId = HohStoneskinTargetId, source = "shared-hoh-target-arbitration" });
                    }
                    if (chosen != null) context.EnsureHohCombatTarget(chosen);
                    return chosen;
                }
                if (context?.Duty.DungeonId == 4 && DeepDungeon.Fsd.Core.ProgressionModePolicy.IsProgression(context.FarmingPlan?.Mode))
                    return context.MobCombat.OutputTarget is { IsDead: false, IsTargetable: true } chosen ? chosen : null;
                var stoneskin = PickHohStoneskinTarget(context, float.MaxValue, IsAllowedCombatTarget);
                if (stoneskin != null) return stoneskin;
                if (context?.Duty.DungeonId == 2 && hasPreferredAggro &&
                    CombatTargetingHelpers.GetBattleCharaByGameObjectId(preferredAggroId) is { IsDead: false, IsTargetable: true } preferred &&
                    IsAllowedCombatTarget(preferred) && context.IsEngagedCombatTarget(preferred)) return preferred;
				// Keep reachable targets first, but retain a distant aggro target for BMR to approach.
				// Stationary/ranged enemies do not necessarily follow us into spell range.
				var target = CombatTargetingHelpers.PickHostileHighestHP(engageRange, onlyInRange: true,
					mustBeInCombat: true, IsAllowedCombatTarget);
				if (target != null) return target;
				var distant = CombatTargetingHelpers.PickAggroedHostile(float.MaxValue, out _, preferredAggroId);
				return distant != null && IsAllowedCombatTarget(distant) ? distant : null;
			}

			if (passageOpen)
			{
				if (inCombat)
				{
					if (configuration.NecromancerBandedAutoSelect)
					{
						IBattleChara? target = isBossFloor
							? PickBossTarget(context, IsAllowedCombatTarget)
							: PickMobCombatTarget();
						
					if (target != null)
					{
						try { Service.TargetManager.Target = target; } catch { }
					}

					string tname = target?.Name?.TextValue ?? "無目標";
					status = isBossFloor 
						? "Assist: Passage open - finishing combat (lowest max HP, central)"
						: HohStoneskinTargetId != 0 ? "Assist: Passage open - finishing combat (石肤优先)"
                            : "Assist: Passage open - finishing combat (highest HP)";
						selectStatus = target != null
							? $"Finishing combat →{tname}"
							: "Finishing combat →no target";
						attractStatus = "Disabled →Passage open";
						
						_prevInCombat = inCombat;
						return;
					}
					else
					{
						status = "Assist: Passage open - in combat (select disabled)";
						selectStatus = "Disabled →Passage open";
						attractStatus = "Disabled →Passage open";
						_prevInCombat = inCombat;
						return;
					}
				}
				else
				{
					var nearestHostile = TryPickPreferredAggro(engageRange * 2.5f, out bool withinRange)
					                     ?? (context?.WisdomConfirmed == true ? null : CombatTargetingHelpers.PickNearestHostile(engageRange * 2f, out withinRange, IsAllowedNewTarget));
					
					if (nearestHostile != null && !withinRange)
					{
						if (configuration.NecromancerBandedAutoSelect)
						{
							try { Service.TargetManager.Target = nearestHostile; } catch { }
						}
						
						string tname = nearestHostile.Name?.TextValue ?? "無名";
						status = "Assist: Passage open - waiting for navigation to remaining mob";
						selectStatus = $"Awaiting nav →{tname}";
						attractStatus = "Disabled →Passage open";
						_prevInCombat = inCombat;
						return;
					}
					else
					{
						status = "Assist: Passage open (idle)";
						selectStatus = "Disabled →Passage open";
						attractStatus = "Disabled →Passage open";
						_prevInCombat = inCombat;
						return;
					}
				}
			}

			if (_prevInCombat && !inCombat)
				_nextAttractCastAt = DateTime.MinValue;

			if (configuration.NecromancerBandedAutoSelect)
				{
					if (inCombat)
					{
						IBattleChara? target = isBossFloor
							? PickBossTarget(context, IsAllowedCombatTarget)
							: PickMobCombatTarget();
					if (target != null)
					{
						try { Service.TargetManager.Target = target; } catch { }
					}

			string tname = target?.Name?.TextValue ?? "無目標";
			if (isBossFloor)
				{
					status = "Assist: In combat (最低血量上限、中央目標)";
					selectStatus = target != null
						? $"In combat (lowest max HP, central) →{tname}"
						: "In combat (lowest max HP, central) →no target";
				}
				else
				{
					status = HohStoneskinTargetId != 0 ? "Assist: In combat (石肤优先)" : "Assist: In combat (最高HP目標)";
					selectStatus = target != null
						? $"In combat ({(HohStoneskinTargetId != 0 ? "Stoneskin priority" : "highest HP")}) →{tname}"
						: "In combat (highest HP) →no target";
				}
					}
					else
					{
					bool withinRange;
					var target = context?.HohTargetingEnabled == true
                        ? context.ResolveHohCombatTarget(preferredAggroId, !passageOpen) : preferredOocTarget;
                    if (context?.HohTargetingEnabled == true && target != null)
                    {
                        var delta = target.Position - player.Position;
                        preferredOocTargetWithinRange = delta.X * delta.X + delta.Z * delta.Z <= engageRange * engageRange;
                    }
					if (target != null)
					{
						withinRange = preferredOocTargetWithinRange;
					}
					else
					{
						target = CombatTargetingHelpers.PickNearestHostile(engageRange, out withinRange, IsAllowedNewTarget);
					}
					if (target != null)
					{
						if (context?.HohTargetingEnabled == true) context.EnsureHohCombatTarget(target);
                        else try { Service.TargetManager.Target = target; } catch { }

				string tname = target.Name?.TextValue ?? "無名";
				status = withinRange
					? "Assist: OOC target selected (in range)"
					: "Assist: OOC target selected (awaiting navigation)";
				selectStatus = $"OOC target →{tname} (in range: {withinRange})";
			}
				else
				{
					status = "Assist: OOC no target in range";
						if (Service.TargetManager.Target is IBattleChara previous && !IsAllowedNewTarget(previous))
							Service.TargetManager.Target = null;
					selectStatus = "No hostile in range";
				}
					}
				}

                if (!inCombat && configuration.NecromancerBandedAutoSelect &&
                    Service.TargetManager.Target is IBattleChara selected && !selected.IsDead &&
                    IsAllowedNewTarget(selected) &&
                    System.Numerics.Vector2.DistanceSquared(new(player.Position.X, player.Position.Z),
                        new(selected.Position.X, selected.Position.Z)) <= engageRange * engageRange)
                    context?.PrepareCombat(selected.GameObjectId);

				if (!inCombat && configuration.NecromancerBandedAutoAttract)
				{
					uint sid = configuration.NecromancerBandedAttractSkillId;
					if (sid != 0)
					{
						IGameObject? tgt;
						bool targetOk;
						if (preferredOocTarget != null)
						{
							tgt = preferredOocTarget;
							targetOk = preferredOocTargetWithinRange;
						}
						else
						{
							tgt = Service.TargetManager.Target;
							targetOk = false;
							if (tgt is IBattleChara tbc && !tbc.IsDead && IsAllowedNewTarget(tbc))
							{
								var dx = tgt.Position.X - player.Position.X;
								var dz = tgt.Position.Z - player.Position.Z;
								targetOk = (dx * dx + dz * dz) <= (engageRange * engageRange);
							}
						}
						if (!targetOk)
						{
							if (preferredOocTarget != null)
							{
								tgt = null;
							}
							else
							{
								var pick = CombatTargetingHelpers.PickNearestHostile(engageRange, out bool withinRange, IsAllowedNewTarget);
								if (pick != null && withinRange)
								{
									try { Service.TargetManager.Target = pick; } catch { }
									tgt = pick;
									targetOk = true;
								}
								else
								{
									tgt = null;
								}
							}
						}

					if (tgt != null && DateTime.Now >= _nextAttractCastAt)
					{
                        context?.PrepareCombat(tgt.GameObjectId);
                        var attempt = DeepDungeon.Fsd.Dalamud.Actions.FsdActionExecutor.CastWithDiagnostics(sid, tgt.GameObjectId);
                        bool ok = attempt.Accepted;
                        context?.RecordDiagnostic?.Invoke("combat-pull-attempt", new {
                            floor = context.Duty.Floor, targetId = tgt.GameObjectId,
                            currentTargetId = Service.TargetManager.Target?.GameObjectId,
                            targetPosition = new { tgt.Position.X, tgt.Position.Y, tgt.Position.Z },
                            playerPosition = new { player.Position.X, player.Position.Y, player.Position.Z }, engageRange,
                            inCombat, casting = player.IsCasting, attempt,
                            betweenAreas = Service.Condition[ConditionFlag.BetweenAreas],
                            betweenAreas51 = Service.Condition[ConditionFlag.BetweenAreas51] });
						_nextAttractCastAt = DateTime.Now.AddSeconds(2.0);
						status = $"Assist: Cast {(ok ? "OK" : "FAIL")}";
						attractStatus = $"Cast {(ok ? "OK" : "FAIL")}";
					}
					else
					{
						if (tgt == null)
							status = "Assist: no in-range target";
						else
							status = "Assist: waiting GCD";
						attractStatus = tgt == null ? "No in-range target" : "Waiting GCD";
					}
					}
				}

				_prevInCombat = inCombat;
			}
			catch (Exception ex)
			{
			status = "Assist: error (CombatAssistPolicy)";
			context?.RecordDiagnostic?.Invoke("combat-assist-error", new { error = ex.ToString() });
			try { Service.Log.Error($"[CombatAssistPolicy] Tick error: {ex}"); } catch { }
			}
		}

	/// <summary>
	/// Computes the cast range of the configured attract skill.
	/// </summary>
    private IBattleChara? PickHohStoneskinTarget(RunContext? context, float range, Func<IBattleChara, bool> allowed)
    {
        if (!HohStoneskinPriority.Enabled(context?.FarmingPlan?.Mode, context?.Duty.DungeonId ?? 0,
                context?.Duty.Floor ?? 0) || Service.LocalPlayer is not { } player) return null;
        var pool = new List<(IBattleChara Target, HohStoneskinCandidate Candidate)>();
        foreach (var obj in Service.GameObjects)
        {
            if (obj is not IBattleNpc enemy || !enemy.IsTargetable || enemy.IsDead || enemy.CurrentHp == 0 ||
                (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)enemy.SubKind !=
                    global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant || !allowed(enemy)) continue;
            bool engaged = enemy.TargetObjectId == player.GameObjectId ||
                DeepDungeon.Fsd.Dalamud.Runtime.Floor.EnemyChaseHelper.IsAggroedToPlayer(enemy.GameObjectId);
            if (!engaged) continue;
            int priority = _hohStoneskin.Observe(enemy.BaseId, enemy.NameId, enemy.IsCasting, enemy.CastActionId);
            if (priority == 0) continue;
            float distance = Vector2.DistanceSquared(new(player.Position.X, player.Position.Z),
                new(enemy.Position.X, enemy.Position.Z));
            pool.Add((enemy, new(enemy.GameObjectId, enemy.CurrentHp, distance, priority, true)));
        }
        ulong id = HohStoneskinPriority.Select(pool.Select(p => p.Candidate), range);
        var selected = pool.FirstOrDefault(p => p.Candidate.Id == id).Target;
        if (selected == null) return null;
        HohStoneskinTargetId = id;
        if (_lastHohStoneskinTargetId != id)
        {
            _lastHohStoneskinTargetId = id;
            context?.RecordDiagnostic?.Invoke("hoh-stoneskin-target-changed", new
            {
                floor = context.Duty.Floor, targetId = id, name = selected.Name.TextValue,
                baseId = (selected as IBattleNpc)?.BaseId, nameId = (selected as IBattleNpc)?.NameId,
                hp = selected.CurrentHp, priority = pool.First(p => p.Candidate.Id == id).Candidate.Priority,
                castActionId = selected.IsCasting ? selected.CastActionId : 0,
                statuses = selected.StatusList.Where(s => s.StatusId != 0).Select(s => s.StatusId).ToArray(),
                reason = "known-stoneskin-species-before-first-cast"
            });
        }
        return selected;
    }

	internal float GetCachedEngageRange(FsdSettings configuration)
	{
		uint sid = configuration.NecromancerBandedAttractSkillId;
		if (sid == _cachedEngageRangeSkillId)
			return _cachedEngageRange;

		_cachedEngageRangeSkillId = sid;
		_cachedEngageRange = ResolveEngageRange(sid);
		return _cachedEngageRange;
	}

    private ICallGateSubscriber<Vector2>? _arenaCenterIpc;
    private DateTime _nextArenaCenterReadAt;
    private int _arenaFloor = -1;
    private bool _arenaUsesBmr;
    private Vector2 _arenaCenter;
    private string _arenaCenterSource = "add-centroid";
    private ulong _lastBossTargetId;
        private ulong _stickyPt10Add;

    private IBattleChara? PickBossTarget(RunContext? context, Func<IBattleChara, bool> predicate)
    {
        var now = DateTime.UtcNow;
        int floor = context?.Duty.Floor ?? 0;
        bool useBmr = context?.Configuration.BossMechanics.UsesBmrBossHandling() == true;
        if (_arenaFloor != floor || _arenaUsesBmr != useBmr || now >= _nextArenaCenterReadAt)
        {
            _arenaFloor = floor;
            _arenaUsesBmr = useBmr;
            _nextArenaCenterReadAt = now.AddSeconds(1);
            bool hasArenaCenter = false;
            try
            {
                if (useBmr && (_arenaCenterIpc ??= Service.PluginInterface.GetIpcSubscriber<Vector2>("BossMod.Hints.ArenaCenter")).HasFunction)
                {
                    var center = _arenaCenterIpc.InvokeFunc();
                    var player = Service.LocalPlayer;
                    if (player != null && float.IsFinite(center.X) && float.IsFinite(center.Y) &&
                        Vector2.DistanceSquared(center, new Vector2(player.Position.X, player.Position.Z)) <= 100 * 100)
                    {
                        _arenaCenter = center;
                        _arenaCenterSource = "BMR-arena";
                        hasArenaCenter = true;
                    }
                }
            }
            catch { /* Optional IPC: custom movement providers use the add-group centroid. */ }
            if (!hasArenaCenter)
            {
                _arenaCenter = CombatTargetingHelpers.BossTargetFallbackCenter(predicate);
                _arenaCenterSource = "add-centroid";
            }
        }
        bool stabilizeAdds = context?.Duty.DungeonId == 4 && floor == 10;
        if (!stabilizeAdds) _stickyPt10Add = 0;
        var target = CombatTargetingHelpers.PickBossTarget(_arenaCenter, predicate, _stickyPt10Add,
            context == null ? 0 : GetCachedEngageRange(context.Configuration), stabilizeAdds);
        if (stabilizeAdds) _stickyPt10Add = target?.GameObjectId ?? 0;
        ulong id = target?.GameObjectId ?? 0;
        if (id != _lastBossTargetId)
        {
            _lastBossTargetId = id;
            Service.Log.Info($"[CombatAssist] Boss target floor={floor}, id={id}, hp={target?.CurrentHp}, maxHp={target?.MaxHp}, position={target?.Position}, center={_arenaCenter}, centerSource={_arenaCenterSource}, priority={(stabilizeAdds ? "adds-sticky-then-lowest-hp-then-center" : "lowest-max-hp-then-center")}");
        }
        return target;
    }

	private static float ResolveEngageRange(uint sid)
	{
		if (sid == 0)
			return 0f;

		var info = FsdSkillCatalog.GetOrRegister(sid);
		return ResolveConfiguredCastRange(info);
	}

	internal static float ResolveConfiguredCastRange(in FsdSkillInfo info) =>
		info.IsValid ? info.Range : 0f;
}
}
