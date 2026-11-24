using DeepDungeon.Fsd.Core;
using Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private unsafe void TryMaintainEndgameItems(InstanceContentDeepDungeon* dd)
    {
        if (Kind != FloorRuntimeKind.Normal || _ctx.ControlledPtSurvey != null ||
            !EndgameItemPolicy.IsProgression(_ctx.FarmingPlan?.Mode) ||
            dd->DeepDungeonId != 4 || dd->Floor is < 91 or > 98 ||
            !Items.CanAttemptPomanderUse(allowCombat: true)) return;

        // Only 98 gets the mid-floor pickup exception. All stone dispatch paths also
        // enforce its Fate reservation in FloorItemExecutor, including capped chests.
        if (dd->Floor == 98 && Items.GetStoneCountAvailableForFloorUse(2) > 0 &&
            TryDispatchFloorStone(2, dd, "98 wisdom pickup", FloorItemUsePurpose.FinalFloorWisdom)) return;

        bool clearingNeeded = !_ctx.Duty.PassageOpen || Service.Condition[ConditionFlag.InCombat];
        var player = Service.LocalPlayer;
        if (player == null) return;
        foreach (var (slot, status) in new (uint, uint)[]
        {
            (FloorInitPlanner.StrengthPomanderSlotIndex, 687),
            (FloorInitPlanner.SteelPomanderSlotIndex, 1100),
            (FloorInitPlanner.HastePomanderSlotIndex, 4718)
        })
        {
            bool active = _pomanderManager.IsActive(slot) || player.StatusList.Any(s => s.StatusId == status) ||
                Items.RecentlyConfirmed(FloorItemUseKind.Pomander, slot);
            if (EndgameItemPolicy.UseHighFloorBuff(dd->DeepDungeonId, dd->Floor, _ctx.FarmingPlan?.Mode,
                _pomanderManager.GetCount(slot), active, clearingNeeded) &&
                Items.TryUsePomander(slot, dd, "91-98 use surplus; reserve one for 99", FloorItemUsePurpose.HighFloorBuff)) return;
        }
    }
}
