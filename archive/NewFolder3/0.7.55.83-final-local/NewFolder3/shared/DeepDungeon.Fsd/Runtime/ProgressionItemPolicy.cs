using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public static class ProgressionItemPolicy
{
    public static bool AffectsNextFloor(uint slot) => slot is 4 or 5 or 6;
    public static bool CanBenefitNextFloor(uint dungeon, int floor, int? stopAfterFloor) =>
        DeepDungeonFloorClassifier.Classify(dungeon, floor + 1) == DeepDungeonFloorKind.Mob &&
        (!stopAfterFloor.HasValue || floor + 1 <= stopAfterFloor.Value);

    public static bool AllowsCombat(uint slot, bool boss, FloorItemUsePurpose purpose, uint dungeon = 4) =>
        dungeon == 2 && slot == FloorInitPlanner.PurityPomanderSlotIndex ||
        dungeon == 2 && (purpose is FloorItemUsePurpose.HohEmergency or FloorItemUsePurpose.HohPetrification or FloorItemUsePurpose.HohFrailty ||
            purpose == FloorItemUsePurpose.GoldChestOvercap && slot is HohItemPolicy.Frailty or HohItemPolicy.Petrification or FloorInitPlanner.FlightPomanderSlotIndex) ||
        slot == 12 && purpose == FloorItemUsePurpose.CleansingCharm ||
        slot == 8 && purpose == FloorItemUsePurpose.FortuneBeforeFate ||
        (slot is FloorInitPlanner.StrengthPomanderSlotIndex or FloorInitPlanner.SteelPomanderSlotIndex or
            FloorInitPlanner.HastePomanderSlotIndex) && (boss || purpose is FloorItemUsePurpose.GoldChestOvercap or FloorItemUsePurpose.HighFloorBuff);
}
