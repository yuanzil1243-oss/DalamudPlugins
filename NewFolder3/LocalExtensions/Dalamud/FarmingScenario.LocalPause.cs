using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

internal sealed partial class FarmingScenario
{
    private bool _localPauseVerificationActive;

    internal bool CanCancelFloorsetPause => !_complete && _context?.StatusIsError != true &&
        session.HasPendingFloorsetPause;

    internal bool TryCancelFloorsetPause()
    {
        if (!CanCancelFloorsetPause) return false;
        bool cancelVerification = _localPauseVerificationActive || session.PersistPause != null;
        if (!session.TryCancelPendingFloorsetPause()) return false;
        // Only pause-owned verification is abandoned. Prepared-save verification,
        // deletion and the normal exit remain owned by the original task.
        if (cancelVerification)
        {
            _verification?.Dispose();
            _verification = null;
            _localPauseVerificationActive = false;
        }
        if (_context is { } ctx)
            ctx.StatusLine = "已取消本組後暫停，將按原任務及循環次數繼續。";
        return true;
    }

    private bool TryPauseBeforeCleanup(RunContext ctx, FarmingAttemptResult result,
        FarmingSessionDecision decision)
    {
        if (session.LocalPause is not { } request) return false;
        if (result.DutyFailed || result.Aborted || decision.Error.Length > 0 ||
            !result.DutyCompleted && !session.Plan.ReusesSave)
        {
            FinishPause(ctx, "本組未正常完成；暫停並保留存檔，不重試或刪檔。", error: true);
            return true;
        }
        if (!decision.CountCycle)
        {
            if (session.Plan.Mode != FarmingMode.DeepProgression && !RelicFarmingPolicy.CanContinue(session.Plan) || decision.NextFloor <= _entryFloor)
                FinishPause(ctx, "本組沒有可驗證的後續進度；停止並保留存檔。", error: true);
            else
            {
                _localPauseVerificationActive = true;
                VerifyPauseSave(ctx, decision.NextFloor, slot => request.Persist(
                    FloorsetResumeKind.ContinueSave, slot, request.CompletedCycles, session.Failures));
            }
            return true;
        }
        return false; // Let upstream finish its existing verification/deletion/cycle accounting.
    }

    private bool TryPauseAfterCleanup(RunContext ctx)
    {
        if (session.LocalPause is not { } request) return false;
        int completed = request.CompletedCycles + (CountsAsCycle ? 1 : 0);
        if (!FloorsetPausePolicy.HasNextCycle(session.Plan, completed))
        {
            FinishPause(ctx, "本組與原定收尾已完成；任務已結束，沒有待繼續的循環。");
            return true;
        }
        var kind = session.Plan.ReusesSave ? FloorsetResumeKind.ReusePrepared : FloorsetResumeKind.RestartCycle;
        SaveSlotSnapshot? slot = session.Plan.ReusesSave ? session.Source.Pinned : null;
        string? error = request.Persist(kind, slot, completed, session.Failures);
        FinishPause(ctx, error ?? "本組與原定收尾已完成，已暫停；可繼續原任務或解除續跑鎖定。", error != null);
        return true;
    }
}
