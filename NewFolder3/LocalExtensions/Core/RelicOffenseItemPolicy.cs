namespace DeepDungeon.Fsd.Core;

public enum RelicOffenseItem { None, Strength, Frailty, Haste, Juniper }

public readonly record struct RelicOffenseItemSnapshot(
    uint Dungeon, int Floor, bool NormalFloor, bool CanAttempt, bool NeedsCombat,
    bool StrengthUsable, bool StrengthActive, bool FrailtyUsable, bool FrailtyActiveOrUsed,
    bool HasteUsable, bool HasteActive, bool JuniperUsable, bool JuniperActiveOrUsed,
    int FrailtyCount = 0, bool FrailtyReady = false, int JuniperCount = 0);

/// <summary>Second Frailty is spent on the next ordinary floor; one stays for the boss.</summary>
public sealed class HohRelicFrailtyReserve
{
    private int _lastFloor;
    private int _lastCount;
    public int NextSpendFloor { get; private set; }

    public bool Observe(int floor, int count)
    {
        bool carriedIntoNewFloor = floor == _lastFloor + 1 && _lastCount < 2;
        if (floor < _lastFloor || floor is < 21 or > 30)
            NextSpendFloor = 0;
        _lastFloor = floor;
        _lastCount = count;
        if (floor is < 21 or > 30) return false;
        if (count <= 1) NextSpendFloor = 0;
        else if (NextSpendFloor == 0)
            NextSpendFloor = floor % 10 == 9 || carriedIntoNewFloor ? floor : floor + 1;
        return floor is >= 21 and <= 29 && count >= 2 && floor >= NextSpendFloor;
    }
}

/// <summary>Relic-only output spending, preserving one Frailty/Juniper for the boss.</summary>
public static class RelicOffenseItemPolicy
{
    public const byte JuniperStoneId = 3;
    public const uint JuniperStatusId = 4584;

    public static bool AllowsNormalJuniper(FarmingPlan? plan, int count) => count > 0 &&
        (plan is not { RelicMaterial: true, DungeonId: 4 } || count >= 2);

    public static RelicOffenseItem Decide(FarmingPlan? plan, in RelicOffenseItemSnapshot s)
    {
        if (plan == null || !RelicFarmingPolicy.IsValid(plan) || plan.DungeonId != s.Dungeon ||
            s.Floor < plan.StartFloor || s.Floor > plan.StartFloor + 9 ||
            !s.CanAttempt)
            return RelicOffenseItem.None;
        if (!s.NormalFloor)
            return s.Dungeon == 2 && s.Floor % 10 == 0 && s.FrailtyCount > 0 &&
                s.FrailtyUsable && !s.FrailtyActiveOrUsed ? RelicOffenseItem.Frailty : RelicOffenseItem.None;
        if (s.StrengthUsable && !s.StrengthActive) return RelicOffenseItem.Strength;
        if (s.Dungeon == 4 && s.HasteUsable && !s.HasteActive) return RelicOffenseItem.Haste;
        if (s.Dungeon == 2 && s.FrailtyCount >= 2 && s.FrailtyReady &&
            s.FrailtyUsable && !s.FrailtyActiveOrUsed) return RelicOffenseItem.Frailty;
        if (s.Dungeon == 4 && s.NeedsCombat && s.JuniperCount >= 2 &&
            s.JuniperUsable && !s.JuniperActiveOrUsed) return RelicOffenseItem.Juniper;
        return RelicOffenseItem.None;
    }
}
