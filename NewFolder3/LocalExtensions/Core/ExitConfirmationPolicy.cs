namespace DeepDungeon.Fsd.Core;

/// <summary>Exact localized native-exit vocabulary, scoped to a completed terminal reward room.</summary>
public static class ExitConfirmationPolicy
{
    public static ReadOnlySpan<uint> NativeExitPromptRows => [103, 104, 105];

    public static bool CanConfirmNativeExit(uint dungeonId, int floor, bool inDuty,
        bool resultWindowPresent, bool terminalRoomObserved, bool rewardsPending, bool dutyCompleted) =>
        (inDuty || resultWindowPresent) && terminalRoomObserved && !rewardsPending && dutyCompleted &&
        TerminalRewardExitPolicy.TryGetExitBaseId(dungeonId, floor, out _);

    // All text is normalized with the same client-localized vocabulary as the existing exit reader.
    public static ExitUiKind Classify(string text, string dutyPrompt, IReadOnlySet<string> restPrompts,
        IReadOnlySet<string> nativeExitPrompts, bool terminalExitReady)
    {
        if (text.Length == 0) return ExitUiKind.Unknown;
        if (dutyPrompt.Length > 0 && text == dutyPrompt) return ExitUiKind.DutyConfirmation;
        if (restPrompts.Contains(text)) return ExitUiKind.RestConfirmation;
        return terminalExitReady && nativeExitPrompts.Contains(text)
            ? ExitUiKind.DutyConfirmation : ExitUiKind.Unknown;
    }
}
