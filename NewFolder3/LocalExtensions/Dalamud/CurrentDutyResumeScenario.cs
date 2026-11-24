using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

/// <summary>Attaches only the current duty to the unmodified floor/exit controllers.</summary>
internal sealed class CurrentDutyResumeScenario(FarmingPlan plan, int groupStart) : IScenario
{
    private RunContext? _context;
    private DungeonExitController? _exit;
    private bool _complete;
    internal bool PauseAfterGroup { get; set; }
    public string Name => $"繼續探索（第 {groupStart}–{groupStart + 9} 層）";
    public bool IsComplete => _complete;
    public bool ShouldLoop => false;
    public bool CountsAsCycle => false;
    public bool RequiresDutyCompletionEvent => true;

    internal bool TrySetChestTargets(RunContext? expectedContext, bool gold, bool silver, bool bronze)
    {
        var context = _context;
        if (context == null || !ReferenceEquals(context, expectedContext) || _complete ||
            context.StatusIsError || context.AttemptAborted || context.DutyFailureObserved ||
            !CurrentDutyChestPolicy.TryApply(context.FarmingPlan, context.RunOptions, new(false, gold, silver, bronze)))
            return false;
        context.FarmingPlan = context.FarmingPlan! with { OpenGold = gold, OpenSilver = silver, OpenBronze = bronze };
        return true;
    }

    internal static ContinueExplorationFacts ReadFacts(DutyState? duty, bool running)
    {
        bool territoryKnown = DungeonCatalog.TryGetByTerritoryId(Service.ClientState.TerritoryType, out var dungeon);
        (int start, int end) = territoryKnown && dungeon.TerritoryFloorRanges.TryGetValue(Service.ClientState.TerritoryType, out var range)
            ? range : (0, 0);
        bool bound = Service.Condition[ConditionFlag.BoundByDuty] || Service.Condition[ConditionFlag.BoundByDuty56] ||
            Service.Condition[ConditionFlag.BoundByDuty95];
        return new(territoryKnown, running, bound && duty?.IsInDuty == true,
            duty != null && !duty.StateReadFailed,
            DutyState.IsClientTransitioning || duty?.IsTransitioning == true ||
                Service.Condition[ConditionFlag.WatchingCutscene] || Service.Condition[ConditionFlag.OccupiedInCutSceneEvent],
            Service.LocalPlayer is { IsDead: false }, Service.PlayerState.ContentId,
            Service.LocalPlayer?.ClassJob.RowId ?? 0, duty?.DungeonId ?? 0,
            territoryKnown ? dungeon.DutyDungeonId : 0, duty?.Floor ?? 0, start, end,
            duty?.CurrentFloorKind is not (null or DeepDungeonFloorKind.Unknown));
    }

    public void Initialize(RunContext context)
    {
        _context = context;
        context.ResetAttemptState();
        context.FarmingPlan = plan;
        context.RunOptions.Set(new RunOptions
        {
            OpenGold = plan.OpenGold, OpenSilver = plan.OpenSilver, OpenBronze = plan.OpenBronze,
            BandedEnabled = false, LeaveMode = LeaveMode.AfterFinishDungeon,
            RequireValidatedAbandonPrompt = true
        });
        context.TerminalRewardsRequired = groupStart == 91 && ResultAltarPolicy.TryGet(plan.DungeonId, 100, out _) ||
            groupStart == 191 && Potd200RewardPolicy.IsTerminal(plan.DungeonId, 200);
        context.EntryReadyForDuty = false;
        context.StatusLine = "接續目前深宮的探索。";
    }

    public void Update(IFramework framework)
    {
        var ctx = _context;
        if (ctx == null || _complete) return;
        if (ctx.StatusIsError) { ctx.EntryReadyForDuty = false; _complete = true; return; }
        if (DutyState.IsClientTransitioning || ctx.Duty.IsTransitioning || ctx.Duty.StateReadFailed)
        { ctx.EntryReadyForDuty = false; return; }
        if (ctx.AttemptAborted || ctx.DutyFailureObserved)
        { Fail(ctx, "本次探索已中止或攻略失敗；停止並保留存檔。"); return; }
        if (ctx.Duty.IsInDuty)
        {
            var facts = ReadFacts(ctx.Duty, false);
            // Cutscenes, raising and floor stabilization pause the existing controllers.
            if (facts.Transitioning || !facts.PlayerAlive || !facts.FloorClassified)
            { ctx.EntryReadyForDuty = false; return; }
            if (!ContinueExplorationPolicy.TryAttach(facts, out var currentGroup, out var error) ||
                currentGroup != groupStart || facts.DungeonId != plan.DungeonId ||
                !ExternalSavePolicy.MatchesCharacter(plan, facts.Character, facts.Job))
            { Fail(ctx, error.Length > 0 ? error : "地宮、層段、角色或職業已變更，停止接續。"); return; }
            ctx.EntryReadyForDuty = true;
            // Completion may predate attaching FSD (e.g. reconnect after the boss).
            // Same evidence as upstream FarmingScenario.ObserveDutyCompletion.
            if (!ctx.DutyCompletionObserved && ctx.Duty.IsBossFloor && ctx.Duty.PassageOpen &&
                !Service.DutyState.IsDutyStarted && !Service.Condition[ConditionFlag.InCombat])
                ctx.MarkDutyCompleted();
            return;
        }
        ctx.EntryReadyForDuty = false;
        if (!ctx.DutyCompletionObserved)
        { Fail(ctx, "未觀測到本組通關；停止並保留存檔。"); return; }
        if (ctx.TerminalRewardsRequired)
        { Fail(ctx, "終局獎勵尚未完成驗收；停止並保留現場，請檢查原版獎勵紀錄。"); return; }
        if (!DungeonCatalog.TryGetByDungeonId(plan.DungeonId, out var dungeon))
        { Fail(ctx, "無法辨認地宮入口，停止接續。"); return; }
        _exit ??= new(returnToEntrance: true, entranceNpcBaseId: dungeon.NpcDataId);
        if (!_exit.IsPrepared) _exit.Prepare(ctx);
        if (_exit.Update() != ExitOutcome.Completed) return;
        ctx.StatusLine = "本組探索完成，已正常出本並保留存檔。";
        _complete = true;
    }

    private void Fail(RunContext ctx, string reason)
    {
        ctx.EntryReadyForDuty = false;
        ctx.Navigator.CancelAll();
        ctx.StatusLine = reason;
        ctx.StatusIsError = true;
        _complete = true;
    }

    public void Dispose() { _exit?.Dispose(); _context = null; }
}
