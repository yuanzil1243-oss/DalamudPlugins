namespace DeepDungeon.Fsd.Core;

public enum PtRestExitConfirmationPhase
{
    SeekingExit,
    AwaitingPrompt,
    AwaitingDeparture,
    ClearingStalePrompt
}

public enum PtRestExitPromptAction
{
    Confirm,
    WaitForText,
    WaitForDeparture,
    WaitForPromptClear,
    RecoverKnownPrompt,
    RejectDelete,
    RejectUnowned,
    RejectText
}

/// <summary>Tracks one exit interaction through prompt confirmation and departure.</summary>
public sealed class PtRestExitConfirmationState
{
    public const int OwnershipWindowMilliseconds = 2000;
    public const int DepartureTimeoutMilliseconds = 15000;
    public const int PromptClearTimeoutMilliseconds = 5000;
    public const int MaximumRecoveries = 3;

    public PtRestExitConfirmationPhase Phase { get; private set; }
    public DateTime InteractionAtUtc { get; private set; } = DateTime.MinValue;
    public DateTime ConfirmationAtUtc { get; private set; } = DateTime.MinValue;
    public DateTime RecoveryAtUtc { get; private set; } = DateTime.MinValue;
    public ulong ExitObjectId { get; private set; }
    public int RecoveryCount { get; private set; }

    public void Reset()
    {
        Phase = PtRestExitConfirmationPhase.SeekingExit;
        InteractionAtUtc = ConfirmationAtUtc = RecoveryAtUtc = DateTime.MinValue;
        ExitObjectId = 0;
        RecoveryCount = 0;
    }

    public void RecordInteraction(DateTime nowUtc, ulong exitObjectId)
    {
        if (Phase != PtRestExitConfirmationPhase.SeekingExit)
            throw new InvalidOperationException("An exit interaction is already pending.");
        InteractionAtUtc = nowUtc;
        ExitObjectId = exitObjectId;
        Phase = PtRestExitConfirmationPhase.AwaitingPrompt;
    }

    public bool RecordConfirmation(bool callbackAccepted, DateTime nowUtc)
    {
        if (!callbackAccepted || Phase != PtRestExitConfirmationPhase.AwaitingPrompt) return false;
        // Keep the interaction evidence until departure; the same addon can remain
        // visible for several frames after a successful callback.
        ConfirmationAtUtc = nowUtc;
        Phase = PtRestExitConfirmationPhase.AwaitingDeparture;
        return true;
    }

    public PtRestExitPromptAction DecidePrompt(
        bool deletePrompt, bool promptReadable, bool abandonPrompt,
        string expectedPrompt, string actualPrompt, DateTime nowUtc)
    {
        if (deletePrompt) return PtRestExitPromptAction.RejectDelete;
        bool expectedMatches = promptReadable && !string.IsNullOrWhiteSpace(expectedPrompt) &&
            ControlledPtSurveyPolicy.DecideRestExitPrompt(
                false, true, 0, OwnershipWindowMilliseconds, expectedPrompt, actualPrompt) ==
            ControlledPtRestPromptDecision.Accept;

        if (Phase == PtRestExitConfirmationPhase.AwaitingDeparture)
            return !promptReadable || expectedMatches
                ? PtRestExitPromptAction.WaitForDeparture
                : PtRestExitPromptAction.RejectText;

        if (Phase == PtRestExitConfirmationPhase.ClearingStalePrompt)
            return !promptReadable || expectedMatches || abandonPrompt
                ? PtRestExitPromptAction.WaitForPromptClear
                : PtRestExitPromptAction.RejectText;

        if (!promptReadable) return PtRestExitPromptAction.WaitForText;

        if (Phase == PtRestExitConfirmationPhase.SeekingExit)
            return expectedMatches || abandonPrompt
                ? PtRestExitPromptAction.RecoverKnownPrompt
                : PtRestExitPromptAction.RejectUnowned;

        if (!expectedMatches)
            return abandonPrompt ? PtRestExitPromptAction.RecoverKnownPrompt : PtRestExitPromptAction.RejectText;

        return InteractionExpired(nowUtc)
            ? PtRestExitPromptAction.RecoverKnownPrompt
            : PtRestExitPromptAction.Confirm;
    }

    public bool InteractionExpired(DateTime nowUtc) =>
        Phase == PtRestExitConfirmationPhase.AwaitingPrompt &&
        (nowUtc - InteractionAtUtc).TotalMilliseconds > OwnershipWindowMilliseconds;

    public bool DepartureTimedOut(DateTime nowUtc) =>
        Phase == PtRestExitConfirmationPhase.AwaitingDeparture &&
        (nowUtc - ConfirmationAtUtc).TotalMilliseconds > DepartureTimeoutMilliseconds;

    public bool PromptClearTimedOut(DateTime nowUtc) =>
        Phase == PtRestExitConfirmationPhase.ClearingStalePrompt &&
        (nowUtc - RecoveryAtUtc).TotalMilliseconds > PromptClearTimeoutMilliseconds;

    public bool TryBeginRecovery(DateTime nowUtc, bool promptStillOpen)
    {
        if (Phase == PtRestExitConfirmationPhase.AwaitingDeparture || RecoveryCount >= MaximumRecoveries)
            return false;
        RecoveryCount++;
        InteractionAtUtc = ConfirmationAtUtc = DateTime.MinValue;
        ExitObjectId = 0;
        RecoveryAtUtc = nowUtc;
        Phase = promptStillOpen
            ? PtRestExitConfirmationPhase.ClearingStalePrompt
            : PtRestExitConfirmationPhase.SeekingExit;
        return true;
    }

    public void ObservePromptClosed()
    {
        if (Phase == PtRestExitConfirmationPhase.ClearingStalePrompt)
            Phase = PtRestExitConfirmationPhase.SeekingExit;
    }
}
