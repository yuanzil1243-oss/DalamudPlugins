using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct HohRetreatRoom(int Room, bool Visited, int Hops, int LiveEnemies, float DistanceSquared);

/// <summary>Retreat through one doorway into a previously observed empty room.</summary>
public static class HohCentaurRetreatPolicy
{
    public const float MaximumPathLength = 64;
    public static IEnumerable<int> OrderRooms(IEnumerable<HohRetreatRoom> rooms, int previousRoom) => rooms
        .Where(r => r.Room >= 0 && r.Visited && r.Hops == 1 && r.LiveEnemies == 0 && float.IsFinite(r.DistanceSquared))
        .OrderByDescending(r => r.Room == previousRoom).ThenBy(r => r.DistanceSquared).Select(r => r.Room);

    public static bool SafeRoute(IReadOnlyList<Vector3> path, Vector3 start, IEnumerable<Vector3> idle,
        int fromRoom, int toRoom, Func<Vector3, int> roomAt) =>
        TrySafeRoute(path, start, idle, fromRoom, toRoom, roomAt, out _);

    public static bool TrySafeRoute(IReadOnlyList<Vector3> path, Vector3 start, IEnumerable<Vector3> idle,
        int fromRoom, int toRoom, Func<Vector3, int> roomAt, out string reason)
    {
        reason = "invalid-retreat-rooms";
        if (fromRoom < 0 || toRoom < 0 || fromRoom == toRoom) return false;
        if (!PassageCombatPolicy.TrySafeHohRecoveryPath(path, start, idle, out _, out _, out _, out reason,
            MaximumPathLength)) return false;
        var previous = start;
        foreach (var point in path)
        {
            int samples = Math.Max(1, (int)Math.Ceiling(Vector3.Distance(previous, point)));
            for (int i = 0; i <= samples; i++)
            {
                int room = roomAt(Vector3.Lerp(previous, point, (float)i / samples));
                if (room >= 0 && room != fromRoom && room != toRoom)
                { reason = "retreat-crosses-third-room"; return false; }
            }
            previous = point;
        }
        reason = "safe-adjacent-retreat"; return true;
    }
}
