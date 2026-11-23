using System;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>Owns one floor's boss navigation, combat buffs and diagnostic cadence.</summary>
internal sealed class FloorBossController
{
    private const float BossNavigationArrivalTolerance = 3f;
    private const uint SteelStatusId = 1100;
    private const uint StrengthStatusId = 687;
    private const uint PtHasteStatusId = 4718;
    private readonly byte _floor;
    private readonly RunContext _ctx;
    private readonly NavigationHelper _navHelper;
    private readonly PomanderManager _pomanderManager;
    private readonly FloorItemExecutor _items;
    private readonly TerminalRoomController _terminalRooms;
    // These helpers retain their run lifetime; this floor owner does not dispose or recreate them.
    private readonly Pt30DivineFavorFlashHelper? _pt30DivineFavorFlashHelper;
    private readonly Pt50ChaseOutputGuard? _pt50ChaseOutputGuard;
    private readonly Pt50IslandMovementController? _pt50IslandMovement;
    private readonly Func<string, FloorObjectiveKind, bool> _requireMovementPermission;
    private readonly Action<string> _setStatus;
    private readonly Action<string, object> _recordReplayEvent;
    private readonly Action<byte, IPlayerCharacter> _recordBossSnapshot;
    private DateTime _nextBossDiagnosticAtUtc = DateTime.MinValue;
    private bool BossNavigationResolved { get; set; }
    public bool BossSerenityAwaitingBuff { get; private set; }
    private bool _bossFateConfirmed;
    private DateTime? _revivalRebuffStarted;
    private readonly HashSet<uint> _revivalUnavailableReported = new();
    private string Status { set => _setStatus(value); }
    private FarmingItemPolicy ItemPolicy => FarmingItemPolicy.For(_ctx.FarmingPlan?.Mode);

    public FloorBossController(byte floor, RunContext context, NavigationHelper navigation,
        PomanderManager pomanders, FloorItemExecutor items, TerminalRoomController terminalRooms,
        Pt30DivineFavorFlashHelper? pt30, Pt50ChaseOutputGuard? pt50, Pt50IslandMovementController? pt50Movement,
        Func<string, FloorObjectiveKind, bool> requireMovementPermission, Action<string> setStatus,
        Action<string, object> recordReplayEvent, Action<byte, IPlayerCharacter> recordBossSnapshot)
    {
        _floor = floor;
        _ctx = context;
        _navHelper = navigation;
        _pomanderManager = pomanders;
        _items = items;
        _terminalRooms = terminalRooms;
        _pt30DivineFavorFlashHelper = pt30;
        _pt50ChaseOutputGuard = pt50;
        _pt50IslandMovement = pt50Movement;
        _requireMovementPermission = requireMovementPermission;
        _setStatus = setStatus;
        _recordReplayEvent = recordReplayEvent;
        _recordBossSnapshot = recordBossSnapshot;
    }

    public void OnSerenityConfirmed() => BossSerenityAwaitingBuff = true;
    public void OnFateConfirmed() => _bossFateConfirmed = true;

    private void RecordBossCombatSnapshot()
    {
        var player = Service.LocalPlayer;
        var now = DateTime.UtcNow;
        if (player == null || now < _nextBossDiagnosticAtUtc) return;
        _nextBossDiagnosticAtUtc = now.AddSeconds(1);
        _recordBossSnapshot(_floor, player);
    }

