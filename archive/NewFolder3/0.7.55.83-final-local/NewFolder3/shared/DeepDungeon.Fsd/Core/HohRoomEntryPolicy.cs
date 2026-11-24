using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>Observe and pull at the entrance-side edge, without committing to a room-center destination.</summary>
public static class HohRoomEntryPolicy
{
    public static IEnumerable<Vector3> Seeds(Vector3 fromCenter, Vector3 nextCenter)
    {
        if (!CombatPositionPolicy.IsFinite(fromCenter) || !CombatPositionPolicy.IsFinite(nextCenter)) yield break;
        var delta = new Vector2(nextCenter.X - fromCenter.X, nextCenter.Z - fromCenter.Z);
        float length = delta.Length();
        if (length < 8) yield break;
        var direction = delta / length;
        var side = new Vector2(-direction.Y, direction.X);
        var boundary = new Vector2(fromCenter.X, fromCenter.Z) + delta * .5f;
        foreach (float inset in new[] { 2f, 4f, 6f })
        {
            if (inset > length * .25f) continue;
            foreach (float offset in new[] { 0f, 3f, -3f, 6f, -6f })
            {
                var point = boundary + direction * inset + side * offset;
                yield return new(point.X, nextCenter.Y, point.Y);
            }
        }
    }

    public static bool PreferLocalChests(bool highFloor, bool passageOpen, bool combatInProgress,
        bool petrificationClearing, bool localPullAvailable, bool localChestWork) =>
        highFloor && !passageOpen && !combatInProgress && !petrificationClearing && !localPullAvailable && localChestWork;

    public static RoomPlanEntry[] PrioritizeLocalChests(IReadOnlyList<RoomPlanEntry> route, int currentRoom)
    {
        if (currentRoom is < 0 or >= 25) return route.ToArray();
        var local = route.FirstOrDefault(e => e.RoomIndex == currentRoom);
        local = new(currentRoom, false, true, false, HoardEvidenceState.Disabled) { ChestBatch = local.ChestBatch };
        return new[] { local }.Concat(route.Where(e => e.RoomIndex != currentRoom)).ToArray();
    }
}
