using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using global::Dalamud.Plugin.Services;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

internal sealed partial class FarmingScenario
{
    private AetherpoolTracker? _gripTracker;
    private AetherpoolGripExchangeFlow? _gripFlow;
    private bool _localDeleteDone, _postGripChecked;
    private ulong _gripCharacter;
    internal FarmingScenario WithGripTracker(AetherpoolTracker tracker)
    { _gripTracker = tracker; return this; }
    private void InitializeLocalGripExchange()
    { _gripCharacter = Service.PlayerState.ContentId; }
    private bool ValidateLocalGripIdentity(RunContext ctx)
    {
        if (session.Plan.GripExchange == null) return true;
        var status = AetherpoolExchangePolicy.EvaluateCharacter(_gripCharacter, Service.PlayerState.ContentId,
            Service.LocalPlayer != null, ctx.Duty.StateReadFailed || ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning);
        if (status == GripCharacterStatus.Ready) return true;
        if (status == GripCharacterStatus.Waiting)
        {
            ctx.StatusLine = "等待角色載入後繼續聚魔柄流程。";
            return false;
        }
        ctx.StatusLine = _gripCharacter == 0 ? "聚魔柄任務啟動角色未確認；請重新啟動任務。"
            : "聚魔柄任務角色已變更；停止自動化。";
        ctx.StatusIsError = true;
        return false;
    }
    private bool TryInitialGripExchange(RunContext ctx, IFramework framework)
    {
        if (session.Plan.GripExchange == null || session.InitialGripExchangeChecked) return true;
        if (ctx.Duty.StateReadFailed || ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning) return false;
        if (ctx.Duty.IsInDuty) return true;
        if (!RunGripExchange(ctx, framework)) return false;
        session.InitialGripExchangeChecked = true;
        return !ctx.StatusIsError && ValidateGripEntryReserve(ctx);
    }
    private bool ValidateGripEntryReserve(RunContext ctx)
    {
        if (AetherpoolExchangePolicy.GetDungeon(session.Plan.DungeonId) is not { } dungeon ||
            _gripTracker?.TryGet(dungeon.DungeonId, out var state) != true) return true; // Native entry still validates unknown/unlocked state.
        if (state.Weapon == null || state.Armor == null || state.Weapon >= dungeon.Reserve && state.Armor >= dungeon.Reserve) return true;
        ctx.StatusLine = $"聚魔強化不足：武器 +{state.Weapon}／防具 +{state.Armor}；本任務保留值為 {dungeon.Reserve}。";
        ctx.StatusIsError = true; return false;
    }
    private bool TryPostExitGripExchange(RunContext ctx, FarmingAttemptResult result, IFramework framework)
    {
        // A pending pause prevents a new batch. An already-started exchange owns
        // its confirmation/receipt and must finish or fail before the task pauses.
        if (!AetherpoolExchangePolicy.ShouldRunPostExitExchange(session.Plan.GripExchange != null,
                _postGripChecked, session.HasPendingFloorsetPause, _gripFlow != null, result)) return true;
        if (!RunGripExchange(ctx, framework)) return false;
        _postGripChecked = true; return !ctx.StatusIsError;
    }
    private bool RunGripExchange(RunContext ctx, IFramework framework)
    {
        var plan = session.Plan;
        if (_gripTracker == null || plan.GripExchange is not { IsValid: true } options ||
            TaskContinuationPolicy.PreserveSave(plan) || plan.CurrentDutyOnly || plan.ReusesSave ||
            !plan.RelicMaterial && (plan.DungeonId != 4 || plan.StartFloor != 21) ||
            plan.Mode != FarmingMode.Hoard ||
            plan.RelicMaterial && !RelicFarmingPolicy.IsValid(plan) ||
            AetherpoolExchangePolicy.GetDungeon(plan.DungeonId) is not { } dungeon) return true;
        if (_gripFlow == null)
        {
            if (!_gripTracker.TryGet(plan.DungeonId, out var state)) return true;
            int count = AetherpoolExchangePolicy.ComputePlannedExchanges(state.Weapon, state.Armor, options, dungeon.Reserve);
            if (count <= 0) return true;
            _gripFlow = new(dungeon, count, _gripTracker); _gripFlow.Prepare(ctx);
        }
        if (!_gripFlow.Update(framework)) return false;
        _gripFlow.Dispose(); _gripFlow = null; return true;
    }
}
