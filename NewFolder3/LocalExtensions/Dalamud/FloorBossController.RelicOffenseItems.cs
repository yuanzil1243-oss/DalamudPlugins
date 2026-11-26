using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorBossController
{
    // Called inside the original preparation/buff path, after its movement
    // strategy has allowed item use. Never bypass spin/mechanic suppression.
    private unsafe bool TryUseLocalRelicBossFrailty(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || dd->DeepDungeonId != 2 || dd->Floor != _floor ||
            _ctx.FarmingPlan?.RelicMaterial != true || _ctx.Duty.PassageOpen || _ctx.StatusIsError ||
            !_ctx.EntryReadyForDuty || _ctx.DutyCompletionObserved || _ctx.DutyFailureObserved ||
            _ctx.AttemptAborted || _ctx.CancellationSource.IsCancellationRequested ||
            _ctx.Duty.StateReadFailed || _ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning ||
            Service.LocalPlayer is not { IsDead: false } || _items.Session.DispatchedThisUpdate)
            return false;
        var snapshot = new RelicOffenseItemSnapshot(dd->DeepDungeonId, dd->Floor, false,
            _items.CanAttemptPomanderUse(), true, false, false,
            CombatBuffUsable(ProgressionItemPolicy.FrailtySlot),
            _pomanderManager.IsActive(ProgressionItemPolicy.FrailtySlot) ||
                _ctx.Knowledge.Floor?.WasPomanderUseConfirmed(ProgressionItemPolicy.FrailtySlot) == true,
            false, false, false, false, _pomanderManager.GetCount(ProgressionItemPolicy.FrailtySlot));
        return RelicOffenseItemPolicy.Decide(_ctx.FarmingPlan, snapshot) == RelicOffenseItem.Frailty &&
            _items.TryUsePomander(ProgressionItemPolicy.FrailtySlot, dd, "relic offense: boss reserved Frailty");
    }
}
