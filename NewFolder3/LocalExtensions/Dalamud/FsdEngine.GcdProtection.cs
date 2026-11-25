using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Enums;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using NativeObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private readonly GcdStallWatchdog _gcdWatchdog = new();
    private uint _gcdJob;
    private int _gcdWaitSeconds;
    private long _nextGcdPoll, _nextGcdDiagnostic;
    private string? _gcdDiagnosticReason;
    private ulong _gcdDiagnosticTarget;
    private bool _gcdDisabledByError;

    internal unsafe void UpdateGcdStallProtection()
    {
        try
        {
            // Read live scene state before polling/native access, not the previous FSD snapshot.
            bool enabled = _configuration.Farming.GcdStallProtection;
            if (!_gcdWatchdog.UpdateScope(enabled, enabled && IsGcdProtectionInDeepDungeon()))
            {
                _gcdJob = 0; _gcdDisabledByError = false;
                _nextGcdPoll = 0; _gcdDiagnosticReason = null;
                return;
            }
            long now = Environment.TickCount64;
            if (now < _nextGcdPoll || _gcdDisabledByError) return;
            _nextGcdPoll = now + 100;

            var player = Service.LocalPlayer;
            var target = Service.TargetManager.Target as IBattleNpc;
            ulong targetId = target?.GameObjectId ?? 0;
            uint job = player?.ClassJob.RowId ?? 0;
            int seconds = GcdStallWatchdog.NormalizeWaitSeconds(_configuration.Farming.GcdStallWaitSeconds);
            if (_gcdJob != job || _gcdWaitSeconds != seconds)
            {
                _gcdWatchdog.Reset(); _gcdJob = job; _gcdWaitSeconds = seconds;
                _gcdDiagnosticReason = null;
            }

            var manager = ActionManager.Instance();
            var gcd = manager == null ? null : manager->GetRecastGroupDetail(57);
            bool gcdKnown = gcd != null && float.IsFinite(gcd->Elapsed) && float.IsFinite(gcd->Total);
            bool gcdActive = gcdKnown && gcd->IsActive && gcd->Total - gcd->Elapsed > 0.05f;
            uint action = GcdRecoveryActions.ForJob(job), adjusted = 0;
            uint? actionStatus = null, rangeStatus = null;
            int? actionRange = null, mainGroup = null, additionalGroup = null;
            bool track = false;
            string reason = "eligible";
            // Within deep dungeons, keep the assist independent of FSD/provider readiness.
            if (player is not { IsDead: false, Address: not 0 } ||
                Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51] ||
                Service.Condition[ConditionFlag.WatchingCutscene] || Service.Condition[ConditionFlag.WatchingCutscene78] ||
                Service.Condition[ConditionFlag.OccupiedInCutSceneEvent]) reason = "player-not-ready";
            else if (target is not { IsDead: false, IsTargetable: true, CurrentHp: > 0, Address: not 0 } ||
                targetId == 0 || (BattleNpcSubKind)target.SubKind != BattleNpcSubKind.Combatant)
                reason = "no-selected-combat-target";
            else if (manager == null || !gcdKnown) reason = "gcd-observation-unavailable";
            else if (action == 0) reason = "unsupported-job";
            else
            {
                adjusted = manager->GetAdjustedActionId(action);
                var row = adjusted == 0 ? null : Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>()?.GetRowOrDefault(adjusted);
                if (row is { } data)
                {
                    actionRange = data.Range; mainGroup = data.CooldownGroup; additionalGroup = data.AdditionalCooldownGroup;
                    track = GcdRecoveryActionPolicy.IsHostileGcd(data.CanTargetHostile, data.Range,
                        data.CooldownGroup, data.AdditionalCooldownGroup);
                }
                if (!track) reason = "not-a-verified-hostile-gcd";
                else if (player.IsCasting || Service.Condition[ConditionFlag.Casting]) reason = "casting";
                else if (gcdActive) reason = "gcd-cooldown";
                else if (manager->ActionQueued || manager->AnimationLock > 0.05f) reason = "queued-or-animation-lock";
                else
                {
                    actionStatus = manager->GetActionStatus(ActionType.Action, adjusted, targetId);
                    rangeStatus = ActionManager.GetActionInRangeOrLoS(adjusted,
                        (NativeObject*)player.Address, (NativeObject*)target.Address);
                    if (actionStatus != Service.ActionStatus_Ready) reason = "action-not-ready";
                    else if (!Service.Available_GetActinoInRangeOrLoSStatus.Contains(rangeStatus.Value))
                        reason = "out-of-range-or-sight";
                }
            }

            bool attemptedBefore = _gcdWatchdog.Attempted;
            long elapsed = gcdKnown ? (long)(Math.Clamp(gcd->Elapsed, 0, 300) * 1000) : 0;
            bool recover = _gcdWatchdog.Update(now, targetId, track, gcdActive && track,
                seconds, reason == "eligible", elapsed);
            if (attemptedBefore && gcdActive && track)
                RecordGcdDiagnostic("gcd-watchdog-progress", new { targetId, actionId = gcd->ActionId,
                    evidence = "observed-gcd-cooldown-started-not-hit-confirmation" });
            if (recover)
            {
                // This separately enabled user assist deliberately bypasses FSD's restrictions.
                // Revalidate on the framework thread immediately before the native request.
                var latest = Service.TargetManager.Target as IBattleNpc;
                bool stillValid = _configuration.Farming.GcdStallProtection && IsGcdProtectionInDeepDungeon() &&
                    latest is { IsDead: false, IsTargetable: true, CurrentHp: > 0 } &&
                    latest.GameObjectId == targetId && Service.LocalPlayer?.Address == player!.Address &&
                    !player.IsDead && !player.IsCasting && !manager->ActionQueued && manager->AnimationLock <= 0.05f &&
                    manager->GetAdjustedActionId(action) == adjusted &&
                    manager->GetActionStatus(ActionType.Action, adjusted, targetId) == Service.ActionStatus_Ready &&
                    Service.Available_GetActinoInRangeOrLoSStatus.Contains(ActionManager.GetActionInRangeOrLoS(
                        adjusted, (NativeObject*)player.Address, (NativeObject*)latest.Address));
                bool accepted = stillValid && manager->UseAction(ActionType.Action, adjusted, targetId);
                RecordGcdDiagnostic("gcd-watchdog-attempt", new { targetId, job, requestedActionId = action,
                    adjustedActionId = adjusted, waitSeconds = seconds, idleMilliseconds = _gcdWatchdog.IdleMilliseconds,
                    accepted, actionStatus, rangeStatus, actionRange, mainGroup, additionalGroup,
                    result = accepted ? "native-request-accepted-awaiting-gcd" : "not-dispatched-or-rejected-no-repeat" });
            }
            if (now >= _nextGcdDiagnostic || reason != _gcdDiagnosticReason || targetId != _gcdDiagnosticTarget)
            {
                _nextGcdDiagnostic = now + 5000; _gcdDiagnosticReason = reason; _gcdDiagnosticTarget = targetId;
                RecordGcdDiagnostic("gcd-watchdog-state", new { targetId, job, requestedActionId = action,
                    adjustedActionId = adjusted, reason, gcdKnown, gcdActive, waitSeconds = seconds,
                    idleMilliseconds = _gcdWatchdog.IdleMilliseconds, attempted = _gcdWatchdog.Attempted,
                    actionStatus, rangeStatus, actionRange, mainGroup, additionalGroup });
            }
        }
        catch (Exception ex)
        {
            _gcdWatchdog.Reset(); _gcdDisabledByError = true;
            RecordGcdDiagnostic("gcd-watchdog-error", new { error = ex.ToString(), disabledUntilToggle = true });
        }
    }

    private static bool IsGcdProtectionInDeepDungeon() =>
        Service.Condition[ConditionFlag.InDeepDungeon] && DeepDungeonHelper.IsInDeepDungeon() &&
        !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51];

    private void RecordGcdDiagnostic(string name, object payload)
    {
        // Standalone mode has no RunContext. Optional replay/logging are isolated individually.
        try { _ddHost?.Context?.RecordDiagnostic?.Invoke(name, payload); } catch { }
        try
        {
            string message = $"[LocalExtensions.GcdWatchdog] {name} {System.Text.Json.JsonSerializer.Serialize(payload)}";
            if (name == "gcd-watchdog-error") Service.Log.Error(message);
            else if (name == "gcd-watchdog-state") Service.Log.Debug(message);
            else Service.Log.Info(message);
        }
        catch { }
    }
}
