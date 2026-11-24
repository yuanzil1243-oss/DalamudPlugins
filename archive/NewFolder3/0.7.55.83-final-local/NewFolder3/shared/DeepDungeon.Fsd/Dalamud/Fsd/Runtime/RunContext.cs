using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Combat;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime
{
	/// <summary>
	/// Per-loop instance context; contains runtime services and cancellation.
	/// Created once per dungeon run, survives until duty exit.
	/// </summary>
	public sealed partial class RunContext : IDisposable
	{
		public readonly FsdSettings Configuration;
		public readonly CancellationTokenSource CancellationSource;

		// Services
		public readonly DutyState Duty;
		internal readonly Func<IBattleChara, bool> CanSelectNewCombatTarget;
		public readonly DeepDungeonUi Ui;
		public readonly SaveSlotManager SaveSlots;
		public readonly INavigator Navigator;
		public readonly IRunOptionsProvider RunOptions;
		internal readonly DetailedMapRunSnapshot DetailedMap;
		internal readonly CombatAssistPolicy CombatAssist;
		internal readonly MobCombatController MobCombat;
		internal readonly DeathRecorder DeathRecorder;
		internal CombatRecoveryBudget CombatRecoveryBudget = new();
		internal readonly FsdChestInteraction ChestInteraction;
		internal ControlledPtSurveySession? ControlledPtSurvey;
        internal DeepDungeon.Fsd.Core.FarmingPlan? FarmingPlan;
        internal Func<bool, bool>? SetBossMovementOverride;
        internal bool Pt99InitialColorMovementActive;
        internal readonly DeepDungeon.Fsd.Runtime.HohPetrificationState HohPetrification = new();
        internal readonly DeepDungeon.Fsd.Runtime.HohRunContinuity HohContinuity = new();
        internal readonly DeepDungeon.Fsd.Runtime.HohShieldBreakState HohShieldBreak = new();
        internal bool ShieldBreakOutputPermitted;
        internal Action<bool>? SetBossRotationSuppressed;
        internal Action<string, object>? RecordDiagnostic;
        internal long CombatPreparationSequence { get; private set; }
        internal readonly WisdomCombatState WisdomCombat = new();
        private DateTime _nextWisdomAutoStop;
        private readonly HashSet<object> _hohRecoveryMovementOwners = new();
        internal bool HohRecoveryMovementActive => _hohRecoveryMovementOwners.Count > 0 &&
            HohCurrentDutyResumePolicy.UsesBoundedReposition(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor);
        internal void SetHohRecoveryMovement(object owner, bool active)
        {
            bool changed = active ? _hohRecoveryMovementOwners.Add(owner) : _hohRecoveryMovementOwners.Remove(owner);
            if (changed) RecordDiagnostic?.Invoke("hoh-recovery-movement-state", new {
                moving = HohRecoveryMovementActive, owners = _hohRecoveryMovementOwners.Count,
                floor = Duty.Floor, reason = active ? "validated-recovery-search-or-movement" : "output-validation-or-release" });
        }
        internal bool WisdomConfirmed
        {
            get { WisdomCombat.Sync(Duty.DungeonId, Duty.Floor); return WisdomCombat.Confirmed; }
        }
        internal bool PassageOpenForCombat => Duty.PassageOpen || WisdomConfirmed;
        internal bool NormalCombatOutputSuppressed => HohContinuity.HoldsControls || WisdomConfirmed && WisdomCombat.SuppressOutput(MobCombat.Engaged.Count) ||
            Duty.DungeonId == 2 && HohPetrification.StopAttackingAfterClear && !HohCombatInProgress;
        // HoH91+ also feeds the shared local-consolidation adapter. Earlier HoH floors
        // retain the native combat flag and existing target-of-player / hater check.
        internal bool HohCombatInProgress => Duty.DungeonId == 2 &&
            (Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ||
             HohTargetingEnabled && HohCombatTargets.HasEngagement ||
             Service.GameObjects.OfType<IBattleChara>().Any(e =>
                 e.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                 (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)e.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant &&
                 e.IsTargetable && !e.IsDead && e.CurrentHp > 0 && IsEngagedCombatTarget(e)));
        internal bool IsHohPetrificationTargetAllowed(IBattleChara target) => Duty.DungeonId != 2 ||
            HohHighFloorPolicy.Enabled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor) && IsEngagedCombatTarget(target) ||
            HohPetrification.AllowsTarget(HohPetrification.Clearing && IsHohPetrified(target),
                HohPetrification.StopAttackingAfterClear && IsEngagedCombatTarget(target));
        internal static bool IsHohPetrified(IBattleChara e) => e.StatusList.Any(s => s.StatusId == DeepDungeon.Fsd.Runtime.HohPetrificationState.StoneCurseStatus);
        internal bool IsHohPetrificationClearTarget(IBattleChara target) =>
            Hoh70FloorPolicy.Enabled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor) &&
            HohPetrification.Clearing && target.StatusList?.Any(s =>
                s.StatusId == DeepDungeon.Fsd.Runtime.HohPetrificationState.StoneCurseStatus) == true;

        internal IBattleChara? SelectHohPetrifiedTarget(IEnumerable<IBattleChara> candidates, Vector3 player)
        {
            candidates = candidates.Where(IsHohVoluntaryTargetAllowed);
            ulong preferred = TryGetPreferredAggroTarget(out var id) ? id : 0;
            return HohPetrifiedTargetPolicy.Select(candidates, IsEngagedCombatTarget,
                e => IsCombatTargetSuppressed(e.GameObjectId), e => Vector3.DistanceSquared(player, e.Position),
                e => e.GameObjectId == preferred,
                e => IsEngagedCombatTarget(e) && Hoh70FloorPolicy.IsCentaur(e.BaseId, e.NameId));
        }

        internal bool IsEngagedCombatTarget(IBattleChara target) =>
            HohTargetingEnabled ? HohCombatTargets.IsEngaged(target.GameObjectId) || IsNativeEngagedCombatTarget(target) :
                Floor.EnemyChaseHelper.IsAggroedToPlayer(target.GameObjectId) || target.TargetObjectId == Service.LocalPlayer?.GameObjectId;

        internal void EnforceWisdomTargetGate()
        {
            ClearUnsafeHohPullTargets();
            if (Duty.DungeonId == 2 && (HohPetrification.Clearing || HohPetrification.StopAttackingAfterClear))
            {
                bool blocked = false;
                if (Service.TargetManager.Target is IBattleChara ht && !IsHohPetrificationTargetAllowed(ht))
                { Service.TargetManager.Target = null; blocked = true; }
                if (Service.TargetManager.SoftTarget is IBattleChara hs && !IsHohPetrificationTargetAllowed(hs))
                { Service.TargetManager.SoftTarget = null; blocked = true; }
                if ((blocked || NormalCombatOutputSuppressed) && DateTime.UtcNow >= _nextWisdomAutoStop)
                { _nextWisdomAutoStop = DateTime.UtcNow.AddMilliseconds(500); OmenTools.Interop.Game.ExecuteCommand.Implementations.AutoAttackCommand.Disable(); }
            }
            if (!WisdomConfirmed) return;
            bool cleared = false;
            if (Service.TargetManager.Target is IBattleChara target &&
                !WisdomCombat.AllowsTarget(IsEngagedCombatTarget(target)))
            { Service.TargetManager.Target = null; cleared = true; }
            if (Service.TargetManager.SoftTarget is IBattleChara soft &&
                !WisdomCombat.AllowsTarget(IsEngagedCombatTarget(soft)))
            { Service.TargetManager.SoftTarget = null; cleared = true; }
            if (NormalCombatOutputSuppressed || cleared)
            {
                ClearPreferredAggroTarget();
                if (DateTime.UtcNow >= _nextWisdomAutoStop)
                {
                    _nextWisdomAutoStop = DateTime.UtcNow.AddMilliseconds(500);
                    OmenTools.Interop.Game.ExecuteCommand.Implementations.AutoAttackCommand.Disable();
                }
            }
        }
        private ulong _preparedCombatTarget;
        private bool _rotationWasInCombat;

        internal void PrepareCombat(ulong targetId)
        {
            if (DutyCompletionObserved || targetId == 0 || targetId == _preparedCombatTarget) return;
            _preparedCombatTarget = targetId;
            CombatPreparationSequence++;
        }

        internal void ObserveCombatState(bool inCombat)
        {
            if (_rotationWasInCombat && !inCombat) _preparedCombatTarget = 0;
            _rotationWasInCombat = inCombat;
        }
        internal bool HarvestComplete;
        internal bool TerminalRewardsRequired;
        internal bool TerminalRoomObserved;
        internal bool TerminalReviewRequested;
        internal bool AttemptAborted;
        internal string AttemptAbortReason = string.Empty;
        internal int HoardDiscoveries;
        internal string PreparedSaveDescription = string.Empty;

		// Status
		public string StatusLine = string.Empty;
		public bool StatusIsError = false;
        internal readonly ExplorationRecoveryPause ExplorationRecovery = new();
        internal void StopExplorationRecoverably(string reason)
        {
            StatusLine = reason; StatusIsError = true;
            if (FarmingPlan != null && Duty.IsInDuty && !Duty.IsBossFloor && Duty.Floor > 0 &&
                !DutyFailureObserved && !DutyCompletionObserved)
                ExplorationRecovery.Pause(Duty.DungeonId, Duty.Floor, reason);
        }
		public bool DutyCompletionObserved { get; private set; }
		internal long DutyCompletionSequence { get; private set; }
		public bool DutyFailureObserved { get; private set; }
		internal readonly DutyTimeoutEvidence TimeoutEvidence = new();
		internal string DutyFailureReason { get; private set; } = string.Empty;

		private readonly object _preferredAggroLock = new();
		private ulong _preferredAggroTargetId;
		private Vector3 _preferredAggroTargetPosition;
		private DateTime _preferredAggroExpiry = DateTime.MinValue;
		private readonly Dictionary<ulong, DateTime> _suppressedCombatTargets = new();

	internal RunContext(
		FsdSettings configuration,
		DutyState dutyState,
		DetailedMapRunSnapshot detailedMap, Func<nint>? currentDeepDungeon = null)
	{
		Configuration = configuration;
        _currentDeepDungeon = currentDeepDungeon ?? ReadCurrentDeepDungeon;
		DetailedMap = detailedMap ??
			throw new ArgumentNullException(nameof(detailedMap));
		CancellationSource = new CancellationTokenSource();
			Duty = dutyState;
			CanSelectNewCombatTarget = IsNewCombatTargetAllowed;
			Ui = new DeepDungeonUi();
			SaveSlots = new SaveSlotManager();
			Navigator = new NavigatorVNavAdapter();
			CombatAssist = new CombatAssistPolicy();
			MobCombat = new MobCombatController();
			DeathRecorder = new DeathRecorder();
			var defaults = new RunOptions
			{
				OpenGold = configuration.NecromancerAutoOpenGoldChest,
				OpenSilver = configuration.NecromancerAutoOpenSilverChest,
				OpenBronze = configuration.NecromancerAutoOpenBronzeChest,
				BandedEnabled = configuration.NecromancerAutoBandedFarmEnabled,
				LeaveMode = LeaveModeUiMapping.FromUiIndex(configuration.NecromancerAutoLeaveMode),
				LeaveAfterMinutes = Math.Max(0, configuration.NecromancerAutoLeaveAfterMinutes)
			};
			RunOptions = new RunOptionsProvider(defaults);
			ChestInteraction = new FsdChestInteraction(configuration, RunOptions);
		}

	public CancellationToken Token => CancellationSource.Token;

    // Existing hate is an obligation, even for a mob we would not voluntarily pull.
    internal int ControlledCombatEngagedCount => HohHighFloorPolicy.ControlsCombat(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor)
        ? Math.Max(MobCombat.Engaged.Count, Math.Max(HohCombatTargets.EngagedCount,
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] ? 1 : 0))
        : MobCombat.Engaged.Count;

    internal bool IsNewCombatTargetAllowed(IBattleChara target) =>
        (!HohCentaurFightActive || IsEngagedCombatTarget(target)) &&
        IsHohVoluntaryTargetAllowed(target) &&
        (Duty.DungeonId == 2 && (HohPetrification.Clearing || HohPetrification.StopAttackingAfterClear)
            ? IsHohPetrificationTargetAllowed(target) :
        PassageCombatPolicy.AllowsNewTarget(WisdomConfirmed,
            MobCombatPolicy.UsesControlledHighFloor(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor),
            PassageOpenForCombat, ControlledCombatEngagedCount, IsEngagedCombatTarget(target),
            MobCombat.IsLocalSupplementCandidate(this, target)) &&
        DeepDungeon.Fsd.Core.MobPullPolicy.CanInitiateControlled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor,
            target.NameId, MobCombat.Engaged.Count, MobCombat.DangerousEngaged,
            HohTargetingEnabled ? IsEngagedCombatTarget(target) :
                Floor.EnemyChaseHelper.IsAggroedToPlayer(target.GameObjectId) ||
                target.TargetObjectId == Service.LocalPlayer?.GameObjectId)) &&
        (Duty.DungeonId == 2 && HohPetrification.Clearing && IsHohPetrified(target) ||
            !HohHighFloorPolicy.ControlsCombat(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor) ||
            PassageCombatPolicy.AllowsNewTarget(WisdomConfirmed, true, PassageOpenForCombat,
                ControlledCombatEngagedCount, IsEngagedCombatTarget(target), MobCombat.IsLocalSupplementCandidate(this, target)));

	public void Cancel()
	{
		try { CancellationSource.Cancel(); } catch { }
	}

	/// <summary>
	/// Reset per-attempt state (error flags and observations) for a fresh start.
	/// Called by scenarios on Initialize() to clear stale state from previous attempts.
	/// 
	/// Does NOT reset:
	/// - DutyStateWatcher (tracks multi-loop state like floor, hoard count)
	/// - Navigator (persistent VNavmesh connection)
	/// - Configuration / RunOptions (user settings)
	/// </summary>
	public void ResetAttemptState()
	{
        HohContinuity.Reset();
        WisdomCombat.Reset();
        HohCombatTargets.Reset();
        HohPetrification.BeginFloor(0, 0);
        ExplorationRecovery.Clear();
		// Clear error state
		StatusIsError = false;
		StatusLine = string.Empty;
		DutyCompletionObserved = false;
		DutyCompletionSequence = 0;
		DutyFailureObserved = false;
		DutyFailureReason = string.Empty;
		TimeoutEvidence.Reset();
		
		ClearSuppressedCombatTargets();
		
		// Note: CancellationSource is NOT reset here because it's created once per RunContext lifetime
		// If we need cancellation, we dispose and create a new RunContext
	}

	public void Dispose()
	{
        _hohRecoveryMovementOwners.Clear();
        HohCombatTargets.Reset();
        try { CombatAssist.Reset(this); } catch { }
        WisdomCombat.Reset();
		try { MobCombat.Dispose(); } catch { }
		try { DeathRecorder.Dispose(); } catch { }
		SetBossRotationSuppressed?.Invoke(false);
		Cancel();
		try { (Navigator as IDisposable)?.Dispose(); }
		catch (Exception ex)
		{
			try { Service.Log.Error($"[RunContext] Failed to dispose navigator: {ex}"); } catch { }
		}
		try { CancellationSource.Dispose(); }
		catch (Exception ex)
		{
			try { Service.Log.Error($"[RunContext] Failed to dispose cancellation source: {ex}"); } catch { }
		}
	}

	public void MarkDutyCompleted()
	{
		if (DutyFailureObserved)
			return;

		DutyCompletionObserved = true;
		DutyCompletionSequence++;
	}


	public void MarkDutyFailed(string reason = "duty-failed")
	{
		if (!DutyFailureObserved) DutyFailureReason = reason;
		DutyFailureObserved = true;
		DutyCompletionObserved = false;
	}

	public void SetPreferredAggroTarget(ulong targetId, Vector3 position, TimeSpan? ttl = null)
	{
		if (targetId == 0)
			return;

		var expiry = DateTime.Now + (ttl ?? TimeSpan.FromSeconds(6));
		lock (_preferredAggroLock)
		{
			_preferredAggroTargetId = targetId;
			_preferredAggroTargetPosition = position;
			_preferredAggroExpiry = expiry;
		}
	}

	public void ClearPreferredAggroTarget()
	{
		lock (_preferredAggroLock)
		{
			_preferredAggroTargetId = 0;
			_preferredAggroTargetPosition = default;
			_preferredAggroExpiry = DateTime.MinValue;
		}
	}

	public bool TryGetPreferredAggroTarget(out ulong targetId)
	{
		lock (_preferredAggroLock)
		{
			if (_preferredAggroTargetId != 0 && DateTime.Now <= _preferredAggroExpiry)
			{
				targetId = _preferredAggroTargetId;
				return true;
			}
		}

		targetId = 0;
		return false;
	}

	public bool TryGetPreferredAggroTarget(out ulong targetId, out Vector3 position)
	{
		lock (_preferredAggroLock)
		{
			if (_preferredAggroTargetId != 0 && DateTime.Now <= _preferredAggroExpiry)
			{
				targetId = _preferredAggroTargetId;
				position = _preferredAggroTargetPosition;
				return true;
			}
		}

		targetId = 0;
		position = default;
		return false;
	}

	public void SuppressCombatTarget(ulong targetId, TimeSpan? ttl = null)
	{
		if (targetId == 0)
			return;

		lock (_preferredAggroLock)
		{
			PruneSuppressedCombatTargetsLocked();
			_suppressedCombatTargets[targetId] = DateTime.UtcNow + (ttl ?? TimeSpan.FromSeconds(20));
		}
	}

	public bool IsCombatTargetSuppressed(ulong targetId)
	{
		if (targetId == 0)
			return false;
        if (HohTargetingEnabled && (HohCombatTargets.IsEngaged(targetId) ||
            (Service.GameObjects?.OfType<IBattleChara>() ?? Enumerable.Empty<IBattleChara>()).Any(e => e.GameObjectId == targetId && IsNativeEngagedCombatTarget(e)))) return false;
        if (MobCombat.Engaged.Any(enemy => enemy.GameObjectId == targetId)) return false;
		if (CombatRecoveryBudget.IsTargetExhausted(targetId))
			return true;

		lock (_preferredAggroLock)
		{
			PruneSuppressedCombatTargetsLocked();
			return _suppressedCombatTargets.ContainsKey(targetId);
		}
	}

	public void ClearSuppressedCombatTargets()
	{
		lock (_preferredAggroLock)
		{
			_suppressedCombatTargets.Clear();
		}
	}

	private void PruneSuppressedCombatTargetsLocked()
	{
		if (_suppressedCombatTargets.Count == 0)
			return;

		var now = DateTime.UtcNow;
		Span<ulong> expired = stackalloc ulong[Math.Min(_suppressedCombatTargets.Count, 32)];
		int expiredCount = 0;
		foreach (var pair in _suppressedCombatTargets)
		{
			if (pair.Value > now)
				continue;

			if (expiredCount >= expired.Length)
				break;

			expired[expiredCount++] = pair.Key;
		}

		for (int i = 0; i < expiredCount; i++)
			_suppressedCombatTargets.Remove(expired[i]);
	}
	}
}

