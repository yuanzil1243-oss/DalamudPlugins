using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public enum Pt99ItemStep { None, Strength, Steel, Haste, WaitForFateTargets, Fate, Serenity }

public static class EndgameItemPolicy
{
    public static bool IsProgression(FarmingMode? mode) =>
        mode is FarmingMode.DeepProgression or FarmingMode.FreeExploration;
    public static bool ReserveFate(uint dungeon, int floor, FarmingMode? mode) =>
        dungeon == 4 && floor == 98 && IsProgression(mode);
    public static bool UseHighFloorBuff(uint dungeon, int floor, FarmingMode? mode,
        int stock, bool active, bool clearingNeeded) =>
        dungeon == 4 && floor is >= 91 and <= 98 && IsProgression(mode) &&
        stock > 1 && !active && clearingNeeded;

    // Availability includes bans, request deferrals and inventory. A missing item never blocks later steps.
    public static Pt99ItemStep NextBossStep(bool strengthNeeded, bool steelNeeded, bool hasteNeeded,
        bool fateAvailable, bool fateConfirmed, bool bothBossesReady, bool serenityNeeded) =>
        strengthNeeded ? Pt99ItemStep.Strength : steelNeeded ? Pt99ItemStep.Steel :
        hasteNeeded ? Pt99ItemStep.Haste : fateAvailable && !fateConfirmed
            ? bothBossesReady ? Pt99ItemStep.Fate : Pt99ItemStep.WaitForFateTargets
            : serenityNeeded ? Pt99ItemStep.Serenity : Pt99ItemStep.None;
}
