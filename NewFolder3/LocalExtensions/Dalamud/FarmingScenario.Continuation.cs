using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

internal sealed partial class FarmingScenario
{
    private DungeonEntryController? _continuationVerification;
    private PreparedSaveBinding? _continuationSource;
    private bool _stopAfterAttempt;

    private bool TryAttachSelectedMode(RunContext ctx)
    {
        if (ctx.Duty.StateReadFailed || ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning)
            return false;
        var facts = CurrentDutyResumeScenario.ReadFacts(ctx.Duty, false);
        if (!ContinueExplorationPolicy.TryAttach(facts, out int floor, out var error) ||
            facts.DungeonId != session.Plan.DungeonId || floor != session.NextFloor ||
            !ExternalSavePolicy.MatchesCharacter(session.Plan, facts.Character, facts.Job) ||
            !TaskContinuationPolicy.ValidateMode(session.Plan, floor, out error))
        {
            ctx.StatusLine = error.Length > 0 ? error : "目前副本與所選續打模式不符。";
            ctx.StatusIsError = true;
            return false;
        }
        session.ConfirmDutyAttachment();
        BeginDuty(ctx, floor);
        ctx.StatusLine = $"按「{session.Plan.DisplayName}」接續第 {facts.Floor} 層。";
        ctx.RecordDiagnostic?.Invoke("local-mode-continuation-attached", new
        { plan = session.Plan, floor = facts.Floor, groupStart = floor, source = "verify-after-exit" });
        return true;
    }

    private bool TryBindContinuationAfterExit(RunContext ctx, FarmingSessionDecision decision)
    {
        if (!session.NeedsContinuationSource || decision.Error.Length > 0 ||
            decision.StopAfterAttempt && !decision.DeleteOwnedSave) return true;
        if (_continuationVerification == null)
        {
            _continuationSource = new(maxFloor: session.Plan.MaxFloor)
                { ContinuationResultFloor = _entryFloor };
            // Verification only: no save selection, entry, confirmation or deletion.
            _continuationVerification = new(_dungeon, _entryFloor,
                prepared: _continuationSource, verifyOnly: true);
            _continuationVerification.Prepare(ctx);
        }
        var result = _continuationVerification.Update();
        if (result.Outcome == EntryOutcome.Pending) return false;
        if (result.Outcome != EntryOutcome.SourceVerified || _continuationSource?.Pinned is not { } slot)
        {
            ctx.StatusIsError = true;
            _complete = true;
            return false;
        }
        session.BindContinuationSource(slot);
        ctx.RecordDiagnostic?.Invoke("local-mode-continuation-source-verified", new
        { slot.Index, slot.StartFloor, slot.RowFingerprint, entry = _entryFloor });
        _continuationVerification.Dispose();
        _continuationVerification = null;
        return true;
    }
}
