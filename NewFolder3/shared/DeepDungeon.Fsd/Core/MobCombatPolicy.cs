using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public static class ProgressionModePolicy
{
    public static bool IsProgression(FarmingMode? mode) => mode is FarmingMode.DeepProgression or FarmingMode.FreeExploration;
}

public readonly record struct CombatEnemy(ulong Id, uint NameId, uint Hp, float Distance, bool Engaged);

/// <summary>Only enemies already on the player's hate list participate in focus decisions.</summary>
public static class MobCombatPolicy
{
    // Track priority fights on every ordinary HoH floor; gathering/consolidation
    // remains separately gated by UsesControlledHighFloor.
    public static bool TrackingEnabled(FarmingMode? mode, uint dungeon, int floor) =>
        ProgressionModePolicy.IsProgression(mode) &&
        (dungeon == 4 || Hoh70FloorPolicy.Enabled(mode, dungeon, floor));

    public static int Priority(uint dungeon, int floor, uint nameId) => dungeon != 4 ? 0 :
        nameId is 14264 or 14265 or 14266 ? 2 :
        floor is >= 71 and <= 79 && nameId == 14194 ||
        floor is >= 61 and <= 69 && nameId == 14179 ||
        floor is >= 93 and <= 98 && nameId == 14222 ? 1 : 0;

    public static CombatEnemy? Select(uint dungeon, int floor, IEnumerable<CombatEnemy> enemies, ulong stickyId)
    {
        var live = enemies.Where(e => e.Engaged && e.Hp > 0).ToArray();
        if (live.Length == 0) return null;
        int priority = live.Max(e => Priority(dungeon, floor, e.NameId));
        var sticky = live.Where(e => e.Id == stickyId && Priority(dungeon, floor, e.NameId) == priority).ToArray();
        if (sticky.Length != 0) return sticky[0];
        return live.Where(e => Priority(dungeon, floor, e.NameId) == priority)
            .OrderBy(e => e.Hp).ThenBy(e => e.Distance).ThenBy(e => e.Id).First();
    }

    public static bool HighFloorEnabled(FarmingMode? mode, uint dungeon, int floor) =>
        ProgressionModePolicy.IsProgression(mode) && dungeon == 4 && floor is >= 91 and <= 98;

    public static bool UsesControlledHighFloor(FarmingMode? mode, uint dungeon, int floor) =>
        HighFloorEnabled(mode, dungeon, floor) || HohHighFloorPolicy.ControlsCombat(mode, dungeon, floor);

    // Grounded room centers must have a buffer to every unengaged visible enemy.
    public static bool ClearOfNewEnemies(Vector2 point, IEnumerable<Vector2> unengaged, float radius = 16) =>
        unengaged.All(p => Vector2.DistanceSquared(point, p) >= radius * radius);
}

/// <summary>Latch consolidation until combat actually ends, not merely until the count dips below the trigger.</summary>
public sealed class CombatConsolidationState
{
    public bool Active { get; private set; }
    public bool Update(bool enabled, int engaged, int threshold)
    {
        if (!enabled || engaged == 0) Active = false;
        else if (engaged >= Math.Clamp(threshold, 1, 6)) Active = true;
        return Active;
    }
}
