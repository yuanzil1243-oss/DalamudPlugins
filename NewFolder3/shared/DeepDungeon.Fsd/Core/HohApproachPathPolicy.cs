using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct HohApproachObstacle(ulong Id, Vector3 Position, bool MayEngage);

/// <summary>Only a directed HoH pull may pass ordinary eligible enemies; local escapes remain strict.</summary>
public static class HohApproachPathPolicy
{
    public static bool AllowsTargetRoom(bool scoped, bool engaged, int playerRoom, int targetRoom) =>
        !scoped || engaged || playerRoom >= 0 && targetRoom == playerRoom;

    public static int IncidentalCapacity(bool controlledFloor, int engaged, bool selectedEngaged, int limit = 6) =>
        controlledFloor ? Math.Max(0, Math.Clamp(limit, 1, 6) - Math.Max(0, engaged) - (selectedEngaged ? 0 : 1)) : int.MaxValue;

    // Validate/move only as far as the first usable firing point on the returned navmesh route.
    // Endpoint, ground continuity and mechanic validation still run in the production caller.
    public static List<Vector3> UntilFirstOutput(Vector3 start, IReadOnlyList<Vector3> path, Func<Vector3, bool> canOutput)
    {
        var prefix = new List<Vector3>();
        var previous = start;
        foreach (var end in path)
        {
            if (!CombatPositionPolicy.IsFinite(previous) || !CombatPositionPolicy.IsFinite(end)) return path.ToList();
            float length = Vector3.Distance(previous, end);
            if (!float.IsFinite(length) || length > HohTargetApproachPolicy.MaximumPathLength) return path.ToList();
            int steps = Math.Max(1, (int)Math.Ceiling(length));
            for (int i = 1; i <= steps; i++)
            {
                var point = Vector3.Lerp(previous, end, i / (float)steps);
                if (!CombatPositionPolicy.HasRealHorizontalMove(start, point) || !canOutput(point)) continue;
                prefix.Add(point);
                return prefix;
            }
            prefix.Add(end);
            previous = end;
        }
        return prefix;
    }

    public static bool TryValidate(IReadOnlyList<Vector3> path, Vector3 start,
        IReadOnlyList<HohApproachObstacle> obstacles, int capacity, out ulong blockerId,
        out Vector3 blocker, out float initialDistance, out float minimumDistance,
        out int incidentalCount, out string reason, float maximumLength = HohTargetApproachPolicy.MaximumPathLength)
    {
        blockerId = 0; incidentalCount = 0;
        if (!PassageCombatPolicy.TrySafeHohRecoveryPath(path, start, [], out blocker,
                out initialDistance, out minimumDistance, out reason, maximumLength)) return false;
        var encountered = new HashSet<ulong>();
        foreach (var enemy in obstacles)
        {
            if (PassageCombatPolicy.TrySafeHohRecoveryPath(path, start, [enemy.Position], out var point,
                    out float initial, out float minimum, out var rejected, maximumLength)) continue;
            if (rejected != "unengaged-enemy-on-local-path" || !enemy.MayEngage || enemy.Id == 0)
            {
                blockerId = enemy.Id; blocker = enemy.Position; initialDistance = initial;
                minimumDistance = minimum; reason = rejected;
                return false;
            }
            if (!encountered.Add(enemy.Id)) continue;
            incidentalCount++;
            if (incidentalCount <= Math.Max(0, capacity)) continue;
            blockerId = enemy.Id; blocker = point; initialDistance = initial;
            minimumDistance = minimum; reason = "hoh-approach-pull-capacity";
            return false;
        }
        reason = "hoh-approach-pull-policy-accepted";
        return true;
    }
}
