using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public static class SupplyRoutePolicy
{
    public static int RemainingNormalFloorsInSet(int floor) => floor > 0 ? Math.Max(0, 9 - floor % 10) : 0;
    // Initial tunable budget: 90s per remaining normal floor, 4m for boss, 1m passage reserve.
    public static bool HasTime(double secondsLeft, int remainingMobFloors) =>
        double.IsFinite(secondsLeft) && secondsLeft > 300 + Math.Max(0, remainingMobFloors) * 90;

    public static bool OnWay(Vector2 player, Vector2 chest, Vector2 destination)
    {
        var forward = destination - player;
        if (!float.IsFinite(forward.LengthSquared()) || forward.LengthSquared() < 1) return false;
        float progress = Vector2.Dot(chest - player, Vector2.Normalize(forward));
        // No backward travel; at most a short detour within the current forward corridor.
        return progress >= 0 && progress <= forward.Length() + 1.5f &&
            Vector2.Distance(player, chest) + Vector2.Distance(chest, destination) <= forward.Length() + 4;
    }
}
