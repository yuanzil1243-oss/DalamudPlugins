using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using global::Dalamud.Game.ClientState.Conditions;
using System.Runtime.CompilerServices;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

/// <summary>Opt-in handoff after the original reward controller confirms every reward.</summary>
internal static class TerminalRewardAutoExit
{
    private sealed class Observation
    {
        internal TerminalRewardExitDecision? LastDecision;
    }
    private static readonly ConditionalWeakTable<RunContext, Observation> Observations = new();

    internal static TerminalRewardExitAction Update(RunContext context, bool pauseRequested = false)
    {
        bool enabled = context.Configuration.Farming.AutoExitAfterTerminalReward;
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
        var decision = TerminalRewardExitPolicy.Decide(context.FarmingPlan, facts);
        var observation = Observations.GetValue(context, _ => new Observation());
        if (observation.LastDecision != decision)
        {
            observation.LastDecision = decision;
            context.RecordDiagnostic?.Invoke("local-terminal-reward-exit-decision", new
            {
                action = decision.Action.ToString(), decision.Reason, enabled, pauseRequested,
                facts, plan = context.FarmingPlan is { } plan ? new
                {
                    plan.DungeonId, plan.Mode, plan.ExternalSave, plan.ManagedContinuation, plan.KeepSave, plan.CurrentDutyOnly,
                    plan.StopAfterFloor, plan.HoldAfterClear, plan.HoldOnFailure,
                    plan.FreeExploration, plan.EndFloor
                } : null
            });
            Service.Log.Info($"[LocalExtensions.TerminalReward] {decision.Action}: {decision.Reason}");
        }
        if (decision.Action == TerminalRewardExitAction.Wait)
        {
            context.StatusLine = facts.RewardsPending || !facts.DutyCompleted
                ? "等待終局獎勵確認；完成後自動退本。"
                : "終局獎勵已確認；等待退本狀態穩定。";
            return decision.Action;
        }
        Observations.Remove(context);
        if (decision.Action != TerminalRewardExitAction.Continue) return decision.Action;

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
        return decision.Action;
    }
}
