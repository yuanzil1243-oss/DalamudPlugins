using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct HohCentaurObstacle(ulong Id, Vector3 Position, float Radius);

/// <summary>Pulling exclusions, not transit exclusions. These are conservative policy margins, not aggro radii.</summary>
public static class HohCentaurPullPolicy
{
    public const float PlayerClearance = 12;
    public const float TargetClearance = 15;

    public static bool ClearPosition(Vector3 point, float radius, IEnumerable<HohCentaurObstacle> centaurs,
        float clearance, out ulong blocker)
    {
        blocker = 0;
        if (!CombatPositionPolicy.IsFinite(point) || !float.IsFinite(radius) || radius < 0 ||
            !float.IsFinite(clearance) || clearance < 0) return false;
        foreach (var enemy in centaurs)
        {
            if (!CombatPositionPolicy.IsFinite(enemy.Position) || !float.IsFinite(enemy.Radius) || enemy.Radius < 0)
            { blocker = enemy.Id; return false; }
            float minimum = clearance + radius + enemy.Radius;
            if (Vector2.DistanceSquared(new(point.X, point.Z), new(enemy.Position.X, enemy.Position.Z)) < minimum * minimum)
            { blocker = enemy.Id; return false; }
        }
        return true;
    }

    public static bool AllowsPull(Vector3 player, float playerRadius, Vector3 target, float targetRadius,
        IReadOnlyList<HohCentaurObstacle> centaurs, out ulong blocker, out string reason)
    {
        if (!ClearPosition(player, playerRadius, centaurs, PlayerClearance, out blocker))
        { reason = "pull-position-near-centaur"; return false; }
        if (!ClearPosition(target, targetRadius, centaurs, TargetClearance, out blocker))
        { reason = "pull-target-near-centaur"; return false; }
        reason = "centaur-pull-clear"; return true;
    }
}
