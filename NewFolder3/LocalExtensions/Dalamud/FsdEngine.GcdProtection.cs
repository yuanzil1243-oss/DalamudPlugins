using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Actions;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;
using MasterPlayer.MechanicRuntime;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private readonly GcdStallWatchdog _gcdWatchdog = new();
    private RunContext? _gcdContext;
    private (uint Dungeon, byte Floor, uint Job) _gcdScope;
    private long _nextGcdPoll, _nextGcdDiagnostic;
    private string? _gcdDiagnosticReason;
    private ulong _gcdDiagnosticTarget;
    private bool _gcdDisabledByError;

    private unsafe void UpdateGcdStallProtection()
    {
        var ctx = _ddHost?.Context;
        if (!_configuration.Farming.GcdStallProtection || !IsRunActive || ctx == null)
        {
            _gcdWatchdog.Reset(); _gcdContext = null; _gcdDisabledByError = false;
            return;
        }
        long now = Environment.TickCount64;
        if (now < _nextGcdPoll || _gcdDisabledByError) return;
        _nextGcdPoll = now + 100;
        try
        {
            var state = _currentDeepDungeonState;
            var player = Service.LocalPlayer;
            var target = Service.TargetManager.Target as IBattleNpc;
            ulong targetId = target?.GameObjectId ?? 0;
            uint job = player?.ClassJob.RowId ?? 0;
            var scope = (state.DungeonId, state.Floor, job);
            if (!ReferenceEquals(_gcdContext, ctx) || _gcdScope != scope)
            {
                _gcdWatchdog.Reset(); _gcdContext = ctx; _gcdScope = scope;
                _gcdDiagnosticReason = null;
            }
            string reason = "eligible";
            var manager = ActionManager.Instance();
            var gcd = manager == null ? null : manager->GetRecastGroupDetail(57);
            bool gcdKnown = gcd != null && float.IsFinite(gcd->Elapsed) && float.IsFinite(gcd->Total);
            bool gcdActive = gcdKnown && gcd->IsActive && gcd->Total - gcd->Elapsed > 0.05f;
            uint action = GcdRecoveryActions.ForJob(job), adjusted = 0;
            uint? actionStatus = null, rangeStatus = null;
            if (!state.IsValid || !state.IsInDuty || !state.IsInDeepDungeonTerritory ||
                state.FloorKind is not (DeepDungeonFloorKind.Mob or DeepDungeonFloorKind.Boss) ||
                state.IsTransitioning || ctx.Duty.StateReadFailed || ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning)
                reason = "duty-not-ready";
            else if (player is not { IsDead: false, Address: not 0 } ||
                Service.Condition[ConditionFlag.WatchingCutscene] || Service.Condition[ConditionFlag.WatchingCutscene78] ||
                Service.Condition[ConditionFlag.OccupiedInCutSceneEvent] || Service.Condition[ConditionFlag.Jumping] ||
                Service.Condition[ConditionFlag.Jumping61]) reason = "player-not-ready";
            else if (!ctx.EntryReadyForDuty || ctx.StatusIsError || ctx.AttemptAborted ||
                ctx.DutyCompletionObserved || ctx.DutyFailureObserved || ctx.TerminalStayRequested ||
                _ddHost!.FloorController.AllowsCombatChannel != true) reason = "task-not-allowing-combat";
            else if (target is not { IsDead: false, IsTargetable: true, CurrentHp: > 0, Address: not 0 } ||
                targetId == 0 || ctx.CombatAssist.SelectedId != targetId ||
                !ctx.IsNewCombatTargetAllowed(target) || ctx.IsCombatTargetSuppressed(targetId))
                reason = "no-fsd-approved-target";
            else if (MechanicExecutionState.Restrictions != ExecutionRestrictions.None ||
                player.StatusList.Any(s => s.StatusId == 960)) reason = "mechanic-restriction";
            else if (!_rotationControl.DesiredEnabled || _configuration.Rotation.Provider is not
                (FsdRotationProvider.RotationSolverReborn or FsdRotationProvider.WrathCombo or FsdRotationProvider.PromeRotation) ||
                ReadRotationState(_configuration.Rotation.Provider) != FsdRotationState.On)
                reason = "output-off-or-stop-state-unreadable";
            else if (manager == null || !gcdKnown) reason = "gcd-observation-unavailable";
            else
            {
                var frame = _encounters.Read(target.EntityId, target.BaseId);
                if (state.FloorKind == DeepDungeonFloorKind.Boss &&
                    !DeepDungeonBossPolicy.UsesSpin(state.DungeonId, state.Floor) && frame.Readiness.Kind != EncounterReadinessKind.Ready)
                    reason = "boss-mechanics-" + frame.Readiness.Kind.ToString().ToLowerInvariant();
                else if (frame.Restrictions != ExecutionRestrictions.None)
                    reason = "encounter-restriction";
                else if (frame.Encounter is { BossDead: false, Hints: { } hints } snapshot &&
                    (snapshot.Territory != (ushort)Service.ClientState.TerritoryType ||
                     DateTime.UtcNow - snapshot.CapturedAt > TimeSpan.FromMilliseconds(500) ||
                     hints.HoldFire || hints.ForceCancelCast ||
                     hints.Mode == BossMod.AIHints.SpecialMode.Pyretic && hints.ModeActive(DateTime.UtcNow)))
                    reason = "encounter-hold-or-stale";
                else if (player.IsCasting || Service.Condition[ConditionFlag.Casting]) reason = "casting";
                else if (gcdActive) reason = "gcd-cooldown";
                else if (manager->ActionQueued || manager->AnimationLock > 0.05f) reason = "queued-or-animation-lock";
                else if (action == 0) reason = "unsupported-job";
                else
                {
                    adjusted = manager->GetAdjustedActionId(action);
                    var row = adjusted == 0 ? null : Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRowOrDefault(adjusted);
                    if (row is not { } data || !data.CanTargetHostile || data.Range <= 0 ||
                        data.CooldownGroup != 58 && data.AdditionalCooldownGroup != 58)
                        reason = "not-a-verified-hostile-gcd";
                    else
                    {
                        actionStatus = manager->GetActionStatus(ActionType.Action, adjusted, targetId);
                        rangeStatus = ActionManager.GetActionInRangeOrLoS(adjusted,
                            (NativeObject*)player.Address, (NativeObject*)target.Address);
                        if (actionStatus != Service.ActionStatus_Ready) reason = "action-not-ready";
                        else if (!Service.Available_GetActinoInRangeOrLoSStatus.Contains(rangeStatus.Value) ||
                            !CombatPositionNavigation.CheckAttack(player.Position, target.Position, data.Range).Allowed)
                            reason = "out-of-range-or-sight";
                    }
                }
            }
            bool attemptedBefore = _gcdWatchdog.Attempted;
            bool recover = _gcdWatchdog.Update(now, targetId, reason == "eligible", gcdActive);
            if (attemptedBefore && gcdActive)
                ctx.RecordDiagnostic?.Invoke("gcd-watchdog-progress", new { targetId, actionId = gcd->ActionId,
                    evidence = "observed-gcd-cooldown-started-not-hit-confirmation", floor = state.Floor });
            if (recover)
            {
                // Same framework thread; revalidate the exact action/target immediately before dispatch.
                bool stillValid = Service.TargetManager.Target?.GameObjectId == targetId &&
                    ctx.CombatAssist.SelectedId == targetId && !MechanicExecutionState.ActionsBlocked &&
                    !manager->ActionQueued && !player!.IsCasting &&
                    ReadRotationState(_configuration.Rotation.Provider) == FsdRotationState.On &&
                    manager->GetActionStatus(ActionType.Action, adjusted, targetId) == Service.ActionStatus_Ready;
                bool accepted = stillValid && FsdActionExecutor.Cast(adjusted, targetId);
                ctx.RecordDiagnostic?.Invoke("gcd-watchdog-attempt", new { targetId, job, requestedActionId = action,
                    adjustedActionId = adjusted, idleMilliseconds = _gcdWatchdog.IdleMilliseconds, accepted,
                    actionStatus, rangeStatus, floor = state.Floor, inCombat = Service.Condition[ConditionFlag.InCombat],
                    result = accepted ? "native-request-accepted-awaiting-gcd" : "not-dispatched-or-rejected-no-repeat" });
                Service.Log.Info($"[LocalExtensions.GcdWatchdog] floor={state.Floor} target={targetId} action={adjusted} accepted={accepted}; awaiting actual GCD progress.");
            }
            if (now >= _nextGcdDiagnostic || reason != _gcdDiagnosticReason || targetId != _gcdDiagnosticTarget)
            {
                _nextGcdDiagnostic = now + 5000; _gcdDiagnosticReason = reason; _gcdDiagnosticTarget = targetId;
                ctx.RecordDiagnostic?.Invoke("gcd-watchdog-state", new { floor = state.Floor, targetId,
                    selectedTargetId = ctx.CombatAssist.SelectedId, reason, gcdKnown, gcdActive,
                    idleMilliseconds = _gcdWatchdog.IdleMilliseconds, attempted = _gcdWatchdog.Attempted });
            }
        }
        catch (Exception ex)
        {
            _gcdWatchdog.Reset(); _gcdDisabledByError = true;
            ctx.RecordDiagnostic?.Invoke("gcd-watchdog-error", new { error = ex.ToString(), disabledForRun = true });
            Service.Log.Error(ex, "[LocalExtensions.GcdWatchdog] Disabled for this run after observation failure");
        }
    }
}
