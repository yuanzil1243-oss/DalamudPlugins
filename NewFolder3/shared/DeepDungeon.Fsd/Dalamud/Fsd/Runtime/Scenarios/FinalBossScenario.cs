using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Services;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

/// <summary>One independent Final Verse normal attempt; never constructs any save-management flow.</summary>
internal sealed class FinalBossScenario(FarmingPlan plan) : IScenario
{
    private RunContext? _context;
    private FinalBossEntryFlow? _entry;
    private PilgrimsTraverseRestExitFlow? _rest;
    private DateTime _nextSnapshot, _nextLeave, _completedAt;
    private DateTime _missingBossSince;
    public string Name => "PT 尾王模式（独立练习，无存档）";
    public bool IsComplete { get; private set; }
    public bool ShouldLoop => CountsAsCycle;
    public bool CountsAsCycle { get; private set; }
    public bool RequiresDutyCompletionEvent => true;
    internal bool EntryAccepted { get; private set; }

    public void Initialize(RunContext context)
    {
        _context = context;
        if (plan.Mode != FarmingMode.FinalBoss || plan.SaveUse != SaveUse.None || plan.StartFloor != 99)
        {
            context.StatusLine = "尾王免存檔流程僅允許無存檔的獨立99層練習；已停止。";
            context.StatusIsError = true;
            IsComplete = true;
            return;
        }
        context.ResetAttemptState();
        context.FarmingPlan = plan;
        context.RunOptions.Set(new RunOptions { OpenGold = false, OpenSilver = false, OpenBronze = false,
            BandedEnabled = false, RequireValidatedAbandonPrompt = true });
        context.RecordDiagnostic?.Invoke("final-boss-practice-start", new { territory = FinalBossPracticePolicy.TerritoryId,
            contentFinder = FinalBossPracticePolicy.ContentFinderId, saveUse = "None", plan.TargetCycles, plan.Infinite });
        _entry = new FinalBossEntryFlow();
        _entry.Prepare(context);
    }

    public unsafe void Update(IFramework framework)
    {
        var ctx = _context!;
        if (ctx.StatusIsError) { IsComplete = true; return; }
        if (IsComplete) return;
        var now = DateTime.UtcNow;
        bool inside = FinalBossPracticePolicy.IsPractice(Service.ClientState.TerritoryType);
        if (!EntryAccepted)
        {
            if (!_entry!.Update(framework)) return;
            EntryAccepted = true;
            DeepDungeonUi.CloseDeepDungeonEntryWindows();
            ctx.RecordDiagnostic?.Invoke("final-boss-practice-entered", new { territory = Service.ClientState.TerritoryType, floor = 99 });
        }
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        { ctx.CombatAssist.Reset(ctx); return; }
        if (!inside)
        {
            ctx.CombatAssist.Reset(ctx);
            if (!ctx.DutyCompletionObserved || ctx.DutyFailureObserved)
            { Fail("尾王练习退出但没有有效通关事件，不计完成次数。"); return; }
            _rest ??= new PilgrimsTraverseRestExitFlow(requireValidatedConfirmation: true);
            if (!_rest.IsPrepared) _rest.Prepare(ctx);
            if (!_rest.Update(framework)) return;
            if (ctx.StatusIsError) return;
            CountsAsCycle = true;
            IsComplete = true;
            ctx.StatusLine = "尾王练习通关并退出，完成1次；未操作任何存档。";
            ctx.RecordDiagnostic?.Invoke("final-boss-practice-complete", new { dutyCompleted = true, exited = true, saveUse = "None" });
            return;
        }
        if (ctx.Duty.IsTransitioning) { ctx.CombatAssist.Reset(ctx); return; }
        if (ctx.DutyFailureObserved || Service.LocalPlayer?.IsDead == true)
        { Fail("尾王测试失败，已停止并保留现场；不删除存档、不计次。"); return; }
        if (ctx.DutyCompletionObserved)
        {
            ctx.CombatAssist.Reset(ctx);
            ctx.SetBossRotationSuppressed?.Invoke(true);
            if (_completedAt == default) _completedAt = now;
            if (now - _completedAt > TimeSpan.FromSeconds(30)) { Fail("尾王已通关但退出未完成，已停止供检查。"); return; }
            if (now < _nextLeave) return;
            _nextLeave = now.AddSeconds(1);
            if (DeepDungeonUi.TryGetSelectYesNo(out var yesno))
            {
                if (!DeepDungeonUi.IsAbandonDutyConfirmationPrompt(yesno, out var error)) { Fail(error); return; }
                DeepDungeonUi.Fire(yesno, 0);
            }
            else LeaveDutyHelper.TryRequestLeaveDuty();
            ctx.StatusLine = "尾王练习已通关，正在退出（无存档流程）。";
            return;
        }
        ctx.DeathRecorder.Tick(ctx);
        ctx.CombatAssist.Tick(ctx.Configuration, ctx, true, false, out var status, out _, out _);
        if (ctx.StatusIsError) { IsComplete = true; return; }
        if (ctx.CombatAssist.Pt99Decision.Target == Pt99TargetKind.WaitForBossState)
        {
            if (_missingBossSince == default) _missingBossSince = now;
            if (now - _missingBossSince > TimeSpan.FromSeconds(60))
            { Fail("尾王练习持续60秒未取得完整Boss状态，已停止；请检查场地与目标记录。"); return; }
        }
        else _missingBossSince = default;
        ctx.StatusLine = status;
        if (now >= _nextSnapshot)
        {
            _nextSnapshot = now.AddSeconds(1);
            var player = Service.LocalPlayer;
            ctx.RecordDiagnostic?.Invoke("final-boss-combat-snapshot", new { territory = Service.ClientState.TerritoryType,
                logicalFloor = 99, practice = true, status, playerHp = player?.CurrentHp,
                playerPosition = player == null ? null : new { player.Position.X, player.Position.Y, player.Position.Z },
                statuses = player?.StatusList.Select(s => new { s.StatusId, s.Param, s.RemainingTime }).ToArray(),
                enemies = Service.GameObjects.OfType<IBattleNpc>().Where(e => e.BaseId is Pt99TargetPolicy.GriefBaseId or Pt99TargetPolicy.EaterBaseId or Pt99TargetPolicy.MinionBaseId)
                    .Select(e => new { e.GameObjectId, e.BaseId, e.CurrentHp, e.MaxHp, e.IsCasting, e.CastActionId,
                        statuses = e.StatusList.Select(s => new { s.StatusId, s.Param, s.RemainingTime }).ToArray() }).ToArray() });
        }
    }

    private void Fail(string reason)
    {
        _context!.CombatAssist.Reset(_context);
        _context.SetBossRotationSuppressed?.Invoke(true);
        _context.StatusLine = reason; _context.StatusIsError = true;
        _context.RecordDiagnostic?.Invoke("final-boss-practice-failed", new { reason, saveUse = "None" });
    }

    public void Dispose()
    {
        _context?.CombatAssist.Reset(_context);
        _entry?.Reset(); _rest?.Dispose();
        _context = null;
    }
}