    public unsafe void Update(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        if (player == null)
        {
            _pt50IslandMovement?.Reset();
            Status = "Boss floor - waiting for player";
            return;
        }

        if (Service.Condition[ConditionFlag.BetweenAreas] ||
            Service.Condition[ConditionFlag.BetweenAreas51])
        {
            Status = "Boss floor - waiting for floor load to complete";
            _navHelper?.Cancel();
            _pt30DivineFavorFlashHelper?.Reset();
            _pt50ChaseOutputGuard?.Reset();
            _pt50IslandMovement?.Reset();
            return;
        }
        if (_terminalRooms.TryUpdatePt99ResultTransfer(dd)) return;
        if (dd->DeepDungeonId == 2 && TryRebuffAfterRevival(dd)) return;
        if (!_requireMovementPermission("boss objective", FloorObjectiveKind.DefeatBoss))
        {
            _pt30DivineFavorFlashHelper?.Reset();
            _pt50ChaseOutputGuard?.Reset();
            _pt50IslandMovement?.Reset();
            return;
        }

        RecordBossCombatSnapshot();
        bool externalMovement = _ctx.Configuration.BossMechanicsActive;
        bool bmrBossHandling = _ctx.Configuration.BossMechanics.UsesBmrBossHandling();
        if (bmrBossHandling) _pt30DivineFavorFlashHelper?.Update(dd);
        else _pt30DivineFavorFlashHelper?.Reset();
        // The dedicated route owns movement only after synchronously disabling BMR.
        if (_pt50IslandMovement?.Update(dd, bmrBossHandling && _ctx.Configuration.BossMechanics.Pt50IslandMovementEnabled) == true)
        {
            _pt50ChaseOutputGuard?.Reset();
            _ctx.SetBossRotationSuppressed?.Invoke(_pt50IslandMovement.IsChasing);
            _navHelper.Cancel();
            BossNavigationResolved = true;
            TryMaintainBossBuffs(dd);
            Status = _pt50IslandMovement.Status;
            return;
        }
        _pt50ChaseOutputGuard?.Update(dd, externalMovement && bmrBossHandling);
        if (dd->DeepDungeonId == 4 && dd->Floor == 99 && bmrBossHandling && _ctx.Pt99InitialColorMovementActive)
        {
            _navHelper.Cancel();
            TryMaintainBossBuffs(dd);
            Status = "99层入场：前往固定光色地板中心，等待实际Buff";
            return;
        }
        if (dd->DeepDungeonId == 4 && dd->Floor == 99 && externalMovement && bmrBossHandling)
        {
            _navHelper?.Cancel();
            TryMaintainBossBuffs(dd);
            Status = "PT99 - 18%平衡门控、实际颜色转火；机制走位优先";
            return;
        }

        if (Service.Condition[ConditionFlag.InCombat])
        {
            TryMaintainBossBuffs(dd);
            if (!BossNavigationResolved)
            {
                _navHelper?.Cancel();
                BossNavigationResolved = true;
                Service.Log.Info("[FloorPhase] Boss combat started -> canceling boss navigation");
            }

            Status = _pt30DivineFavorFlashHelper?.IsDivineFavorMovementActive == true
                ? "Boss floor - PT30 Divine Favor movement override active"
                : "Boss floor - combat assist active";
            return;
        }

        var boss = Runtime.Helpers.CombatTargetingHelpers.PickNearestHostile(60f, out _);
        if (boss == null)
        {
            if (bmrBossHandling && !externalMovement && _pt30DivineFavorFlashHelper?.TryUpdateBossEngageMovement(dd, player.Position, out var pt30EngageStatus) == true)
            {
                _navHelper?.Cancel();
                Status = pt30EngageStatus;
                return;
            }

            Status = "Boss floor - waiting for boss target";
            return;
        }

        TryMaintainBossBuffs(dd);
        if (BossNavigationResolved)
        {
            var currentTarget = Service.TargetManager.Target as IBattleChara;
            if (currentTarget != null &&
                currentTarget.GameObjectId == boss.GameObjectId &&
                !currentTarget.IsDead &&
                IsWithinBossEngageRadius(player.Position, boss.Position))
            {
                Status = "Boss floor - waiting for boss combat";
                return;
            }

            BossNavigationResolved = false;
            _recordReplayEvent("boss-navigation-reset", new
            {
                floor = dd->Floor,
                reason = "combat-ended-or-target-lost",
                bossId = boss.GameObjectId,
                currentTargetId = currentTarget?.GameObjectId ?? 0
            });
        }

        if (bmrBossHandling && !externalMovement && _pt30DivineFavorFlashHelper?.TryUpdateBossEngageMovement(dd, player.Position, out var pt30BossEngageStatus) == true)
        {
            _navHelper?.Cancel();
            Status = pt30BossEngageStatus;
            return;
        }

        var withinRange = IsWithinBossEngageRadius(player.Position, boss.Position);
        if (withinRange)
        {
            _navHelper?.Cancel();
            BossNavigationResolved = true;
            Status = "Boss floor - at boss";
            Service.Log.Info("[FloorPhase] Boss navigation complete -> in engage range");
            return;
        }

        var state = _navHelper!.Navigate(boss.Position, player.Position, BossNavigationArrivalTolerance, retryIntervalSeconds: 5.0);
        switch (state)
        {
            case NavigationState.Moving:
                Status = "Boss floor - navigating to boss";
                break;
            case NavigationState.Arrived:
                BossNavigationResolved = true;
                Status = "Boss floor - at boss";
                Service.Log.Info("[FloorPhase] Boss navigation complete -> arrived");
                break;
            case NavigationState.StuckRepathing:
                Status = $"Boss floor - repathing to boss ({_navHelper.StuckRetryCount}/3)";
                break;
            case NavigationState.StuckGiveUp:
                BossNavigationResolved = true;
                Status = "Boss floor - boss navigation failed";
                Service.Log.Warning("[FloorPhase] Boss navigation gave up");
                break;
            case NavigationState.Failed:
                BossNavigationResolved = true;
                Status = "Boss floor - boss navigation unavailable";
                Service.Log.Warning("[FloorPhase] Boss navigation failed to start");
                break;
        }
    }

