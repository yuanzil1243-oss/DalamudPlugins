using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Runtime;
using FFXIVClientStructs.FFXIV.Component.GUI;
using OmenTools.Extensions;
using global::Dalamud.Utility;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

internal sealed partial class FarmingScenario
{
    private ulong _failureCharacter;
    private int _lastFailureScopeFloor;
    private uint _lastFailureScopeTerritory;
    private long _nextFailureResultClick;
    private long _nextFailureObservationError;
    private DungeonEntryController? _failureVerification;
    private PreparedSaveBinding? _failureSource;
    private SaveSlotSnapshot? _verifiedFailedSave;

    // Runs before the host's native-controller availability guard. The result addon
    // is independently verifiable even when the floor controller has disappeared.
    internal bool TryHandleFailureResult()
    {
        try { return ObserveFailureResult(); }
        catch (Exception error)
        {
            // This observer runs ahead of the host guard. A transient addon read
            // must not escape Update or invent an outcome/deletion permission.
            long now = Environment.TickCount64;
            if (now >= _nextFailureObservationError)
            {
                _nextFailureObservationError = now + 5000;
                Service.Log.Warning($"[LocalExtensions.Failure] 結算觀測暫未完成，等待重試：{error.Message}");
            }
            return false;
        }
    }

    private unsafe bool ObserveFailureResult()
    {
        var ctx = _context;
        if (ctx == null || !_entered || _complete || ctx.StatusIsError || ctx.Duty.StateReadIncompatible ||
            DutyState.IsClientTransitioning || Service.LocalPlayer == null) return false;
        ulong character = Service.PlayerState.ContentId;
        uint territory = Service.ClientState.TerritoryType;
        if (!ctx.Duty.StateReadFailed && ctx.Duty.IsInDuty && ctx.Duty.DungeonId == _dungeon.DutyDungeonId &&
            ctx.Duty.Floor >= _entryFloor && ctx.Duty.Floor < _entryFloor + 10)
        {
            if (_failureCharacter == 0) _failureCharacter = character;
            _lastFailureScopeFloor = ctx.Duty.Floor;
            _lastFailureScopeTerritory = territory;
        }
        bool sameAttempt = character != 0 && character == _failureCharacter && territory == _lastFailureScopeTerritory &&
            _dungeon.TerritoryFloorRanges.TryGetValue(territory, out var range) && range.Item1 == _entryFloor &&
            _lastFailureScopeFloor >= range.Item1 && _lastFailureScopeFloor <= range.Item2;
        if (!sameAttempt || ctx.DutyCompletionObserved || !TryReadFailureResult(out var result, out var buttonNode))
            return false;
        var facts = new FailedResultFacts(_entered, sameAttempt, false, Service.LocalPlayer.IsDead,
            true, ctx.DutyCompletionObserved, ctx.DutyFailureObserved);
        if (!ctx.DutyFailureObserved)
        {
            if (!FailedAttemptRecoveryPolicy.ShouldObserveFailure(facts)) return false;
            ctx.MarkDutyFailed();
            ctx.Navigator.CancelAll();
            ctx.RecordDiagnostic?.Invoke("local-failure-result-observed", new
            { floor = _lastFailureScopeFloor, territory, entry = _entryFloor, source = "dead-player-and-native-final-results", facts });
            Service.Log.Info("[LocalExtensions.Failure] 已確認死亡結算；退本後讀取原存檔決定續打或重開。");
        }
        if (ctx.TryHoldFailedAttempt("攻略失敗")) return false;
        RequestLeave();
        ctx.StatusLine = "攻略已結算；退出後檢查原存檔，能續打則續打，不能續打才重開。";
        long now = Environment.TickCount64;
        if (now >= _nextFailureResultClick)
        {
            _nextFailureResultClick = now + InteractionTiming.GetTransitionRetryMilliseconds();
            bool sent = false;
            var evt = buttonNode->AtkEventManager.Event;
            for (int i = 0; evt != null && i < 64; i++, evt = evt->NextEvent)
                if (evt->State.EventType == AtkEventType.ButtonClick)
                { result->ReceiveEvent(evt->State.EventType, (int)evt->Param, evt); sent = true; break; }
            ctx.RecordDiagnostic?.Invoke("local-failure-result-exit", new { sent, floor = _lastFailureScopeFloor });
        }
        return true;
    }

    private static unsafe bool TryReadFailureResult(out AtkUnitBase* result, out AtkResNode* buttonNode)
    {
        buttonNode = null;
        if (!DeepDungeonUi.TryGetVisibleAddon("DeepDungeonResult", out result) || !result->IsAddonAndNodesReady()) return false;
        var titleNode = result->UldManager.SearchNodeById(2);
        var title = titleNode == null ? null : titleNode->GetAsAtkTextNode();
        buttonNode = result->UldManager.SearchNodeById(25);
        var button = buttonNode == null ? null : buttonNode->GetAsAtkComponentButton();
        if (title == null || button == null || button->ButtonTextNode == null || !button->IsEnabled) return false;
        var sheet = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
        string expectedTitle = PtEntryText.Normalize(sheet.GetRow(10438).Text.ExtractText());
        string expectedExit = PtEntryText.Normalize(sheet.GetRow(10444).Text.ExtractText());
        return expectedTitle.Length > 0 && expectedExit.Length > 0 &&
            PtEntryText.Normalize(title->NodeText.ExtractText()) == expectedTitle &&
            PtEntryText.Normalize(button->ButtonTextNode->NodeText.ExtractText()) == expectedExit;
    }

    private bool TryReconcileFailedSave(RunContext ctx, in FarmingAttemptResult result,
        ref FarmingSessionDecision decision)
    {
        if (!result.DutyFailed || !decision.RecoverFailure || !TaskContinuationPolicy.CanDeleteSave(session.Plan)) return true;
        if (_verifiedFailedSave == null)
        {
            if (_failureVerification == null)
            {
                _failureSource = new(requiredSlot: session.OwnedSlot, maxFloor: session.Plan.MaxFloor)
                    { FailedAttemptFloor = _entryFloor };
                if (session.Source.Pinned is { Empty: false } pinned) _failureSource.PinCheckpoint(pinned);
                _failureVerification = new(_dungeon, _entryFloor, prepared: _failureSource, verifyOnly: true);
                _failureVerification.Prepare(ctx);
            }
            var verified = _failureVerification.Update();
            if (verified.Outcome == EntryOutcome.Pending) return false;
            if (verified.Outcome != EntryOutcome.SourceVerified || _failureSource?.Pinned is not { } source)
            { ctx.StatusIsError = true; _complete = true; return false; }
            _verifiedFailedSave = source;
            if (source.Enterable) session.BindFailedSaveCheckpoint(source);
            ctx.RecordDiagnostic?.Invoke("local-failure-save-reconciled", new
            { source.Index, source.StartFloor, source.Enterable, source.RowFingerprint,
                recovery = source.Enterable ? "resume-current-save" : "delete-verified-save-and-restart" });
            _failureVerification.Dispose();
            _failureVerification = null;
        }
        decision = FailedAttemptRecoveryPolicy.Reconcile(session.Plan, decision, _verifiedFailedSave.Value);
        return true;
    }
}
