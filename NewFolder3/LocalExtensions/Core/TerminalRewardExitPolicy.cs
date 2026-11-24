namespace DeepDungeon.Fsd.Core;

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
    public static bool ShouldContinue(FarmingPlan? plan, in TerminalRewardExitFacts facts)
    {
        if (!facts.Enabled || !facts.StayRequested || !facts.RoomObserved || facts.RewardsPending ||
            !facts.DutyCompleted || facts.DutyFailed || facts.AttemptAborted || facts.HasError ||
            facts.StateReadFailed || !facts.InDuty || facts.Transitioning || !facts.PlayerReady)
            return false;
        if (plan is not { Mode: FarmingMode.DeepProgression } ||
            plan.StopAfterFloor.HasValue || plan.HoldAfterClear || plan.HoldOnFailure ||
            plan.DungeonId != facts.DungeonId || facts.Floor != plan.MaxFloor ||
            plan.FreeExploration && plan.EndFloor != plan.MaxFloor)
            return false;
        return Potd200RewardPolicy.IsTerminal(facts.DungeonId, facts.Floor) ||
            ResultAltarPolicy.TryGet(facts.DungeonId, facts.Floor, out _);
    }
}
