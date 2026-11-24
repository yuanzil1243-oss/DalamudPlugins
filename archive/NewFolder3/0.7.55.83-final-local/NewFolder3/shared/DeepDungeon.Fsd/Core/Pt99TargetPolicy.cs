namespace DeepDungeon.Fsd.Core;

public enum Pt99TargetKind { WaitForColor, Grief, Eater, Minion, WaitForBalance, Finished, WaitForBossState }

public enum Pt99Color { None, Light, Dark }

public readonly record struct Pt99TargetDecision(Pt99TargetKind Target, double HpDifference, Pt99Color RequestedColor = Pt99Color.None)
{
    public bool SuppressOutput => Target is Pt99TargetKind.WaitForColor or Pt99TargetKind.WaitForBalance or Pt99TargetKind.Finished or Pt99TargetKind.WaitForBossState;
}

/// <summary>Directional balance gate. Movement confirms the actual color before output resumes.</summary>
public static class Pt99TargetPolicy
{
    public const uint GriefBaseId = 0x48EA, EaterBaseId = 0x48EB, MinionBaseId = 0x48EC;
    public const uint LightStatusId = 4560, DarkStatusId = 4559;
    public const double PrepareColorLimit = 18;
    public const double BalanceLimit = 24;

    public static bool RequiresColorForBossOutput(bool light, bool dark, bool minionTarget = false) =>
        !minionTarget && light == dark;

    public static bool IsOutputTargetCompatible(bool light, bool dark, uint targetBaseId, bool minionAllowed = false) =>
        targetBaseId == MinionBaseId ? minionAllowed :
        !RequiresColorForBossOutput(light, dark) &&
        targetBaseId == (light ? GriefBaseId : EaterBaseId);

    public static bool CanUsePrepullFloorRoute(bool inCombat, uint griefHp, uint griefMaxHp,
        uint eaterHp, uint eaterMaxHp, bool anyEnemyCasting) =>
        !inCombat && !anyEnemyCasting && griefMaxHp > 1 && eaterMaxHp > 1 &&
        griefHp == griefMaxHp && eaterHp == eaterMaxHp;

    public static Pt99TargetDecision Decide(bool light, bool dark, uint griefHp, uint griefMaxHp,
        uint eaterHp, uint eaterMaxHp, bool minionAvailable, bool inCombat = false)
    {
        double difference = griefMaxHp == 0 || eaterMaxHp == 0 ? 0 :
            100d * eaterHp / eaterMaxHp - 100d * griefHp / griefMaxHp;
        if (minionAvailable) return new(Pt99TargetKind.Minion, difference);
        if (griefMaxHp == 0 || eaterMaxHp == 0) return new(Pt99TargetKind.WaitForBossState, difference);
        if (griefHp <= 1 && eaterHp <= 1) return new(Pt99TargetKind.Finished, difference);
        if (!light && !dark)
            return new(Pt99TargetKind.WaitForColor, difference,
                griefHp <= 1 || difference > 0 ? Pt99Color.Dark : Pt99Color.Light);
        if (light && dark)
            return new(Pt99TargetKind.WaitForColor, difference);
        bool canAttack = light ? griefHp > 1 : eaterHp > 1;
        // Compare ratios, not rounded display values or an absolute gap. Exactly
        // 18/24 percentage points belong to the prepare/stop side of the boundary.
        double worseningGap = light ? difference : -difference;
        bool fallingBehind = worseningGap >= BalanceLimit - 1e-9;
        return !canAttack || fallingBehind
            ? new(Pt99TargetKind.WaitForBalance, difference, light ? Pt99Color.Dark : Pt99Color.Light)
            : new(light ? Pt99TargetKind.Grief : Pt99TargetKind.Eater, difference,
                worseningGap >= PrepareColorLimit - 1e-9 ? light ? Pt99Color.Dark : Pt99Color.Light : Pt99Color.None);
    }
}
