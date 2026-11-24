using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>HoH gathering limits and shared room/path protections.</summary>
public static class HohHighFloorPolicy
{
    public static bool Enabled(FarmingMode? mode, uint dungeon, int floor) =>
        mode == FarmingMode.FreeExploration && dungeon == 2 && floor is >= 91 and <= 99;

    // All supported ordinary floors actively gather; centaur exclusions are enforced separately.
    public static bool EarlierGroup(FarmingMode? mode, uint dungeon, int floor) =>
        mode == FarmingMode.FreeExploration && dungeon == 2 && floor is >= 81 and <= 89;

    public static bool ActiveGroup(FarmingMode? mode, uint dungeon, int floor) =>
        ControlsCombat(mode, dungeon, floor);

    public static bool ControlsCombat(FarmingMode? mode, uint dungeon, int floor) =>
        EarlierGroup(mode, dungeon, floor) || Enabled(mode, dungeon, floor);

    public static int SupplementLimit(FarmingMode? mode, uint dungeon, int floor, int configured) =>
        ActiveGroup(mode, dungeon, floor) ? 6 : Math.Clamp(configured, 1, 6);

    public static int ConsolidationThreshold(FarmingMode? mode, uint dungeon, int floor, bool passageOpen) =>
        ActiveGroup(mode, dungeon, floor) && !passageOpen ? 6 : 1;

    public static bool CanSupplement(FarmingMode? mode, uint dungeon, int floor, int engaged, int configured,
        bool sameRoom, float distance, float range, bool dangerousEngaged, bool priorityFight, bool dangerousCandidate) =>
        ActiveGroup(mode, dungeon, floor)
            ? engaged > 0 && engaged < SupplementLimit(mode, dungeon, floor, configured) && sameRoom &&
                float.IsFinite(distance) && float.IsFinite(range) && distance >= 0 && distance <= range &&
                !dangerousEngaged && !priorityFight && !dangerousCandidate
            : PassageCombatPolicy.CanSupplement(engaged, configured, sameRoom, distance, range,
                dangerousEngaged, priorityFight, dangerousCandidate);

    public static bool AllowsSupplementExecutor(FarmingMode? mode, uint dungeon, int floor, bool legacyAttract) =>
        ControlsCombat(mode, dungeon, floor) || legacyAttract;

    public static bool WaitsForPassage(FarmingMode? mode, uint dungeon, int floor, bool passageOpen) =>
        Enabled(mode, dungeon, floor) && !passageOpen;

    public static ObjectiveArbiterSnapshot ApplyFinalFloorPassagePolicy(FarmingMode? mode, uint dungeon,
        int floor, in ObjectiveArbiterSnapshot snapshot) =>
        !Enabled(mode, dungeon, floor) || floor != 99 || !snapshot.PassageOpen ? snapshot : snapshot with {
            VisibleBandedChest = false, KnownOrConfirmedHoard = false, RequiredHoardDiscovery = false,
            MandatoryHoardTerminal = true, RequiredChestWork = false, RecoverActiveChest = false,
            PassageFirst = true };

    public static bool PathStaysInRoom(Vector3 start, IReadOnlyList<Vector3> path, int combatRoom,
        Func<Vector3, int> roomAt)
    {
        if (combatRoom < 0 || path.Count == 0 || !CombatPositionPolicy.IsFinite(start) || roomAt(start) != combatRoom) return false;
        var from = start;
        foreach (var end in path)
        {
            if (!CombatPositionPolicy.IsFinite(end)) return false;
            int steps = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(from, end) / .5f));
            if (steps > 100) return false;
            for (int i = 1; i <= steps; i++)
                if (roomAt(Vector3.Lerp(from, end, i / (float)steps)) != combatRoom) return false;
            from = end;
        }
        return true;
    }
}