    private static bool IsWithinBossEngageRadius(Vector3 playerPosition, Vector3 bossPosition)
    {
        var dx = playerPosition.X - bossPosition.X;
        var dz = playerPosition.Z - bossPosition.Z;
        return dx * dx + dz * dz <=
               BossNavigationArrivalTolerance * BossNavigationArrivalTolerance;
    }

    private bool CombatBuffUsable(uint slot, bool overcapRelief = false) =>
        _items.IsPomanderAvailableForFloorUse(slot) && ItemPolicy.AllowsCombatBuff(
            _pomanderManager.GetCount(slot), _ctx?.Duty.IsBossFloor == true, overcapRelief);

    private unsafe bool TryRebuffAfterRevival(InstanceContentDeepDungeon* dd)
    {
        if (!_ctx.HohContinuity.BossRebuffPending)
        { _revivalRebuffStarted = null; _revivalUnavailableReported.Clear(); return false; }
        _navHelper.Cancel(); BossNavigationResolved = false;
        var now = DateTime.UtcNow;
        _revivalRebuffStarted ??= now;
        HohRebuffItemState State(uint slot, uint status)
        {
            var state = HohRevivalBuffPolicy.ObserveItem(LocalStatusRemaining(status) > 0,
                _items.Session.Pending?.Key == new FloorItemUseKey(FloorItemUseKind.Pomander, slot) ||
                _items.RecentlyConfirmed(FloorItemUseKind.Pomander, slot), _pomanderManager.GetCount(slot),
                DeepDungeonFloorItemUsePolicy.CanUsePomanders(dd->DeepDungeonBanId) && _items.AllowsHohBossHp(slot), _items.IsPomanderBlockedForFloor(slot),
                _items.CanAttemptPomanderUse(allowCombat: true), CombatBuffUsable(slot));
            if (state is HohRebuffItemState.NoStock or HohRebuffItemState.Forbidden &&
                _revivalUnavailableReported.Add(slot))
                _recordReplayEvent("hoh-revival-buff-unavailable", new { floor = dd->Floor, slot, reason = state.ToString() });
            return state;
        }
        var steel = State(FloorInitPlanner.SteelPomanderSlotIndex, SteelStatusId);
        var strength = State(FloorInitPlanner.StrengthPomanderSlotIndex, StrengthStatusId);
        var next = HohRevivalBuffPolicy.Next(steel, strength);
        if (next == HohRebuffStep.Complete)
        {
            _ctx.HohContinuity.CompleteBossRebuff();
            _recordReplayEvent("hoh-revival-rebuff-complete", new { floor = dd->Floor, steel = steel.ToString(), strength = strength.ToString(),
                _ctx.HohContinuity.ResumeSequence });
            Status = "天宫：重生后Buff检查完成，恢复Boss接战";
            return true;
        }
        Status = $"天宫：重生后先补Buff（防御：{steel}，自身：{strength}），暂缓接战";
        if (now - _revivalRebuffStarted > TimeSpan.FromSeconds(20))
        {
            _ctx.StopExplorationRecoverably("天宫：重生后补Buff超过20秒仍未确认，已保留现场并停止自动接战。");
            return true;
        }
        if (next is HohRebuffStep.Steel or HohRebuffStep.Strength)
            _items.TryUsePomander(next == HohRebuffStep.Steel ? FloorInitPlanner.SteelPomanderSlotIndex : FloorInitPlanner.StrengthPomanderSlotIndex,
                dd, "HOH revival rebuff before engage");
        return true;
    }

