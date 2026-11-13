using DeepDungeon.Fsd.Core;

var now = new DateTime(2026, 11, 10, 11, 24, 10, DateTimeKind.Utc);
const string question = "Leave the rest room?";
var tests = new List<(string Name, Action Test)>();

PtRestExitPromptAction Prompt(PtRestExitConfirmationState state, int milliseconds = 0,
    string actual = question, bool delete = false, bool readable = true,
    bool abandon = false, string expected = question) =>
    state.DecidePrompt(delete, readable, abandon, expected, actual, now.AddMilliseconds(milliseconds));

PtRestExitConfirmationState Interacted()
{
    var state = new PtRestExitConfirmationState();
    state.RecordInteraction(now, 123);
    return state;
}

void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected {expected}, got {actual}.");
}

tests.Add(("old-exit-prompt-is-cancelled-and-reacquired-not-confirmed", () =>
    Equal(PtRestExitPromptAction.RecoverKnownPrompt, Prompt(new()))));
tests.Add(("old-abandon-prompt-is-cancelled-not-confirmed", () =>
    Equal(PtRestExitPromptAction.RecoverKnownPrompt,
        Prompt(new(), actual: "Abandon duty?", abandon: true))));
tests.Add(("unrelated-prompt-keeps-unowned-protection", () =>
    Equal(PtRestExitPromptAction.RejectUnowned, Prompt(new(), actual: "Other dialog?"))));
tests.Add(("owned-prompt-can-be-confirmed", () =>
    Equal(PtRestExitPromptAction.Confirm, Prompt(Interacted(), 400))));
tests.Add(("owned-prompt-normalizes-whitespace", () =>
    Equal(PtRestExitPromptAction.Confirm, Prompt(Interacted(), actual: " Leave the\nrest room? "))));
tests.Add(("wrong-text-is-rejected-even-after-interaction", () =>
    Equal(PtRestExitPromptAction.RejectText, Prompt(Interacted(), actual: "Other dialog?"))));
tests.Add(("empty-expected-and-actual-cannot-authorize-confirmation", () =>
    Equal(PtRestExitPromptAction.RejectText, Prompt(Interacted(), actual: "", expected: ""))));
tests.Add(("unready-text-is-retried-without-clicking", () =>
    Equal(PtRestExitPromptAction.WaitForText, Prompt(Interacted(), readable: false))));
tests.Add(("expired-prompt-must-be-reacquired", () =>
    Equal(PtRestExitPromptAction.RecoverKnownPrompt, Prompt(Interacted(), 2001))));
