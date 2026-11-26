namespace DeepDungeon.Fsd.Core;

public sealed partial record FarmingPlan
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FreeBlindRangeEnabled { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int FreeBlindStartFloor { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public int FreeBlindEndFloor { get; init; }
}

/// <summary>Keep exact hoard work enabled outside the optional blind-search range.</summary>
public static class FreeBlindSearchPolicy
{
    public static bool Restricted(FarmingPlan? plan) => plan is { FreeExploration: true, FreeBlindRangeEnabled: true };
    public static bool Inside(FarmingPlan plan, int floor) =>
        plan.FreeBlindStartFloor >= 1 && plan.FreeBlindEndFloor >= plan.FreeBlindStartFloor &&
        plan.FreeBlindEndFloor <= plan.MaxFloor &&
        floor >= plan.FreeBlindStartFloor && floor <= plan.FreeBlindEndFloor;
    public static bool AllowsBlind(FarmingPlan? plan, int floor) => !Restricted(plan) || Inside(plan!, floor);
    public static bool AllowsHoardTool(FarmingPlan? plan, int floor, uint slot, bool overflow) =>
        !Restricted(plan) || Inside(plan!, floor) || overflow && slot == FloorInitPlanner.IntuitionPomanderSlotIndex;
}