    private static float LocalStatusRemaining(uint statusId, bool zeroMeansPermanent = false)
    {
        var player = Service.LocalPlayer;
        if (player != null)
            foreach (var status in player.StatusList)
                if (status.StatusId == statusId)
                    return zeroMeansPermanent && status.RemainingTime == 0 ? float.PositiveInfinity : Math.Abs(status.RemainingTime);
        return 0;
    }

    private unsafe void TryMaintainBossBuffs(InstanceContentDeepDungeon* dd)
    {
        if (_ctx?.ControlledPtSurvey != null || !_items.CanAttemptPomanderUse(allowCombat: true)) return;
        if (dd->DeepDungeonId == 4 && dd->Floor == 99 && EndgameItemPolicy.IsProgression(_ctx?.FarmingPlan?.Mode))
        {
            TryMaintainPt99Items(dd);
            return;
        }
        // Defence first. Refresh shortly before expiry rather than repeatedly overwriting active buffs.
        if (CombatBuffUsable(FloorInitPlanner.SteelPomanderSlotIndex) &&
            (dd->DeepDungeonId != 2 || !_items.RecentlyConfirmed(FloorItemUseKind.Pomander, FloorInitPlanner.SteelPomanderSlotIndex)) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(SteelStatusId)) &&
            _items.TryUsePomander(FloorInitPlanner.SteelPomanderSlotIndex, dd, "boss steel refresh")) return;
        if (CombatBuffUsable(FloorInitPlanner.StrengthPomanderSlotIndex) &&
            (dd->DeepDungeonId != 2 || !_items.RecentlyConfirmed(FloorItemUseKind.Pomander, FloorInitPlanner.StrengthPomanderSlotIndex)) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(StrengthStatusId)))
            if (_items.TryUsePomander(FloorInitPlanner.StrengthPomanderSlotIndex, dd, "boss strength refresh")) return;
        if (dd->DeepDungeonId == 4 && CombatBuffUsable(FloorInitPlanner.HastePomanderSlotIndex) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(PtHasteStatusId)))
            if (_items.TryUsePomander(FloorInitPlanner.HastePomanderSlotIndex, dd, "boss haste refresh")) return;
        // HoH's third boss item replaces PT haste in this flow. Native active state,
        // the shared confirmed request channel and HoH's duration gate prevent repeated consumption.
        if (dd->DeepDungeonId == 2 && _items.IsPomanderAvailableForFloorUse(HohItemPolicy.Frailty) &&
            !_pomanderManager.IsActive(HohItemPolicy.Frailty) && !_items.RecentlyConfirmed(FloorItemUseKind.Pomander, HohItemPolicy.Frailty))
            if (_items.TryUsePomander(HohItemPolicy.Frailty, dd, "HOH boss frailty refresh", FloorItemUsePurpose.HohEmergency)) return;
        if (dd->DeepDungeonId == 4)
        {
            // Demiclone row 5/native stone 3, not the floor-effect removal pomander.
            float remaining = LocalStatusRemaining(4584, zeroMeansPermanent: true); // Tranquil Moonlight: maximum HP.
            if (!FarmingItemPolicy.NeedsBossRefresh(remaining)) BossSerenityAwaitingBuff = false;
            if (ItemPolicy.AllowsBossSerenity(_items.GetStoneCountAvailableForFloorUse(3), _ctx?.Duty.IsBossFloor == true,
                remaining, BossSerenityAwaitingBuff))
                _items.TryDispatchFloorStone(3, dd, "boss serenity incense", FloorItemUsePurpose.BossSerenity);
        }
    }

    private unsafe void TryMaintainPt99Items(InstanceContentDeepDungeon* dd)
    {
        bool pomandersAllowed = DeepDungeonFloorItemUsePolicy.CanUsePomanders(dd->DeepDungeonBanId);
        bool Needs(uint slot, uint status) => pomandersAllowed && CombatBuffUsable(slot) &&
            !_items.RecentlyConfirmed(FloorItemUseKind.Pomander, slot) &&
            FarmingItemPolicy.NeedsBossRefresh(LocalStatusRemaining(status));
        float serenityRemaining = LocalStatusRemaining(4584, zeroMeansPermanent: true);
        if (!FarmingItemPolicy.NeedsBossRefresh(serenityRemaining)) BossSerenityAwaitingBuff = false;
        bool incenseAllowed = DeepDungeonFloorItemUsePolicy.CanUsePtIncense(dd->DeepDungeonBanId);
        var bosses = Service.GameObjects.OfType<IBattleChara>().Where(b =>
            b.BaseId is Pt99TargetPolicy.GriefBaseId or Pt99TargetPolicy.EaterBaseId &&
            b.IsTargetable && !b.IsDead && b.CurrentHp > 1).Select(b => b.BaseId).Distinct().Count();
        var step = EndgameItemPolicy.NextBossStep(
            Needs(FloorInitPlanner.StrengthPomanderSlotIndex, StrengthStatusId),
            Needs(FloorInitPlanner.SteelPomanderSlotIndex, SteelStatusId),
            Needs(FloorInitPlanner.HastePomanderSlotIndex, PtHasteStatusId),
            incenseAllowed && _items.GetStoneCountAvailableForFloorUse(1) > 0, _bossFateConfirmed,
            bosses == 2 && Service.Condition[ConditionFlag.InCombat],
            incenseAllowed && ItemPolicy.AllowsBossSerenity(_items.GetStoneCountAvailableForFloorUse(3), true,
                serenityRemaining, BossSerenityAwaitingBuff));
        switch (step)
        {
            case Pt99ItemStep.Strength:
                _items.TryUsePomander(FloorInitPlanner.StrengthPomanderSlotIndex, dd, "99 ordered strength"); break;
            case Pt99ItemStep.Steel:
                _items.TryUsePomander(FloorInitPlanner.SteelPomanderSlotIndex, dd, "99 ordered steel"); break;
            case Pt99ItemStep.Haste:
                _items.TryUsePomander(FloorInitPlanner.HastePomanderSlotIndex, dd, "99 ordered haste"); break;
            case Pt99ItemStep.Fate:
                _items.TryDispatchFloorStone(1, dd, "99 ordered fate", FloorItemUsePurpose.BossFate); break;
            case Pt99ItemStep.Serenity:
                _items.TryDispatchFloorStone(3, dd, "99 ordered serenity", FloorItemUsePurpose.BossSerenity); break;
        }
    }
}