tests.Add(("failed-callback-preserves-ownership-for-retry", () =>
{
    var state = Interacted();
    Equal(false, state.RecordConfirmation(false, now.AddMilliseconds(300)));
    Equal(PtRestExitConfirmationPhase.AwaitingPrompt, state.Phase);
    Equal(PtRestExitPromptAction.Confirm, Prompt(state, 650));
}));
tests.Add(("regression-same-addon-after-350ms-does-not-become-unowned", () =>
{
    var state = Interacted();
    Equal(true, state.RecordConfirmation(true, now.AddMilliseconds(300)));
    Equal(now, state.InteractionAtUtc);
    Equal(123UL, state.ExitObjectId);
    Equal(PtRestExitPromptAction.WaitForDeparture, Prompt(state, 650));
}));
tests.Add(("confirmed-prompt-never-gets-confirmed-twice-even-after-ownership-window", () =>
{
    var state = Interacted();
    state.RecordConfirmation(true, now.AddMilliseconds(300));
    Equal(PtRestExitPromptAction.WaitForDeparture, Prompt(state, 4000));
    Equal(false, state.RecordConfirmation(true, now.AddMilliseconds(4000)));
}));
tests.Add(("unreadable-closing-addon-is-not-a-new-error", () =>
{
    var state = Interacted();
    state.RecordConfirmation(true, now);
    Equal(PtRestExitPromptAction.WaitForDeparture, Prompt(state, 350, readable: false));
}));
tests.Add(("different-dialog-after-confirmation-is-not-clicked", () =>
{
    var state = Interacted();
    state.RecordConfirmation(true, now);
    Equal(PtRestExitPromptAction.RejectText, Prompt(state, 350, actual: "Other dialog?"));
}));
tests.Add(("delete-save-protection-applies-in-every-phase", () =>
{
    foreach (var phase in Enum.GetValues<PtRestExitConfirmationPhase>())
    {
        var state = new PtRestExitConfirmationState();
        if (phase == PtRestExitConfirmationPhase.AwaitingPrompt) state.RecordInteraction(now, 1);
        if (phase == PtRestExitConfirmationPhase.AwaitingDeparture)
        {
            state.RecordInteraction(now, 1);
            state.RecordConfirmation(true, now);
        }
        if (phase == PtRestExitConfirmationPhase.ClearingStalePrompt) state.TryBeginRecovery(now, true);
        Equal(PtRestExitPromptAction.RejectDelete, Prompt(state, delete: true));
    }
}));
tests.Add(("stale-prompt-must-close-before-a-new-interaction", () =>
{
    var state = new PtRestExitConfirmationState();
    Equal(true, state.TryBeginRecovery(now, true));
    Equal(PtRestExitPromptAction.WaitForPromptClear, Prompt(state));
    bool threw = false;
    try { state.RecordInteraction(now, 1); } catch (InvalidOperationException) { threw = true; }
    Equal(true, threw);
    state.ObservePromptClosed();
    state.RecordInteraction(now, 456);
    Equal(PtRestExitPromptAction.Confirm, Prompt(state, 200));
    Equal(456UL, state.ExitObjectId);
}));
tests.Add(("three-recoveries-are-bounded", () =>
{
    var state = new PtRestExitConfirmationState();
    for (int i = 0; i < 3; i++) Equal(true, state.TryBeginRecovery(now, false));
    Equal(false, state.TryBeginRecovery(now, false));
    Equal(3, state.RecoveryCount);
}));
tests.Add(("confirmed-exit-cannot-be-reinteracted-as-a-retry", () =>
{
    var state = Interacted();
    state.RecordConfirmation(true, now);
    Equal(false, state.TryBeginRecovery(now, false));
}));
tests.Add(("departure-timeout-is-bounded", () =>
{
    var state = Interacted();
    state.RecordConfirmation(true, now);
    Equal(false, state.DepartureTimedOut(now.AddMilliseconds(15000)));
    Equal(true, state.DepartureTimedOut(now.AddMilliseconds(15001)));
}));
tests.Add(("stale-prompt-close-timeout-is-bounded", () =>
{
    var state = new PtRestExitConfirmationState();
    state.TryBeginRecovery(now, true);
    Equal(false, state.PromptClearTimedOut(now.AddMilliseconds(5000)));
    Equal(true, state.PromptClearTimedOut(now.AddMilliseconds(5001)));
}));
tests.Add(("callback-without-an-interaction-cannot-mark-success", () =>
    Equal(false, new PtRestExitConfirmationState().RecordConfirmation(true, now))));
tests.Add(("next-cycle-clears-prior-confirmation-and-recovery-state", () =>
{
    var state = new PtRestExitConfirmationState();
    for (int cycle = 0; cycle < 2; cycle++)
    {
        state.Reset();
        Equal(PtRestExitConfirmationPhase.SeekingExit, state.Phase);
        Equal(0, state.RecoveryCount);
        Equal(DateTime.MinValue, state.InteractionAtUtc);
        state.RecordInteraction(now, (ulong)(100 + cycle));
        Equal(PtRestExitPromptAction.Confirm, Prompt(state));
        Equal(true, state.RecordConfirmation(true, now));
        Equal(PtRestExitPromptAction.WaitForDeparture, Prompt(state, 350));
    }
}));

foreach (var (name, test) in tests)
{
    test();
    Console.WriteLine($"PASS {name}");
}
Console.WriteLine($"Rest exit regression tests: {tests.Count} passed.");
