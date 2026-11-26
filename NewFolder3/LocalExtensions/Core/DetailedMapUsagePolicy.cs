namespace DeepDungeon.Fsd.Core;

/// <summary>Map identity includes the dungeon, not just a matching floor number.</summary>
public static class DetailedMapUsagePolicy
{
    public static string? ScenarioKey(FarmingPlan plan) =>
        plan.Mode == FarmingMode.Hoard && plan.BandedEnabled
            ? DetailedMapScenarioCatalog.Definitions.FirstOrDefault(s =>
                s.DungeonId == plan.DungeonId && s.FloorSetStart == plan.StartFloor)?.Key
            : null;

    public static bool Applies(DetailedMapCatalog? catalog, uint dungeon, uint territory, byte floor) =>
        catalog != null && DetailedMapScenarioCatalog.TryGetByKey(catalog.ScenarioKey, out var scope) &&
        catalog.TerritoryId == territory &&
        scope.Covers(dungeon, territory, (floor - 1) / 10 * 10 + 1, floor) &&
        catalog.TryGetFloorIndex(floor, out _);
}
