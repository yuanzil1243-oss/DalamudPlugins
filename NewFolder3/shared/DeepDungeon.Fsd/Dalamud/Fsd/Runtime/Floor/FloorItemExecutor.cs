using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using global::Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal readonly record struct FloorItemUseResolution(
    PendingFloorItemUse Pending, FloorItemUseConfirmationDecisionKind Decision, int CurrentCount);

/// <summary>Owns dispatch, observation, retries, and cancellation for one floor's item requests.</summary>
internal sealed class FloorItemExecutor : IDisposable
{
    private readonly RunContext _ctx;
    private readonly PomanderManager _pomanderManager;
    private readonly ChatWatchers? _chatWatchers;
    private readonly Func<FloorPhase> _getPhase;
    private readonly Action<string, bool> _recordNative;
    private readonly Action<string, object> RecordReplayEvent;
    private FloorPhase _phase => _getPhase();
    private bool _disposed;
    private readonly Dictionary<FloorItemUseKey, DateTime> _confirmedAt = new();
    public HohFloorItemBudget HohBudget { get; } = new();
    public bool RecentlyConfirmed(FloorItemUseKind kind, uint id) =>
        _confirmedAt.TryGetValue(new(kind, id), out var at) && DateTime.UtcNow - at < TimeSpan.FromSeconds(5);
    public FloorItemUseSession Session { get; } = new();
    public PendingIntuitionState PendingIntuition { get; } = new();
    private readonly FortuneBeforeFateState _fortune = new();
    public DateTime? FortuneStartedAt => _fortune.StartedAt;

    // Returns false only while the single request channel is preparing Fortune.
    // Missing/forbidden Fortune must not prevent the original Fate flow.
    public unsafe bool PrepareFortuneForFate(InstanceContentDeepDungeon* dd)
    {
        const uint slot = FloorInitPlanner.FortunePomanderSlotIndex;
        var action = _fortune.Decide(_pomanderManager.IsActive(slot),
            Session.Pending?.Purpose == FloorItemUsePurpose.FortuneBeforeFate, _pomanderManager.GetCount(slot),
            DeepDungeonFloorItemUsePolicy.CanUsePomanders(dd->DeepDungeonBanId) && !IsPomanderBlockedForFloor(slot), DateTime.UtcNow);
        if (action == FortunePreparation.Ready) return true;
        if (action == FortunePreparation.RequestFortune && CanAttemptPomanderUse(allowCombat: true) &&
            TryUsePomander(slot, dd, "fortune before fate", FloorItemUsePurpose.FortuneBeforeFate)) _fortune.RecordDispatch();
        return false;
    }

    public FloorItemExecutor(RunContext context, PomanderManager pomanders, ChatWatchers? watchers,
        Func<FloorPhase> getPhase, Action<string, bool> recordNative, Action<string, object> record)
    {
        _ctx = context;
        _pomanderManager = pomanders;
        _chatWatchers = watchers;
        _getPhase = getPhase;
        _recordNative = recordNative;
        RecordReplayEvent = record;
    }

    private void RecordNativeIntuitionState(string checkpoint, bool force) => _recordNative(checkpoint, force);

    public void Dispose()
    {
        _disposed = true;
        Session.Cancel();
    }
    public bool CanAttemptPomanderUse(bool allowCombat = false) => CanAttemptPomanderUse(this, allowCombat);

    public static bool CanAttemptPomanderUse(FloorItemExecutor? executor, bool allowCombat = false)
    {
        if (executor?.Session.Pending != null ||
            (!allowCombat && Service.Condition[ConditionFlag.InCombat]) ||
            Service.Condition[ConditionFlag.Casting] ||
                    Service.LocalPlayer?.IsCasting == true ||
                    !PomanderManager.CanDispatchItemRequest() ||
            Service.Condition[ConditionFlag.BetweenAreas] ||
            Service.Condition[ConditionFlag.BetweenAreas51])
        {
            return false;
        }

        if (DateTime.UtcNow < executor?.Session.NextUseAtUtc)
            return false;

        return true;
    }

