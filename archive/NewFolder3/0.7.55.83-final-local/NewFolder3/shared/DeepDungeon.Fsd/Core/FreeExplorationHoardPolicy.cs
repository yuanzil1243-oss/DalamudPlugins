namespace DeepDungeon.Fsd.Core;

/// <summary>Optional hoard work never changes progression or success accounting.</summary>
public static class FreeExplorationHoardPolicy
{
    public static bool Enabled(FarmingMode? mode, bool hoard) => mode == FarmingMode.FreeExploration && hoard;
    public static bool AllowsNewIntuition(FarmingMode? mode, int floor) => mode != FarmingMode.FreeExploration ||
        floor is >= 91 and <= 98 || floor is >= 1 and < 91 && floor % 10 is >= 1 and <= 7;

    // Reserve ten minutes for 99/rewards plus two minutes per remaining normal floor.
    // This is an explicit conservative policy, not a claim about game mechanics.
    public static int HighFloorReserveSeconds(int floor) => 600 + Math.Max(0, 98 - floor) * 120;
    public static bool AllowsHighFloorDetour(FarmingMode? mode, bool hoard, int floor, int? secondsLeft) =>
        Enabled(mode, hoard) && floor is >= 91 and <= 98 &&
        secondsLeft is { } seconds && seconds > HighFloorReserveSeconds(floor);

    public static IReadOnlyList<RoomPlanEntry> FilterRoute(IReadOnlyList<RoomPlanEntry> entries,
        PassageRouteScope scope, bool allowHoardDetour) => !allowHoardDetour ? scope.Filter(entries) :
        entries.Select(entry => scope.Allows(entry.RoomIndex) ? entry : entry with { ShouldSearchChests = false })
            .Where(entry => entry.ShouldProbeHoard || entry.ShouldVisitForIntel || entry.ShouldSearchChests).ToArray();
}
