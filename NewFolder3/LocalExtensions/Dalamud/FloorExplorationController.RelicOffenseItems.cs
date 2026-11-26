using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using System.Runtime.CompilerServices;
using global::Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private static readonly ConditionalWeakTable<RunContext, HohRelicFrailtyReserve> RelicFrailtyReserves = new();

    private unsafe void ObserveLocalRelicFrailtyInventory(InstanceContentDeepDungeon* dd)
    {
        if (dd != null && dd->DeepDungeonId == 2 && _ctx.FarmingPlan is { DungeonId: 2 } plan &&
            RelicFarmingPolicy.IsValid(plan))
            RelicFrailtyReserves.GetValue(_ctx, static _ => new()).Observe(dd->Floor,
                _pomanderStock[ProgressionItemPolicy.FrailtySlot]);
    }

    private unsafe bool TryUseLocalRelicOffenseItem(InstanceContentDeepDungeon* dd,
        in GeneralAutoPomanderSnapshot general)
    {
        if (dd == null || _ctx.FarmingPlan?.RelicMaterial != true || Kind != FloorRuntimeKind.Normal ||
            _phase != FloorPhase.FloorActive || _ctx.StatusIsError || !_ctx.EntryReadyForDuty ||
            _ctx.DutyCompletionObserved || _ctx.DutyFailureObserved || _ctx.AttemptAborted ||
            _ctx.CancellationSource.IsCancellationRequested || _ctx.Duty.StateReadFailed ||
            _ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning ||
            Service.LocalPlayer is not { IsDead: false } || ItemUse.DispatchedThisUpdate)
            return false;

        var knowledge = _ctx.Knowledge.Floor;
        int frailtyCount = dd->DeepDungeonId == 2 ? _pomanderManager.GetCount(ProgressionItemPolicy.FrailtySlot) : 0;
        bool frailtyReady = dd->DeepDungeonId == 2 &&
            RelicFrailtyReserves.GetValue(_ctx, static _ => new()).Observe(dd->Floor, frailtyCount);
        var snapshot = new RelicOffenseItemSnapshot(dd->DeepDungeonId, dd->Floor, true,
            general.CanAttemptPomanderUse, !_ctx.Duty.PassageOpen || Service.Condition[ConditionFlag.InCombat],
            general.StrengthUsable, general.HasStrengthStatus,
            dd->DeepDungeonId == 2 && FloorItemExecutor.CanUsePomanderOnFloor(ProgressionItemPolicy.FrailtySlot, dd) &&
                IsPomanderAvailableForFloorUse(ProgressionItemPolicy.FrailtySlot),
            _pomanderManager.IsActive(ProgressionItemPolicy.FrailtySlot) ||
                knowledge?.WasPomanderUseConfirmed(ProgressionItemPolicy.FrailtySlot) == true,
            general.HasteUsable, general.HasHasteStatus,
            dd->DeepDungeonId == 4 && DeepDungeonFloorItemUsePolicy.CanUseStones(4, dd->DeepDungeonBanId) &&
                GetStoneCountAvailableForFloorUse(RelicOffenseItemPolicy.JuniperStoneId) > 0,
            HasLocalPlayerStatus(RelicOffenseItemPolicy.JuniperStatusId) ||
                knowledge?.WasStoneUseConfirmed(RelicOffenseItemPolicy.JuniperStoneId) == true,
            frailtyCount, frailtyReady, dd->DeepDungeonId == 4 ?
                _pomanderManager.GetStoneCount(RelicOffenseItemPolicy.JuniperStoneId) : 0);
        var decision = RelicOffenseItemPolicy.Decide(_ctx.FarmingPlan, snapshot);
        uint? slot = decision switch
        {
            RelicOffenseItem.Strength => FloorInitPlanner.StrengthPomanderSlotIndex,
            RelicOffenseItem.Frailty => ProgressionItemPolicy.FrailtySlot,
            RelicOffenseItem.Haste => FloorInitPlanner.HastePomanderSlotIndex,
            _ => null
        };
        if (slot is { } pomander)
            return TryUsePomander(pomander, dd, $"relic offense: {decision}");
        return decision == RelicOffenseItem.Juniper &&
            TryDispatchFloorStone(RelicOffenseItemPolicy.JuniperStoneId, dd,
                "relic offense: Juniper", FloorItemUsePurpose.Automatic);
    }
}
