using DeepDungeon.Fsd.Runtime;
using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

public sealed partial class RunHost
{
    // Called before companion controls and before the host/scenario can consume a missing-duty frame.
    internal void ObserveHohContinuity()
    {
        var ctx = _context;
        if (!_isFsdMode || ctx == null || FarmingPaused ||
            ctx.FarmingPlan?.Mode != Core.FarmingMode.FreeExploration ||
            ctx.Duty.DungeonId != 2 && !ctx.HohContinuity.HasIdentity) return;
        var continuity = ctx.HohContinuity;
        var before = continuity.Phase;
        long sequence = continuity.ResumeSequence;
        var player = Service.LocalPlayer;
        bool stable = !ctx.Duty.StateReadFailed && !ctx.Duty.IsTransitioning &&
            !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51];
        // A disconnected client may also set IsTransitioning; login loss is checked independently.
        continuity.Observe(new(Service.ClientState.IsLoggedIn, player != null, stable,
            ctx.Duty.IsInDuty, ctx.Duty.DungeonId, ctx.Duty.Floor, Service.ClientState.TerritoryType,
            player?.IsDead == true, ctx.DutyFailureObserved || ctx.DutyCompletionObserved), DateTime.UtcNow);
        if (before == continuity.Phase && sequence == continuity.ResumeSequence) return;
        if (continuity.HoldsControls)
        {
            _floorController.CancelActiveMovement(); ctx.Navigator.CancelAll();
            ctx.CombatAssist.Reset(ctx);
            if (player is { IsDead: false }) OmenTools.Interop.Game.ExecuteCommand.Implementations.AutoAttackCommand.Disable();
        }
        _floorController.RecordReplayEvent("hoh-run-continuity", new {
            before = before.ToString(), phase = continuity.Phase.ToString(), floor = ctx.Duty.Floor,
            continuity.ResumeSequence, continuity.BossRebuffPending, loggedIn = Service.ClientState.IsLoggedIn,
            playerAvailable = player != null, dead = player?.IsDead, territory = Service.ClientState.TerritoryType });
        if (continuity.Phase == HohContinuityPhase.Mismatch)
        {
            ctx.StopExplorationRecoverably("天宫：重连后的副本或层组与原任务不一致，已停止自动操作，请核对当前任务。");
            return;
        }
        if (continuity.Phase == HohContinuityPhase.Exited && !ctx.DutyCompletionObserved && !ctx.DutyFailureObserved)
            ctx.MarkDutyFailed("hoh-disconnect-exited-duty");
        if (ctx.StatusIsError) return;
        ctx.StatusLine = _lastStatus = continuity.Phase switch {
            HohContinuityPhase.Disconnected => "天宫：连接中断，保留当前任务，等待原副本恢复。",
            HohContinuityPhase.WaitingRevival => "天宫：已观察到死亡，限时等待重生，暂停移动和接战。",
            HohContinuityPhase.ConfirmedDeath => "天宫：重生等待结束仍死亡，执行原有失败退本和存档清理重试。",
            HohContinuityPhase.Exited => ctx.DutyCompletionObserved ? "天宫：已收到通关结束证据，执行原有收尾。" :
                "天宫：副本已结束，按原失败证据核对存档并重试。",
            _ => continuity.BossRebuffPending ? "天宫：已恢复角色，先补Boss强化自身和强化防御。" : "天宫：原副本已恢复，重新同步AI和输出后继续任务。"
        };
    }
}
