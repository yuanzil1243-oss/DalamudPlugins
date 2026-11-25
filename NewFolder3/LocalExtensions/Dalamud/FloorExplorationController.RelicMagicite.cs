using System.Runtime.CompilerServices;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using global::Dalamud.Game.ClientState.Conditions;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    // One context spans the group's ordinary floors and boss floor. Rebuilding
    // a floor runtime must not reset the interval after an overflow use.
    private static readonly ConditionalWeakTable<RunContext, HohRelicMagiciteCooldown> RelicMagiciteCooldowns = new();
    private HohRelicMagiciteCooldown RelicMagiciteCooldown => RelicMagiciteCooldowns.GetValue(_ctx, static _ => new());

    private unsafe bool UsesLocalRelicMagicite(InstanceContentDeepDungeon* dd) =>
        dd != null && dd->DeepDungeonId == 2 && HohRelicMagicitePolicy.AppliesTo(_ctx.FarmingPlan);

    private unsafe bool CanRequestLocalRelicMagicite(InstanceContentDeepDungeon* dd, byte stone)
    {
        if (!UsesLocalRelicMagicite(dd) || stone is < 1 or > 4) return true;
        return !ItemUse.DispatchedThisUpdate && RelicMagiciteCooldown.CanRequest(DateTime.UtcNow);
    }

    private unsafe void RecordLocalRelicMagiciteRequest(InstanceContentDeepDungeon* dd, byte stone)
    {
        if (!UsesLocalRelicMagicite(dd) || stone is < 1 or > 4) return;
        RelicMagiciteCooldown.ObserveRequestOrConfirmation(DateTime.UtcNow);
        RecordReplayEvent("local-relic-magicite-requested", new
        { floor = Floor, stone, nextRequestAtUtc = RelicMagiciteCooldown.NextRequestAtUtc });
    }

    private void ObserveLocalRelicMagiciteConfirmation(byte stone)
    {
        if (DungeonId != 2 || stone is < 1 or > 4 || !HohRelicMagicitePolicy.AppliesTo(_ctx.FarmingPlan)) return;
        RelicMagiciteCooldown.ObserveRequestOrConfirmation(DateTime.UtcNow);
    }

    private unsafe void UpdateLocalRelicBossMagicite(InstanceContentDeepDungeon* dd)
    {
        if (!UsesLocalRelicMagicite(dd) || Kind != FloorRuntimeKind.Boss || _phase != FloorPhase.BossFloor ||
            dd->Floor != Floor || Floor != 30 || _ctx.Duty.DungeonId != 2 || _ctx.Duty.Floor != Floor ||
            _ctx.StatusIsError || !_ctx.EntryReadyForDuty || _ctx.DutyCompletionObserved ||
            _ctx.DutyFailureObserved || _ctx.AttemptAborted || _ctx.CancellationSource.IsCancellationRequested ||
            _ctx.Duty.StateReadFailed || _ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning ||
            Service.LocalPlayer is not { IsDead: false } || ItemUse.Pending != null ||
            !Service.Condition[ConditionFlag.InCombat] || !RelicMagiciteCooldown.CanRequest(DateTime.UtcNow)) return;

        var hostile = CombatTargetingHelpers.PickNearestHostile(60f, out _);
        bool live = hostile is { IsDead: false, IsTargetable: true, CurrentHp: > 0 };
        var snapshot = new HohRelicMagiciteSnapshot(dd->DeepDungeonId, dd->Floor, true, true, live,
            _ctx.Duty.PassageOpen, Coord.SiteSearchBoundary.ReadStoneCount(dd));
        if (!HohRelicMagicitePolicy.ShouldSpendAtBoss(_ctx.FarmingPlan, snapshot)) return;

        byte stone = FindAvailableHohMagicite(dd);
        if (stone != 0)
            TryDispatchFloorStone(stone, dd, "HoH relic 30 boss: spend remaining magicite", FloorItemUsePurpose.Automatic);
        // The normal boss update still runs. No target, movement, rotation or
        // mechanic state is replaced by this item sidecar.
    }
}
