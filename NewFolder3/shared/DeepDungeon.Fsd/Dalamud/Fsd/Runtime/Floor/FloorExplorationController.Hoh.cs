using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Runtime;
using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using DeepDungeon.Fsd.Dalamud.Map;
using global::Dalamud.Game.ClientState.Objects.Enums;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    // Run independently of the optional search/general-tick branch: combat,
    // priority fights, chest handling and passage travel must not starve purity.
    internal unsafe void TryUseHohCurseRecovery(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || dd->DeepDungeonId != 2 || dd->Floor is < 1 or >= 100 ||
            _ctx.FarmingPlan?.DungeonId != 2 || _ctx.ControlledPtSurvey != null ||
            !HasLocalPlayerStatus(DeepDungeonCurseStatusId)) return;
        Items.TryUsePomander(FloorInitPlanner.PurityPomanderSlotIndex, dd,
            "HOH automatic purity: curse status 1087");
    }

    private bool _hohPetrificationHolding;
    private bool _hohClearReserveRecorded;
    private readonly DeepDungeon.Fsd.Runtime.HohPetrifiedContactState _hohPetrifiedContact = new();
    private bool HasPendingHohRelief => DungeonId == 2 && ActiveChestAttempt is { } a &&
        (a.PendingHohMagiciteOvercap || a.PendingGoldOvercapSlotIndex != null || a.HohRelief.AwaitingLoot);
    private int HohMagiciteStock => Enumerable.Range(1, 4).Sum(id => _pomanderManager.GetStoneCount((byte)id));
    private static bool IsPetrified(IBattleChara enemy) => enemy.StatusList.Any(s => s.StatusId == HohPetrificationState.StoneCurseStatus);
    private IBattleChara[] PetrifiedEnemies() => Service.GameObjects.OfType<IBattleChara>()
        .Where(e => e.ObjectKind == ObjectKind.BattleNpc && (BattleNpcSubKind)e.SubKind == BattleNpcSubKind.Combatant &&
            e.IsTargetable && !e.IsDead && e.CurrentHp > 0 && IsPetrified(e)).ToArray();

    private IBattleChara? SelectHohPetrifiedTarget(Vector3 player) => _ctx.SelectHohPetrifiedTarget(PetrifiedEnemies(), player);

    private void UpdateHohPetrificationObservation()
    {
        _ctx.HohPetrification.Observe(_pomanderManager.IsActive(HohItemPolicy.Petrification), PetrifiedEnemies().Length > 0,
            _ctx.Duty.PassageOpen, DateTime.UtcNow);
        if (ActiveChestAttempt is { HohRelief.AwaitingLoot: true } a)
        {
            int count = a.HohRelievedPomander is { } slot ? _pomanderManager.GetCount(slot) : HohMagiciteStock;
            bool nativeComplete = _ctx.ChestInteraction.Observe(a, ObjectEvidence.Current).Decision.Complete;
            if (a.HohRelief.ObserveLoot(count, nativeComplete))
                RecordReplayEvent("hoh-overcap-original-chest-recovered", new { floor = Floor, entityId = a.EntityId, count, nativeComplete });
        }
    }

    internal void HandleHohMagiciteOvercapObserved()
    {
        var a = ActiveChestAttempt;
        if (IsDisposed || DungeonId != 2 || Kind != FloorRuntimeKind.Normal ||
            _activeWaypoint?.Type != RoomObjectiveType.ChestSilver || a == null || a.EntityId == 0 ||
            _taskRunner?.Phase != TaskPhase.WaitingPost ||
            DateTime.UtcNow - a.InteractionStartedAtUtc > TimeSpan.FromSeconds(5) ||
            a.HohRelief.Consumed || HohMagiciteStock != 3) return;
        if (!a.PendingHohMagiciteOvercap) a.OvercapReliefDeadline = DateTime.UtcNow.AddSeconds(15);
        a.PendingHohMagiciteOvercap = true;
        RecordReplayEvent("hoh-magicite-overcap-correlated", new { floor = Floor, entityId = a.EntityId, stock = HohMagiciteStock });
    }

    private unsafe bool TryHandleHohMagiciteOvercap(InstanceContentDeepDungeon* dd, IPlayerCharacter player)
    {
        var a = ActiveChestAttempt;
        if (dd->DeepDungeonId != 2 || a?.PendingHohMagiciteOvercap != true) return false;
        if (Items.Session.Pending != null) return true;
        bool usable=DeepDungeonFloorItemUsePolicy.CanUseHohMagicite(dd->DeepDungeonBanId);
        // A manual consumption can also free space; never consume again simply to satisfy the old event.
        if (usable && HohMagiciteStock < 3)
        { a.HohRelief.ConfirmConsumption(HohMagiciteStock); a.PendingHohMagiciteOvercap = false; a.NextInteractAt = DateTime.MinValue; return false; }
        if (usable && a.HohRelief.CanRelieve(true, HohMagiciteStock, false) && DateTime.UtcNow < a.OvercapReliefDeadline)
        {
            for (byte id = 1; id <= 4; id++)
                if (Items.GetStoneCountAvailableForFloorUse(id) > 0)
                {
                    Items.TryDispatchFloorStone(id, dd, "HOH confirmed new magicite: free one slot and reopen original chest", FloorItemUsePurpose.HohMagiciteOvercap);
                    return true;
                }
        }
        a.PendingHohMagiciteOvercap = false;
        var phase = _taskRunner!.Phase; double elapsed = _taskRunner.ElapsedSeconds;
        _taskRunner.Reset();
        HandleTaskSkip(dd, player, usable?"HohMagiciteOvercapExhausted":"HohMagiciteUseProhibited", WaypointOutcomeKind.PolicySkipped, elapsed, phase);
        return true;
    }

    private unsafe bool TryUpdateHohPetrification(InstanceContentDeepDungeon* dd)
    {
        if (dd->DeepDungeonId != 2) return false;
        // The original capped chest is recovered before exploration is suspended for the clear.
        bool clearing = _ctx.HohPetrification.Clearing && !HasPendingHohRelief;
        if (!clearing)
        {
            if (_hohPetrificationHolding)
            {
                _hohPetrificationHolding = false; _navHelper.Cancel();
                _hohPetrifiedContact.Reset(); _ctx.ClearPreferredAggroTarget();
                _heldRecovery?.ResetEngagedTargetProgress(); _taskRunner?.SetSuspended(false);
                RecordReplayEvent("hoh-petrification-clear-finished", new { floor = Floor, passageOpen = _ctx.Duty.PassageOpen });
            }
            return false;
        }
        if (!_hohPetrificationHolding)
        {
            _hohPetrificationHolding = true; _navDriver.Cancel();
            ActiveExecution?.Recovery.YieldMovement();
            RecordReplayEvent("hoh-petrification-clear-started", new { floor = Floor });
        }
        _taskRunner?.SetSuspended(true);
        var player = Service.LocalPlayer;
        var enemy = player == null ? null : SelectHohPetrifiedTarget(player.Position);
        if (player == null || enemy == null)
        {
            if (_hohPetrifiedContact.TargetId != 0)
            { _navHelper.Cancel(); _hohPetrifiedContact.Reset(); _ctx.ClearPreferredAggroTarget(); }
            // With no petrified target in this room, normal exploration must keep moving
            // so patrol can find remaining enemies and open the passage.
            _taskRunner?.SetSuspended(false);
            if (player != null && HohHighFloorPolicy.Enabled(_ctx.FarmingPlan?.Mode, DungeonId, Floor)) return false;
            _status = "石化清怪：当前房间没有可选目标，继续巡查";
            return false;
        }
        DriveHohPetrifiedContact(enemy, player, DateTime.UtcNow);
        return true;
    }

    private void DriveHohPetrifiedContact(IBattleChara enemy, IPlayerCharacter player, DateTime now)
    {
        bool changed = _hohPetrifiedContact.TargetId != enemy.GameObjectId;
        if (changed)
        {
            _heldRecovery?.ResetEngagedTargetProgress(); _navHelper.Cancel();
            RecordReplayEvent("hoh-petrified-contact-started", new { floor = Floor,
                targetId = enemy.GameObjectId, player = player.Position, destination = enemy.Position,
                radius = DeepDungeon.Fsd.Runtime.HohPetrifiedContactState.FootRadius });
        }
        _ctx.SetPreferredAggroTarget(enemy.GameObjectId, enemy.Position);
        // Start output immediately; failed casts and active casts must never delay the approach.
        _ctx.PrepareHohPetrifiedOutput(enemy, player.Position);
        long hit = _ctx.HohCombatTargets.TryGetHit(enemy.GameObjectId, out var nativeHit) ? nativeHit.Revision : 0;
        var step = _hohPetrifiedContact.Update(enemy.GameObjectId, player.Position, enemy.Position, enemy.CurrentHp, hit, now);
        if (step == DeepDungeon.Fsd.Runtime.HohPetrifiedContactStep.Attack)
        {
            if (_navHelper.HasActiveTarget) _navHelper.Cancel();
            _status = "石化清怪：已到目标脚下，攻击当前目标";
            return;
        }
        if (step == DeepDungeon.Fsd.Runtime.HohPetrifiedContactStep.Approach)
        {
            var nav = _navHelper.Navigate(enemy.Position, player.Position,
                DeepDungeon.Fsd.Runtime.HohPetrifiedContactState.FootRadius);
            if (nav is not (NavigationState.Failed or NavigationState.StuckGiveUp))
            { _status = "石化清怪：已选中目标，边接近边尝试攻击"; return; }
        }
        _navHelper.Cancel();
        _heldRecovery ??= new CombatPositionRecoveryController(_ctx, _navHelper, _chaseHelper, NormalGraph, RecoveryBudget, RunTelemetry, _setStatus, RecordReplayEvent);
        _heldRecovery.FailPetrifiedContact(enemy, player, step == DeepDungeon.Fsd.Runtime.HohPetrifiedContactStep.OutputStalled
            ? "petrified-contact-no-output" : "petrified-contact-approach-unreachable");
        _hohPetrifiedContact.Reset();
    }

    private unsafe void TryMaintainHohItems(InstanceContentDeepDungeon* dd)
    {
        if (dd == null || dd->DeepDungeonId != 2 || _ctx.FarmingPlan?.DungeonId != 2 ||
            dd->Floor is < 1 or >= 100 || dd->Floor % 10 == 0) return;
        _ctx.HohPetrification.BeginFloor(dd->DeepDungeonId, dd->Floor);
        UpdateHohPetrificationObservation();
        if (HasPendingHohRelief) return;
        bool emergency = Items.HohEmergency;
        if (Items.TryUseHohSerenityForItemBan(dd)) return;
        int stock = 0; byte stone = 0;
        for (byte id = 1; id <= 4; id++)
        {
            int count = Items.GetStoneCountAvailableForFloorUse(id); stock += count;
            if (stone == 0 && count > 0) stone = id;
        }
        if (dd->Floor == 91 && !_hohClearReserveRecorded)
        {
            _hohClearReserveRecorded = true;
            int magicite = HohMagiciteStock, petrification = _pomanderManager.GetCount(HohItemPolicy.Petrification);
            RecordReplayEvent("hoh91-clear-item-reserve-observed", new { floor = Floor, magicite, petrification,
                combined = magicite + petrification, required = 4, reserveMet = magicite + petrification >= 4,
                preferredMagicite = 3, intendedWindow = "94-97", inventoryGuaranteed = false });
        }
        bool enemies = Service.GameObjects.OfType<global::Dalamud.Game.ClientState.Objects.Types.IBattleChara>()
            .Any(n => n.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)n.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant &&
                n.IsTargetable && !n.IsDead && n.CurrentHp > 0);
        if (HohItemPolicy.ShouldUseMagicite(dd->Floor, stock, _ctx.Duty.PassageOpen, emergency, enemies, Items.HohBudget.MagiciteConfirmed) &&
            Items.TryDispatchFloorStone(stone, dd, "HOH budgeted floor clear", FloorItemUsePurpose.HohMagicite)) return;
        if (TryMaintainHohCombatBuffs(dd)) return;
        if (enemies && Items.IsPomanderAvailableForFloorUse(HohItemPolicy.Petrification) &&
            Items.TryUsePomander(HohItemPolicy.Petrification, dd, "HOH 94+ petrification clear", FloorItemUsePurpose.HohPetrification)) return;
        // Prefer a confirmed room clear over spending Frailty immediately before Petrification.
        if (!_ctx.Duty.PassageOpen && Items.IsPomanderAvailableForFloorUse(HohItemPolicy.Frailty) &&
            !_pomanderManager.IsActive(HohItemPolicy.Frailty) &&
            Items.TryUsePomander(HohItemPolicy.Frailty, dd, "HOH surplus frailty", FloorItemUsePurpose.HohFrailty)) return;
        if (HohItemPolicy.IsHardFloor(dd->Floor) && !_pomanderManager.IsActive(0) && !_pomanderManager.IsActive(1))
            Items.TryUsePomander(1, dd, "HOH hard-floor trap visibility");
    }

    private unsafe bool TryMaintainHohCombatBuffs(InstanceContentDeepDungeon* dd)
    {
        if (!HohHighFloorPolicy.Enabled(_ctx.FarmingPlan?.Mode, dd->DeepDungeonId, dd->Floor) ||
            Service.LocalPlayer is not { IsDead: false } player) return false;
        // Steel is reserved for an actually engaged centaur, even when a chest would overcap it.
        // Strength has no remaining boss reserve on 91–99. Both can be used during combat.
        bool Active(uint slot, uint status) => player.StatusList.Any(s => s.StatusId == status) ||
            _pomanderManager.IsActive(slot) || Items.RecentlyConfirmed(FloorItemUseKind.Pomander, slot);
        var choice = HohItemPolicy.CombatBuffToMaintain(dd->Floor, _ctx.HohCentaurFightActive,
            !_ctx.Duty.PassageOpen || _ctx.HohCombatInProgress,
            _pomanderManager.GetCount(FloorInitPlanner.SteelPomanderSlotIndex),
            Active(FloorInitPlanner.SteelPomanderSlotIndex, SteelStatusId),
            _pomanderManager.GetCount(FloorInitPlanner.StrengthPomanderSlotIndex),
            Active(FloorInitPlanner.StrengthPomanderSlotIndex, StrengthStatusId));
        return choice is { } slot && Items.TryUsePomander(slot, dd, slot == FloorInitPlanner.SteelPomanderSlotIndex
            ? "HOH engaged centaur steel" : "HOH 91-99 strength refresh", FloorItemUsePurpose.HighFloorBuff);
    }
}
