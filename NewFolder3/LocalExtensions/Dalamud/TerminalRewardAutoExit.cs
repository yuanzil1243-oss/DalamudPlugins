using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

/// <summary>Opt-in handoff after the original reward controller confirms every reward.</summary>
internal static class TerminalRewardAutoExit
{
    internal static bool TryContinue(RunContext context, bool pauseRequested = false)
    {
        bool enabled = context.Configuration.Farming.AutoExitAfterTerminalReward || pauseRequested;
        if (!enabled) return false;
        var facts = new TerminalRewardExitFacts
        {
            Enabled = enabled,
            StayRequested = context.TerminalStayRequested,
            RoomObserved = context.TerminalRoomObserved,
            RewardsPending = context.TerminalRewardsRequired,
            DutyCompleted = context.DutyCompletionObserved,
            DutyFailed = context.DutyFailureObserved,
            AttemptAborted = context.AttemptAborted,
            HasError = context.StatusIsError,
            StateReadFailed = context.Duty.StateReadFailed,
            InDuty = context.Duty.IsInDuty,
            Transitioning = context.Duty.IsTransitioning || DutyState.IsClientTransitioning,
            PlayerReady = Service.LocalPlayer is { IsDead: false } &&
                !Service.Condition[ConditionFlag.InCombat],
            DungeonId = context.Duty.DungeonId,
            Floor = context.Duty.Floor
        };
        if (!TerminalRewardExitPolicy.ShouldContinue(context.FarmingPlan, facts)) return false;

        // LeaveMode, reward evidence, save ownership and cycle accounting remain upstream-owned.
        // The next host tick enters the normal exit flow; there is no extra completion count here.
        context.TerminalStayRequested = false;
        context.StatusLine = "終局獎勵已確認領取；繼續正常退本，按原任務規則清理存檔及循環。";
        context.RecordDiagnostic?.Invoke("local-terminal-reward-auto-exit", new
        {
            facts.DungeonId, facts.Floor, externalSave = context.FarmingPlan!.ExternalSave,
            currentDutyOnly = context.FarmingPlan.CurrentDutyOnly,
            rewardEvidence = "upstream-terminal-stay-with-rewards-confirmed"
        });
        Service.Log.Info("[LocalExtensions.TerminalReward] " + context.StatusLine);
        return true;
    }
}
