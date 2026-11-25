namespace DeepDungeon.Fsd.Core;

public readonly record struct HohRelicMagiciteSnapshot(uint DungeonId, int Floor,
    bool BossFloor, bool InCombat, bool LiveHostile, bool PassageOpen, int Stock);

/// <summary>Only the self-created HoH 21-30 relic-material loop spends its boss stock.</summary>
public static class HohRelicMagicitePolicy
{
    public static bool AppliesTo(FarmingPlan? plan) =>
        plan is { DungeonId: 2, RelicMaterial: true, StartFloor: 21 } && RelicFarmingPolicy.IsValid(plan);

    public static bool ShouldSpendAtBoss(FarmingPlan? plan, in HohRelicMagiciteSnapshot state) =>
        AppliesTo(plan) && state is { DungeonId: 2, Floor: 30, BossFloor: true,
            InCombat: true, LiveHostile: true, PassageOpen: false, Stock: > 0 };
}
