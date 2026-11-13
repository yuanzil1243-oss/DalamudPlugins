using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>Confirmed Wisdom is floor-local, independent of the delayed native passage flag.</summary>
public sealed class WisdomCombatState
{
    private uint _dungeon;
    private int _floor;
    public bool Confirmed { get; private set; }
    public bool Retreating { get; private set; }
    private DateTime _confirmedAt;
    public void Sync(uint dungeon, int floor)
    {
        if (_dungeon == dungeon && _floor == floor) return;
        Reset(); _dungeon = dungeon; _floor = floor;
    }
    public void Confirm(DateTime now)
    {
        if (Confirmed) return;
        Confirmed = Retreating = true; _confirmedAt = now;
    }
    public bool RetreatExpired(DateTime now) => now - _confirmedAt >= TimeSpan.FromSeconds(8);
    public void FinishRetreat() => Retreating = false;
    public bool SuppressOutput(int engaged) => Confirmed && (Retreating || engaged == 0);
    public bool AllowsExploration(int engaged) => !Confirmed || !Retreating && engaged == 0;
    public bool AllowsTarget(bool engaged) => !Confirmed || !Retreating && engaged;
    public void Reset() { Confirmed = Retreating = false; _dungeon = 0; _floor = 0; _confirmedAt = default; }
}

public static class PassageCombatPolicy
{
    public static bool AllowsNewTarget(bool wisdom, bool highFloor, bool passageOpen, int engaged, bool alreadyEngaged,
        bool localSupplement = false) =>
        alreadyEngaged || !wisdom && (!highFloor || !passageOpen && (engaged == 0 || localSupplement));

    public static bool CanSupplement(int engaged, int limit, bool sameRoom, float distance, float range,
        bool dangerousEngaged, bool priorityFight, bool dangerousCandidate) =>
        engaged > 0 && engaged < Math.Clamp(limit, 1, 6) && sameRoom &&
        float.IsFinite(distance) && distance >= 0 && distance <= Math.Min(12, range) &&
        !dangerousEngaged && !priorityFight && !dangerousCandidate;

    // Validate the complete navmesh path, not merely the destination or a straight-line shortcut.
    // An enemy already close to the starting point must not make retreat impossible, but we cannot approach it.
    public static bool SafeLocalPath(IReadOnlyList<Vector3> path, Vector3 start,
        IEnumerable<Vector3> idleEnemies, float maxLength = 22)
    {
        if (path.Count == 0 || !Finite(start)) return false;
        var enemies = idleEnemies.ToArray();
        float length = 0;
        var previous = start;
        foreach (var point in path)
        {
            if (!Finite(point) || Math.Abs(point.Y - start.Y) > 3) return false;
            length += Vector2.Distance(Xz(previous), Xz(point));
            if (length > maxLength) return false;
            foreach (var enemy in enemies)
            {
                float clearance = Math.Min(12, Vector2.Distance(Xz(start), Xz(enemy)) - .25f);
                if (clearance <= 0) return false;
                var segment = Xz(point) - Xz(previous);
                float t = segment.LengthSquared() < .001f ? 0 : Math.Clamp(Vector2.Dot(Xz(enemy) - Xz(previous), segment) / segment.LengthSquared(), 0, 1);
                if (Vector2.Distance(Xz(previous) + t * segment, Xz(enemy)) < clearance) return false;
            }
            previous = point;
        }
        return true;
    }
    private static Vector2 Xz(Vector3 v) => new(v.X, v.Z);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

public readonly record struct BossAddCandidate(ulong Id, uint MaxHp, uint Hp, float CenterDistance, bool CanHit);

public static class BossAddTargetPolicy
{
    public static ulong Select(IEnumerable<BossAddCandidate> candidates, ulong current)
    {
        var live = candidates.Where(c => c.Hp > 1 && c.MaxHp > 0).ToArray();
        if (live.Length == 0) return 0;
        uint lowestMax = live.Min(c => c.MaxHp);
        var adds = live.Where(c => c.MaxHp == lowestMax).ToArray();
        var sticky = adds.FirstOrDefault(c => c.Id == current && c.CanHit);
        if (sticky.Id != 0) return sticky.Id;
        return adds.OrderByDescending(c => c.CanHit).ThenBy(c => c.Hp)
            .ThenBy(c => c.CenterDistance).ThenBy(c => c.Id).First().Id;
    }
}
