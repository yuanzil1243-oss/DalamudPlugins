using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

/// <summary>One entry/exit attempt. Session owns continuation, prepared source and climb progress.</summary>
internal sealed class FarmingScenario(FarmingSession session, DeepDungeonEntryUiCleanup? entryUiCleanup = null,
    IDeleteSaveFlow? deleteSaveFlow = null) : IScenario
{
    private readonly DeepDungeonEntryUiCleanup _entryUiCleanup = entryUiCleanup ?? new();
    private RunContext? _context;
    private GenericEntryFlow? _create;
    private PreparedSaveFlow? _prepared;
    private PreparedSaveFlow? _verification;
    private PilgrimsTraverseRestExitFlow? _rest;
    private IDeleteSaveFlow? _delete;
    private bool _ownedSaveCleanupCompleted;
    private bool _entered, _restDone, _complete, _recoverFailure, _aborted;
    private int _entryFloor;

    public string Name => $"PT {session.Plan.DisplayName} ({(session.Plan.SaveUse == SaveUse.Prepared ? session.Source.Description : $"{session.NextFloor} 層")})";
    public bool IsComplete => _complete;
    public bool ShouldLoop => !_aborted;
    public bool CountsAsCycle { get; private set; }
    public bool RecoversDutyFailure => _recoverFailure;
    internal bool EntryAccepted => _entered && _context?.StatusIsError != true;
    public bool RequiresDutyCompletionEvent => !session.Plan.ReusesSave && !_recoverFailure && !_aborted;

    public void Initialize(RunContext context)
    {
        _context = context;
        if (session.Plan.Mode == FarmingMode.FinalBoss || session.Plan.SaveUse == SaveUse.None)
        {
            context.StatusLine = "無存檔尾王練習不得使用普通存檔流程；已停止。";
            context.StatusIsError = true;
            _aborted = _complete = true;
            return;
        }
        context.ResetAttemptState();
        context.FarmingPlan = session.Plan;
        var plan = session.Plan;
        context.RunOptions.Set(new RunOptions
        {
            OpenGold = session.OpenGold,
            OpenSilver = session.OpenSilver,
            OpenBronze = session.OpenBronze,
            BandedEnabled = session.BandedEnabled,
            HoardDistributionHintsOnly = plan.Mode == FarmingMode.FreeExploration,
            HarvestChestsRequired = plan.Mode == FarmingMode.Aetherpool,
            DiscoveryOnly = plan.Mode == FarmingMode.HoardDiscovery,
            LeaveMode = LeaveMode.AfterFinishDungeon,
            RequireValidatedAbandonPrompt = true
        });
        if (session.ResumeCurrentDuty)
        {
            _entryFloor = session.NextFloor;
            _entered = true;
            context.TerminalRewardsRequired = _entryFloor == 91;
            context.PreparedSaveDescription = $"原副本接續，存檔 {session.OwnedSlot + 1}，起點 {_entryFloor} 層";
            context.StatusLine = $"原副本接續：第 {context.Duty.Floor} 層。";
            Service.Log.Info($"[Farming] Resumed existing duty: floor={context.Duty.Floor}, entryFloor={_entryFloor}, ownedSlot={session.OwnedSlot}");
        }
        else if (plan.SaveUse == SaveUse.Prepared || session.OwnedSlot >= 0)
        {
            _prepared = new PreparedSaveFlow(session.PrepareSource());
            _prepared.Prepare(context);
        }
        else
        {
            _create = new GenericEntryFlow(DungeonCatalog.PilgrimsTraverse, session.NextFloor, 0);
            _create.Prepare(context);
        }
    }

    public void Update(IFramework framework)
    {
        var ctx = _context;
        if (ctx == null || _complete || session.IsPaused) return;
        if (ctx.StatusIsError) { _complete = true; return; }
        if (ctx.TerminalReviewRequested)
        {
            _aborted = true;
            _complete = true;
            return;
        }
        // The previous floorset can still be zoning when the host creates this attempt.
        // Do not interact with the overworld entry UI during that handoff.
        if (ctx.Duty.IsTransitioning) return;
        if (!_entered)
        {
            if (!ctx.Duty.IsInDuty)
            {
                if (_prepared != null) _prepared.Update(framework);
                else _create?.Update(framework);
                return;
            }
            if (ctx.Duty.IsTransitioning || ctx.Duty.Floor == 0) return;
            // Clean up before validation: even a wrong-floor entry must not leave the save panel open.
            var cleanup = _entryUiCleanup.Update(out string cleanupStatus, out bool closeRequested);
            if (cleanup != EntryUiCleanupResult.Ready)
            {
                ctx.StatusLine = cleanupStatus;
                if (closeRequested || cleanup == EntryUiCleanupResult.Failed)
                    ctx.RecordDiagnostic?.Invoke("entry-ui-cleanup", new { result = cleanup.ToString(), closeRequested,
                        expectedFloor = session.NextFloor, actualFloor = ctx.Duty.Floor, ctx.Duty.DungeonId });
                if (cleanup == EntryUiCleanupResult.Failed) ctx.StatusIsError = true;
                return;
            }
            _entryFloor = _prepared?.SourceFloor ?? session.NextFloor;
            if (session.Plan.StopAfterFloor is { } stopAfter && stopAfter < _entryFloor + 9)
            {
                ctx.StatusLine = $"所选存档从第 {_entryFloor} 层开始，无法在第 {stopAfter} 层停止；已停止自动化，请检查存档与结束层。";
                ctx.RecordDiagnostic?.Invoke("resume-save-stop-boundary-invalid", new
                {
                    saveSlot = _prepared?.SourceSlot ?? -1,
                    sourceFloor = _entryFloor,
                    stopAfterFloor = stopAfter
                });
                ctx.StatusIsError = true;
                return;
            }
            ctx.TerminalRewardsRequired = _entryFloor == 91;
            _entered = true;
            if (ctx.Duty.DungeonId != 4 || ctx.Duty.Floor != _entryFloor)
            {
                ctx.StatusLine = $"入場地宮／樓層與來源不符：預期第 {_entryFloor} 聖路，實際第 {ctx.Duty.Floor} 聖路（地宮 {ctx.Duty.DungeonId}）；請手動退本並檢查存檔。";
                ctx.RecordDiagnostic?.Invoke("entry-floor-mismatch", new { expectedFloor = _entryFloor,
                    actualFloor = ctx.Duty.Floor, ctx.Duty.DungeonId, ownedSlot = session.OwnedSlot });
                ctx.StatusIsError = true;
                return;
            }
            if (_create != null)
            {
                if (!session.BindCreatedSave(ctx.SaveSlots.LastUsedSlotIndex))
                {
                    ctx.StatusLine = "入場來源未由本任務建立；停止自動化，請手動退本。";
                    ctx.StatusIsError = true;
                    return;
                }
            }
        }
        if (ctx.Duty.IsInDuty)
        {
            // Entry parents may become visible only after the first ready frame.
            // Keep watching while inside; these parents have no in-duty purpose.
            var lateCleanup = _entryUiCleanup.Update(out var lateStatus, out bool lateClose);
            if (lateCleanup != EntryUiCleanupResult.Ready)
            {
                ctx.StatusLine = lateStatus;
                if (lateClose || lateCleanup == EntryUiCleanupResult.Failed)
                    ctx.RecordDiagnostic?.Invoke("entry-ui-late-cleanup", new { result = lateCleanup.ToString(),
                        closeRequested = lateClose, actualFloor = ctx.Duty.Floor, ctx.Duty.DungeonId });
                if (lateCleanup == EntryUiCleanupResult.Failed) ctx.StatusIsError = true;
                return;
            }
            if (ctx.AttemptAborted)
            {
                _aborted = true;
                RequestLeave();
                return;
            }
            if (ctx.DutyFailureObserved)
            {
                if (session.Plan.IsProgression)
                {
                    _recoverFailure = true;
                    RequestLeave();
                }
                return;
            }
            if (session.Plan.ReusesSave && (ctx.Duty.IsBossFloor || ctx.Duty.Floor >= 30 || ctx.HarvestComplete))
            {
                if (!ctx.HarvestComplete)
                {
                    _aborted = true;
                    ctx.AttemptAborted = true;
                    ctx.AttemptAbortReason = "已到存檔保護邊界；停止速刷，不計完成輪數。";
                }
                else ctx.HarvestComplete = true;
                RequestLeave();
            }
            return;
        }

        if (ctx.TerminalRewardsRequired && ctx.DutyCompletionObserved && !ctx.DutyFailureObserved)
        {
            ctx.StatusLine = ctx.TerminalRoomObserved
                ? "最終獎勵房尚未完成驗收；FSD 已停止，請檢查100層獎勵紀錄。"
                : "尚未觀測到最終獎勵房；FSD 已停止並保留現場，請檢查100層物件與獎勵。";
            ctx.StatusIsError = true;
            _complete = true;
            return;
        }
        _rest ??= new PilgrimsTraverseRestExitFlow(requireValidatedConfirmation: true);
        if (!_restDone)
        {
            if (!_rest.IsPrepared) _rest.Prepare(ctx);
            if (!_rest.Update(framework)) return;
            _restDone = true;
        }
        _aborted |= ctx.AttemptAborted;
        var result = new FarmingAttemptResult(
            _entryFloor, ctx.DutyCompletionObserved, ctx.DutyFailureObserved, ctx.HarvestComplete, _aborted);
        if (FarmingSessionPolicy.ReachedStopBoundary(session.Plan, result))
        {
            _aborted = true; // Stop the host before it can construct the next entry attempt.
            _complete = true;
            ctx.StatusLine = $"已通過 {session.Plan.StopAfterFloor} 層；停止並保留存檔，等待驗收。";
            Service.Log.Info($"[Farming] Progression stop boundary reached: cleared={_entryFloor + 9}, next={_entryFloor + 10}, ownedSlot={session.OwnedSlot}");
            return;
        }
        var decision = FarmingSessionPolicy.Decide(session.Plan, result);
        if (session.Plan.ReusesSave)
        {
            _verification ??= new PreparedSaveFlow(session.Source, verifyOnly: true);
            if (!_verificationPrepared) { _verification.Prepare(ctx); _verificationPrepared = true; }
            if (!_verification.Update(framework)) return;
            if (ctx.StatusIsError) { _complete = true; return; }
        }
        if (decision.DeleteOwnedSave && !_ownedSaveCleanupCompleted)
        {
            if (session.OwnedSlot < 0)
            {
                ctx.StatusLine = "無法識別本任務建立的存檔；停止清理。";
                ctx.StatusIsError = true;
                _complete = true;
                return;
            }
            if (_delete == null)
            {
                _delete = deleteSaveFlow ?? new GenericDeleteSaveFlow(DungeonCatalog.PilgrimsTraverse,
                    session.OwnedSlot, slots => ValidateOwnedSave(slots));
                _delete.Prepare(ctx);
                ctx.RecordDiagnostic?.Invoke("owned-save-cleanup-started", new
                { mode = session.Plan.Mode.ToString(), ownedSlot = session.OwnedSlot, entryFloor = _entryFloor });
            }
            if (!_delete.Update(framework)) return;
            if (ctx.StatusIsError) { _complete = true; return; }
            int deletedSlot = session.OwnedSlot;
            session.ConfirmOwnedSaveDeleted();
            _ownedSaveCleanupCompleted = true;
            ctx.RecordDiagnostic?.Invoke("owned-save-cleanup-completed", new
            { mode = session.Plan.Mode.ToString(), deletedSlot, entryFloor = _entryFloor, verifiedEmpty = true });
        }
        // A pause may defer counters/re-entry, never required cleanup. Intermediate
        // progression keeps its save; a completed run resumes without deleting twice.
        if (session.TryPauseAfterFloorsetClear(result, decision, _ownedSaveCleanupCompleted))
        {
            ctx.Navigator.CancelAll();
            ctx.StatusLine = $"已通關 {_entryFloor}–{_entryFloor + 9} 層，" +
                (_ownedSaveCleanupCompleted ? "本輪存檔已驗證刪除，" : "接續存檔已保留，") +
                "已在副本外暫停；點擊「繼續」接續原任務。";
            try { ctx.RecordDiagnostic?.Invoke("fsd-paused-after-floorset", new
            {
                entryFloor = _entryFloor, clearedFloor = session.PausedAfterFloor,
                nextFloor = decision.NextFloor, ownedSlot = session.OwnedSlot,
                deleteRequired = decision.DeleteOwnedSave, saveCleanupCompleted = _ownedSaveCleanupCompleted
            }); } catch { }
            Service.Log.Info($"[Farming] Paused after cleanup: cleared={session.PausedAfterFloor}, next={decision.NextFloor}, ownedSlot={session.OwnedSlot}");
            return;
        }
        CountsAsCycle = decision.CountCycle;
        _recoverFailure = decision.RecoverFailure;
        session.CompleteAttempt(decision, ctx.HoardDiscoveries);
        if (decision.Error.Length > 0)
        {
            ctx.StatusLine = ctx.AttemptAbortReason + decision.Error + " FSD 已停止。";
            ctx.StatusIsError = true;
        }
        else
            ctx.StatusLine = decision.RecoverFailure ? $"攻略失敗 {session.Failures} 次；從第 {session.NextFloor} 層重開。"
                : decision.CountCycle ? "本輪完成；存檔收尾已驗證。"
                : $"本組通關；將接續 {session.NextFloor} 層。";
        Service.Log.Info($"[Farming] mode={session.Plan.Mode}, entry={_entryFloor}, clear={ctx.DutyCompletionObserved}, failure={ctx.DutyFailureObserved}, harvest={ctx.HarvestComplete}, discoveries={ctx.HoardDiscoveries}, aborted={_aborted}, reason={ctx.AttemptAbortReason}, cycle={CountsAsCycle}, next={session.NextFloor}, deleteOwned={decision.DeleteOwnedSave}, source={session.Source.Description}");
        _complete = true;
    }

    internal FarmingSession Session => session;
    private string? ValidateOwnedSave(IReadOnlyList<SaveSlotSnapshot> slots) =>
        OwnedSavePolicy.ValidateForDeletion(slots, session.OwnedSlot, _entryFloor, session.Source.Pinned);
    private bool _verificationPrepared;
    private void RequestLeave() => _context?.RunOptions.Update(o => o.LeaveMode = LeaveMode.Immediate);
    public void Dispose()
    {
        _create?.Reset();
        _prepared?.Reset();
        _verification?.Reset();
        _rest?.Dispose();
        (_delete as IDisposable)?.Dispose();
        _context = null;
    }
}