    public bool HohEmergency
    {
        get
        {
            if (_ctx.Duty.DungeonId != 2 || Service.LocalPlayer is not { MaxHp: > 0 } p) return false;
            int attackers = Service.GameObjects.OfType<global::Dalamud.Game.ClientState.Objects.Types.IBattleChara>()
                .Count(n => n.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                    (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)n.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant &&
                    !n.IsDead && n.IsTargetable && n.CurrentHp > 0 && n.TargetObjectId == p.GameObjectId);
            return HohItemPolicy.IsEmergency(p.CurrentHp, p.MaxHp, attackers);
        }
    }

    internal bool AllowsHohBossHp(uint slotIndex)
    {
        if (_ctx.Duty.DungeonId != 2 || !_ctx.Duty.IsBossFloor ||
            slotIndex != HohItemPolicy.Frailty && slotIndex != FloorInitPlanner.StrengthPomanderSlotIndex) return true;
        var boss = Service.GameObjects.OfType<global::Dalamud.Game.ClientState.Objects.Types.IBattleChara>()
            .Where(e => e.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)e.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant &&
                e.IsTargetable && !e.IsDead && e.CurrentHp > 0 && e.MaxHp > 0)
            .OrderByDescending(e => e.MaxHp).FirstOrDefault();
        if (slotIndex == HohItemPolicy.Frailty)
            return boss != null && HohItemPolicy.AllowBossFrailty(boss.CurrentHp, boss.MaxHp, HohBudget.BossFrailtyConfirmed);
        // Initial preparation is allowed before a boss is visible. Missing information
        // during combat / revival cannot authorize another offensive buff.
        return boss != null ? HohItemPolicy.AllowBossStrengthRefresh(boss.CurrentHp, boss.MaxHp) :
            !Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat] &&
            !_ctx.HohContinuity.BossRebuffPending;
    }

    public unsafe bool IsPomanderAvailableForFloorUse(uint slotIndex, FloorItemUsePurpose purpose = FloorItemUsePurpose.Automatic)
    {
        if (!AllowsHohBossHp(slotIndex)) return false;
        if (_ctx.Duty.DungeonId == 2)
        {
            // Boss entry does not run the normal-floor maintenance loop: reset here as well.
            _ctx.HohPetrification.BeginFloor(_ctx.Duty.DungeonId, _ctx.Duty.Floor);
            bool overflow = purpose == FloorItemUsePurpose.GoldChestOvercap;
            int count = _pomanderManager.GetCount(slotIndex);
            if (slotIndex == HohItemPolicy.Frailty && !Hoh70FloorPolicy.AllowsFrailty(
                    _ctx.HohPetrification.UsedThisFloor || _pomanderManager.IsActive(HohItemPolicy.Petrification),
                    Session.Pending?.Key == new FloorItemUseKey(FloorItemUseKind.Pomander, HohItemPolicy.Petrification))) return false;
            if (slotIndex == FloorInitPlanner.SerenityPomanderSlotIndex)
            {
                var framework = EventFramework.Instance();
                var native = framework == null ? null : framework->GetInstanceContentDeepDungeon();
                if (!Hoh70FloorPolicy.AllowsSerenity(_ctx.Duty.Floor, count, native != null &&
                        native->DeepDungeonBanId == DeepDungeonFloorItemUsePolicy.PomanderUseProhibitedBanId,
                        native != null && DeepDungeonFloorEffectPolicy.HasHarmfulSerenityRemovableEffect(
                            native->DeepDungeonStatusId, native->DeepDungeonBanId, native->DeepDungeonDangerId))) return false;
            }
            if (!(overflow && slotIndex == HohItemPolicy.Frailty) && !HohBudget.AllowsPomander(slotIndex, DateTime.UtcNow) ||
                !HohItemPolicy.AllowPomander(_ctx.Duty.Floor, slotIndex, count, _ctx.Duty.IsBossFloor, HohEmergency,
                    overflow || count >= 3 && slotIndex is not (HohItemPolicy.Frailty or HohItemPolicy.Petrification),
                    slotIndex == FloorInitPlanner.SteelPomanderSlotIndex && _ctx.HohCentaurFightActive,
                    slotIndex == HohItemPolicy.Petrification ? Enumerable.Range(1,4).Sum(id => _pomanderManager.GetStoneCount((byte)id)) : 0)) return false;
            if (slotIndex == 4 && !overflow && count < 3 && !HohItemPolicy.SupplyBuffsLow(_pomanderManager.GetCount(2), _pomanderManager.GetCount(3))) return false;
            if (slotIndex == HohItemPolicy.Petrification && !HohItemPolicy.AllowPetrification(_ctx.Duty.Floor,
                _ctx.Duty.IsBossFloor, _ctx.Duty.PassageOpen, HohBudget.MagiciteConfirmed,
                _ctx.HohPetrification.Clearing || _pomanderManager.IsActive(slotIndex), overflow, count,
                Enumerable.Range(1,4).Sum(id => _pomanderManager.GetStoneCount((byte)id)))) return false;

        }
        return !IsPomanderBlockedForFloor(slotIndex) &&
               _pomanderManager.IsUsable(slotIndex);
    }

    public bool IsPomanderBlockedForFloor(uint slotIndex) =>
        Session.IsBlocked(
            new FloorItemUseKey(FloorItemUseKind.Pomander, slotIndex), DateTime.UtcNow) == true;

    public bool IsStoneBlockedForFloor(byte stoneId) =>
        Session.IsBlocked(
            new FloorItemUseKey(FloorItemUseKind.Stone, stoneId), DateTime.UtcNow) == true;

    public unsafe int GetStoneCountAvailableForFloorUse(byte stoneId)
    {
        if (_ctx.Duty.DungeonId == 2)
        {
            var dd=(InstanceContentDeepDungeon*)_ctx.CurrentDeepDungeon;
            if (dd==null || dd->DeepDungeonId!=2 || dd->Floor!=_ctx.Duty.Floor ||
                !DeepDungeonFloorItemUsePolicy.CanUseHohMagicite(dd->DeepDungeonBanId)) return 0;
        }
        return IsStoneBlockedForFloor(stoneId)
            ? 0
            : _pomanderManager.GetStoneCount(stoneId);
    }

    public unsafe bool TryUsePomander(
        uint slotIndex,
        InstanceContentDeepDungeon* dd,
        string reason,
        FloorItemUsePurpose purpose = FloorItemUsePurpose.Automatic)
    {
        bool hohBanSerenity = IsHohBanSerenity(dd, slotIndex);
        bool hohFinalSerenity = IsHohFinalSerenity(dd, slotIndex);
        if (_disposed || dd == null ||
            slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex &&
                (!FreeExplorationHoardPolicy.AllowsNewIntuition(_ctx.FarmingPlan?.Mode, dd->Floor) ||
                 _pomanderManager.IsActive(slotIndex) || _chatWatchers?.IntuitionActive == true ||
                 _chatWatchers?.HasCurrentFloorIntuitionUse == true) ||
            ProgressionItemPolicy.AffectsNextFloor(slotIndex) &&
                !ProgressionItemPolicy.CanBenefitNextFloor(dd->DeepDungeonId, dd->Floor, _ctx.FarmingPlan?.StopAfterFloor) ||
            !CanAttemptPomanderUse(allowCombat: hohBanSerenity || hohFinalSerenity || ProgressionItemPolicy.AllowsCombat(slotIndex, _ctx.Duty.IsBossFloor, purpose, dd->DeepDungeonId)) ||
            !hohBanSerenity && !DeepDungeonFloorItemUsePolicy.CanUsePomanders(dd->DeepDungeonBanId) ||
            !IsPomanderAvailableForFloorUse(slotIndex, purpose))
            return false;

        int countBeforeDispatch = _pomanderManager.GetCount(slotIndex);
        long sightLogSequenceBeforeDispatch =
            _chatWatchers?.SightLogSequence ?? 0;
        long mazerootLogSequenceBeforeDispatch =
            _chatWatchers?.MazerootLogSequence ?? 0;
        if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
            RecordNativeIntuitionState($"before-intuition-use:{reason}", force: true);

        long intuitionAttemptId = 0;
        long intuitionExpectedAtMilliseconds = 0;
        DateTime intuitionExpectedAtUtc = DateTime.MinValue;
        if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
        {
            intuitionExpectedAtMilliseconds = Environment.TickCount64;
            intuitionExpectedAtUtc = DateTime.UtcNow;
            intuitionAttemptId = _chatWatchers?.ExpectIntuitionResult(dd->Floor) ?? 0;
            PendingIntuition.MarkUsed(dd->Floor, intuitionExpectedAtUtc, intuitionAttemptId);
        }

        if (_pomanderManager.Use(slotIndex))
        {
            DateTime dispatchedAtUtc = DateTime.UtcNow;
            bool blocksFloorSetup =
                _phase == FloorPhase.FloorSetup &&
                slotIndex is FloorInitPlanner.IntuitionPomanderSlotIndex or
                    FloorInitPlanner.SightPomanderSlotIndex;
            var pending = Session.Begin(
                new FloorItemUseKey(FloorItemUseKind.Pomander, slotIndex),
                purpose,
                countBeforeDispatch,
                dispatchedAtUtc,
                reason,
                blocksFloorSetup,
                sightLogSequenceBeforeDispatch,
                mazerootLogSequenceBeforeDispatch,
                intuitionAttemptId,
                intuitionExpectedAtMilliseconds);
            if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
                _chatWatchers?.MarkIntuitionUsePendingThisFloor();
            Service.Log.Info(
                $"[FloorPhase] Dispatched pomander slot {slotIndex} request ({reason}) on floor {dd->Floor}; awaiting confirmation");
            RecordReplayEvent("floor-item-use-dispatched", new
            {
                floor = dd->Floor,
                kind = pending.Key.Kind.ToString(),
                itemId = pending.Key.ItemId,
                nativeItemId = dd->Items[(int)slotIndex].ItemId,
                dungeonId = dd->DeepDungeonId,
                purpose = pending.Purpose.ToString(),
                attempt = pending.AttemptNumber,
                reason,
                countBeforeDispatch,
                intuitionAttemptId
            });
            if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
                RecordNativeIntuitionState($"after-intuition-use:{reason}:dispatched", force: true);
            return true;
        }

        if (slotIndex == FloorInitPlanner.IntuitionPomanderSlotIndex)
        {
            PendingIntuition.CancelAttempt(intuitionAttemptId);
            _chatWatchers?.CancelExpectedIntuitionResult(intuitionAttemptId);
            RecordNativeIntuitionState($"after-intuition-use:{reason}:failed", force: true);
        }
        Session.DelayRejectedRequest(DateTime.UtcNow);
        return false;
    }

    private bool IsHohBanSerenityScope => _ctx.FarmingPlan?.Mode == FarmingMode.FreeExploration &&
        _ctx.Duty.DungeonId == 2 && _ctx.Duty.Floor is >= 91 and <= 99;
    private unsafe bool IsHohBanSerenity(InstanceContentDeepDungeon* dd, uint slot) =>
        IsHohBanSerenityScope && dd != null && dd->DeepDungeonId == 2 && dd->Floor == _ctx.Duty.Floor &&
        slot == FloorInitPlanner.SerenityPomanderSlotIndex &&
        dd->DeepDungeonBanId == DeepDungeonFloorItemUsePolicy.PomanderUseProhibitedBanId;
    private unsafe bool IsHohFinalSerenity(InstanceContentDeepDungeon* dd, uint slot) =>
        IsHohBanSerenityScope && dd != null && dd->Floor == 99 && dd->DeepDungeonId == 2 &&
        slot == FloorInitPlanner.SerenityPomanderSlotIndex &&
        DeepDungeonFloorEffectPolicy.HasHarmfulSerenityRemovableEffect(
            dd->DeepDungeonStatusId, dd->DeepDungeonBanId, dd->DeepDungeonDangerId);
    internal unsafe bool TryUseHohSerenityForItemBan(InstanceContentDeepDungeon* dd) =>
        (IsHohBanSerenity(dd, FloorInitPlanner.SerenityPomanderSlotIndex) ||
         IsHohFinalSerenity(dd, FloorInitPlanner.SerenityPomanderSlotIndex)) &&
        TryUsePomander(FloorInitPlanner.SerenityPomanderSlotIndex, dd,
            dd->Floor == 99 ? "HOH final combat floor: remove harmful floor effects" :
                "HOH 91–98 reserved serenity: remove item-use prohibition");

    public unsafe bool TryDispatchFloorStone(
        byte stoneId,
        InstanceContentDeepDungeon* dd,
        string reason,
        FloorItemUsePurpose purpose)
    {
        if (_disposed ||
            dd == null || dd->DeepDungeonId == 2 &&
                !DeepDungeonFloorItemUsePolicy.CanUseHohMagicite(dd->DeepDungeonBanId) ||
            dd->DeepDungeonId == 2 &&
                (purpose is not (FloorItemUsePurpose.HohMagicite or FloorItemUsePurpose.HohMagiciteOvercap) ||
                 purpose == FloorItemUsePurpose.HohMagicite && HohBudget.MagiciteConfirmed) ||
            stoneId == 1 && EndgameItemPolicy.ReserveFate(dd->DeepDungeonId, dd->Floor, _ctx.FarmingPlan?.Mode) ||
            !CanAttemptPomanderUse(allowCombat: purpose is FloorItemUsePurpose.HohMagicite or FloorItemUsePurpose.HohMagiciteOvercap or FloorItemUsePurpose.SilverChestOvercap or FloorItemUsePurpose.FinalFloorWisdom ||
                purpose is FloorItemUsePurpose.BossSerenity or FloorItemUsePurpose.BossFate && _ctx.Duty.IsBossFloor) ||
            dd->DeepDungeonId != 2 && !DeepDungeonFloorItemUsePolicy.CanUsePtIncense(
                dd->DeepDungeonBanId) ||
            GetStoneCountAvailableForFloorUse(stoneId) <= 0)
        {
            return false;
        }

        int countBeforeDispatch = _pomanderManager.GetStoneCount(stoneId);
        long sightLogSequenceBeforeDispatch =
            _chatWatchers?.SightLogSequence ?? 0;
        long mazerootLogSequenceBeforeDispatch =
            _chatWatchers?.MazerootLogSequence ?? 0;
        if (!_pomanderManager.UseStone(stoneId))
        {
            Session.DelayRejectedRequest(DateTime.UtcNow);
            return false;
        }

        DateTime dispatchedAtUtc = DateTime.UtcNow;
        var pending = Session.Begin(
            new FloorItemUseKey(FloorItemUseKind.Stone, stoneId),
            purpose,
            countBeforeDispatch,
            dispatchedAtUtc,
            reason,
            _phase == FloorPhase.FloorSetup &&
            purpose == FloorItemUsePurpose.NaturalReveal,
            sightLogSequenceBeforeDispatch,
            mazerootLogSequenceBeforeDispatch,
            0,
            0);
        Service.Log.Info(
            $"[FloorPhase] Dispatched {(dd->DeepDungeonId == 2 ? "HOH magicite" : "PT incense")} {stoneId} request ({reason}) on floor {dd->Floor}; awaiting confirmation");
        RecordReplayEvent("floor-item-use-dispatched", new
        {
            floor = dd->Floor,
            kind = pending.Key.Kind.ToString(),
            itemId = pending.Key.ItemId,
            purpose = pending.Purpose.ToString(),
            attempt = pending.AttemptNumber,
            reason,
            countBeforeDispatch
        });
        return true;
    }

    public unsafe FloorItemUseResolution? ResolvePending(InstanceContentDeepDungeon* dd)
    {
        var pending = Session.Pending;
        if (pending == null)
            return null;

        int currentCount = pending.Key.Kind == FloorItemUseKind.Pomander
            ? _pomanderManager.GetCount(pending.Key.ItemId)
            : _pomanderManager.GetStoneCount((byte)pending.Key.ItemId);
        bool authoritativeConfirmationObserved =
            pending.AuthoritativeConfirmationObserved ||
            pending.Key is
            {
                Kind: FloorItemUseKind.Pomander,
                ItemId: FloorInitPlanner.SightPomanderSlotIndex
            } &&
            (_chatWatchers?.SightLogSequence ?? 0) >
            pending.SightLogSequenceBeforeDispatch ||
            dd != null && dd->DeepDungeonId == 4 && pending.Key is
            {
                Kind: FloorItemUseKind.Stone,
                ItemId: 2
            } &&
            (_chatWatchers?.MazerootLogSequence ?? 0) >
            pending.MazerootLogSequenceBeforeDispatch;
        var decision = Session.Observe(currentCount, authoritativeConfirmationObserved, DateTime.UtcNow);

        switch (decision)
        {
            case FloorItemUseConfirmationDecisionKind.PendingConfirmation:
                return null;
            case FloorItemUseConfirmationDecisionKind.Confirmed:
                _confirmedAt[pending.Key] = DateTime.UtcNow;
                if (dd != null && dd->DeepDungeonId == 2)
                {
                    if (pending.Key.Kind == FloorItemUseKind.Stone) HohBudget.RecordMagiciteConfirmed();
                    else
                    {
                        HohBudget.RecordPomanderConfirmed(pending.Key.ItemId, DateTime.UtcNow, _ctx.Duty.IsBossFloor);
                        if (pending.Key.ItemId == HohItemPolicy.Petrification) _ctx.HohPetrification.ConfirmUse(DateTime.UtcNow);
                    }
                }
                Session.Confirm(pending);
                return new FloorItemUseResolution(pending, decision, currentCount);
            case FloorItemUseConfirmationDecisionKind.WaitingToRetry:
                RecordUnconfirmedFloorItemUse(dd, pending, currentCount, exhausted: false);
                return null;
            case FloorItemUseConfirmationDecisionKind.RetryReady:
                RecordUnconfirmedFloorItemUse(dd, pending, currentCount, exhausted: false);
                CancelPendingIntuitionAttempt(pending);
                Session.ReleaseForRetry(pending);
                return null;
            case FloorItemUseConfirmationDecisionKind.Exhausted:
                RecordUnconfirmedFloorItemUse(dd, pending, currentCount, exhausted: true);
                CancelPendingIntuitionAttempt(pending);
                Session.Exhaust(pending, DateTime.UtcNow);
                return new FloorItemUseResolution(pending, decision, currentCount);
        }
        return null;
    }

    public void CancelPendingIntuitionAttempt(PendingFloorItemUse pending)
    {
        if (pending.Key is not
            {
                Kind: FloorItemUseKind.Pomander,
                ItemId: FloorInitPlanner.IntuitionPomanderSlotIndex
            })
        {
            return;
        }

        PendingIntuition.CancelAttempt(pending.IntuitionAttemptId);
        _chatWatchers?.CancelExpectedIntuitionResult(pending.IntuitionAttemptId);
        _chatWatchers?.CancelPendingIntuitionUseThisFloor();
    }

    private unsafe void RecordUnconfirmedFloorItemUse(
        InstanceContentDeepDungeon* dd,
        PendingFloorItemUse pending,
        int currentCount,
        bool exhausted)
    {
        if (!pending.TryRecordUnconfirmed())
            return;
        Service.Log.Warning(
            $"[FloorPhase] Floor item request was not confirmed: {pending.Key.Kind} {pending.Key.ItemId}, attempt {pending.AttemptNumber}, reason={pending.Reason}, exhausted={exhausted}");
        RecordReplayEvent("floor-item-use-unconfirmed", new
        {
            floor = dd->Floor,
            kind = pending.Key.Kind.ToString(),
            itemId = pending.Key.ItemId,
            purpose = pending.Purpose.ToString(),
            attempt = pending.AttemptNumber,
            countBeforeDispatch = pending.CountBeforeDispatch,
            currentCount,
            exhausted,
            reason = pending.Reason
        });
    }


}
