namespace DeepDungeon.Fsd.Core;

public enum TerminalRewardExitAction { RetainAndStop, Wait, Continue }
public readonly record struct TerminalRewardExitDecision(TerminalRewardExitAction Action, string Reason);

public readonly record struct TerminalRewardExitFacts
{
    public bool Enabled { get; init; }
    public bool StayRequested { get; init; }
    public bool RoomObserved { get; init; }
    public bool RewardsPending { get; init; }
    public bool DutyCompleted { get; init; }
    public bool DutyFailed { get; init; }
    public bool AttemptAborted { get; init; }
    public bool HasError { get; init; }
    public bool StateReadFailed { get; init; }
    public bool InDuty { get; init; }
    public bool Transitioning { get; init; }
    public bool PlayerReady { get; init; }
    public uint DungeonId { get; init; }
    public int Floor { get; init; }
}

/// <summary>Release only the upstream post-reward stay; never infer a reward from a boss clear.</summary>
public static class TerminalRewardExitPolicy
{
    public static bool ShouldContinue(FarmingPlan? plan, in TerminalRewardExitFacts facts) =>
        Decide(plan, facts).Action == TerminalRewardExitAction.Continue;

    public static TerminalRewardExitDecision Decide(FarmingPlan? plan, in TerminalRewardExitFacts facts)
    {
        if (!facts.Enabled) return new(TerminalRewardExitAction.RetainAndStop, "disabled");
        if (plan is not { Mode: FarmingMode.DeepProgression })
            return new(TerminalRewardExitAction.RetainAndStop, "unsupported-plan");
        if (plan.StopAfterFloor.HasValue || plan.HoldAfterClear || plan.HoldOnFailure ||
            plan.FreeExploration && plan.EndFloor != plan.MaxFloor)
            return new(TerminalRewardExitAction.RetainAndStop, "protected-plan");
        if (facts.DutyFailed || facts.AttemptAborted || facts.HasError)
            return new(TerminalRewardExitAction.RetainAndStop, "run-not-successful");
        if (!TryGetExitBaseId(plan.DungeonId, plan.MaxFloor, out _))
            return new(TerminalRewardExitAction.RetainAndStop, "unsupported-terminal");
        if (!facts.StayRequested || !facts.RoomObserved || facts.RewardsPending || !facts.DutyCompleted)
            return new(TerminalRewardExitAction.Wait, "awaiting-reward-confirmation");
        if (facts.StateReadFailed || facts.Transitioning || !facts.PlayerReady)
            return new(TerminalRewardExitAction.Wait, "awaiting-stable-exit-state");
        // The reward flags belong to this attempt. A manual exit while waiting must
        // still reach the existing outside-duty/save-ownership reconciliation.
        if (!facts.InDuty)
            return new(TerminalRewardExitAction.Continue, "reward-confirmed-already-outside");
        if (plan.DungeonId != facts.DungeonId || facts.Floor != plan.MaxFloor)
            return new(TerminalRewardExitAction.RetainAndStop, "terminal-identity-mismatch");
        return new(TerminalRewardExitAction.Continue, "reward-confirmed");
    }

    public static bool TryGetExitBaseId(uint dungeonId, int floor, out uint baseId)
    {
        baseId = Potd200RewardPolicy.IsTerminal(dungeonId, floor) ? Potd200RewardPolicy.ExitBaseId
            : ResultAltarPolicy.TryGet(dungeonId, floor, out _) ? ResultAltarPolicy.ExitBaseId : 0;
        return baseId != 0;
    }
}
