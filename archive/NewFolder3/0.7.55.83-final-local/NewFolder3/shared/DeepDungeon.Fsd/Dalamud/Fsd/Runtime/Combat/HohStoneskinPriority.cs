using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal readonly record struct HohStoneskinCandidate(
    ulong Id, uint Hp, float DistanceSquared, int Priority,
    bool Engaged, bool Targetable = true, bool Dead = false, bool Allowed = true);

/// <summary>Identify the caster species before its first cast; never prioritize shield recipients.</summary>
internal sealed class HohStoneskinPriority
{
    // CN client BNpcBase/BNpcName and actual 61-70 replay: Heavenly Jubokko / 天之树木子.
    internal const uint CasterBaseId = 8961, CasterNameId = 7349, StoneskinActionId = 12274;
    private bool _enabled;

    internal static bool Enabled(FarmingMode? mode, uint dungeon, int floor) =>
        mode == FarmingMode.FreeExploration && dungeon == 2 && floor is > 0 and < 100 && floor % 10 != 0;

    internal void BeginFloor(FarmingMode? mode, uint dungeon, int floor)
        => _enabled = Enabled(mode, dungeon, floor);

    internal static bool IsCaster(uint baseId, uint nameId) => baseId == CasterBaseId && nameId == CasterNameId;
    internal int Observe(uint baseId, uint nameId, bool casting, uint action) =>
        !_enabled || !IsCaster(baseId, nameId) ? 0 : casting && action == StoneskinActionId ? 2 : 1;

    internal static ulong Select(IEnumerable<HohStoneskinCandidate> candidates, float range)
    {
        HohStoneskinCandidate? best = null;
        foreach (var c in candidates)
        {
            if (c.Id == 0 || c.Hp == 0 || c.Priority <= 0 || !c.Engaged || !c.Targetable || c.Dead ||
                !c.Allowed || !float.IsFinite(c.DistanceSquared) || c.DistanceSquared < 0 ||
                c.DistanceSquared > range * range) continue;
            if (best is not { } b || c.Priority > b.Priority ||
                c.Priority == b.Priority && (c.Hp > b.Hp || c.Hp == b.Hp &&
                    (c.DistanceSquared < b.DistanceSquared || c.DistanceSquared == b.DistanceSquared && c.Id < b.Id)))
                best = c;
        }
        return best?.Id ?? 0;
    }
}
