using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class ExitReceiptTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        { test(); count++; Console.WriteLine("  ok  exit-receipt-" + name); }
        const string scope = "770:True";
        var confirm = new EntryDispatchReceipt(nameof(ExitAction.ConfirmDutyExit), "transition", scope, "leave duty?");
        var result = new EntryDispatchReceipt(nameof(ExitAction.LeaveResult), "transition", scope, "exit");
        Check("hoh-30-confirmation-advances-to-result-without-zone-change", () =>
        {
            var next = ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.Result), true);
            Assert(next == ExitAction.LeaveResult && ExitReceiptPolicy.ShouldClear(confirm, scope, next));
        });
        Check("leave-flow-without-return-to-entrance-also-advances", () =>
        {
            var next = ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.Result), false);
            Assert(next == ExitAction.LeaveResult && ExitReceiptPolicy.ShouldClear(confirm, scope, next));
        });
        Check("result-can-advance-back-to-native-duty-confirmation", () =>
            Assert(ExitReceiptPolicy.ShouldClear(result, scope, ExitAction.ConfirmDutyExit)));
        Check("result-can-advance-to-rest-exit-confirmation", () =>
            Assert(ExitReceiptPolicy.ShouldClear(result, scope, ExitAction.ConfirmRestExit)));
        Check("same-confirmation-retains-receipt-and-retry-throttle", () =>
            Assert(!ExitReceiptPolicy.ShouldClear(confirm, scope, ExitAction.ConfirmDutyExit)));
        Check("same-result-retains-receipt-and-retry-throttle", () =>
            Assert(!ExitReceiptPolicy.ShouldClear(result, scope, ExitAction.LeaveResult)));
        Check("unknown-incomplete-and-transitioning-ui-never-release-confirmation", () =>
        {
            foreach (var ui in new[] { ExitUiKind.Unknown, ExitUiKind.Incomplete })
            {
                var next = ExitUiPolicy.Decide(new(false, true, false, false, false, ui), true);
                Assert(next == ExitAction.Wait && !ExitReceiptPolicy.ShouldClear(confirm, scope, next));
            }
            var wait = ExitUiPolicy.Decide(new(true, true, false, false, false, ExitUiKind.Result), true);
            Assert(wait == ExitAction.Wait && !ExitReceiptPolicy.ShouldClear(confirm, scope, wait));
        });
        Check("no-ui-does-not-immediately-discard-confirmation", () =>
            Assert(!ExitReceiptPolicy.ShouldClear(confirm, scope, ExitAction.RequestDutyExit)));
        Check("changed-duty-scope-clears-old-receipt", () =>
            Assert(ExitReceiptPolicy.ShouldClear(confirm, "770:False", ExitAction.Wait)));
        Check("only-recognized-step-pairs-release-within-same-scope", () =>
        {
            foreach (var previous in Enum.GetValues<ExitAction>())
            foreach (var next in Enum.GetValues<ExitAction>())
            {
                bool allowed = previous == ExitAction.ConfirmDutyExit && next == ExitAction.LeaveResult ||
                    previous == ExitAction.LeaveResult && next is ExitAction.ConfirmDutyExit or ExitAction.ConfirmRestExit;
                Assert(ExitReceiptPolicy.ShouldClear(new(previous.ToString(), "transition", scope), scope, next) == allowed);
            }
        });
        Check("result-step-still-needs-fresh-ui-stability", () =>
        {
            var gate = new EntryDispatchGate(300);
            gate.Observe("confirmation", 0); gate.RecordDispatch(200, confirm);
            Assert(ExitReceiptPolicy.ShouldClear(gate.Pending!.Value, scope, ExitAction.LeaveResult));
            gate.ClearReceipt(); gate.Observe("result", 300);
            Assert(!gate.CanDispatch(449) && gate.CanDispatch(450));
        });
        Check("dispatched-result-keeps-bounded-same-button-retry", () =>
        {
            var gate = new EntryDispatchGate(300);
            gate.Observe("result", 0); gate.RecordDispatch(150, result);
            Assert(!ExitReceiptPolicy.ShouldClear(gate.Pending!.Value, scope, ExitAction.LeaveResult));
            Assert(!gate.CanDispatch(449, true) && gate.CanDispatch(450, true));
            Assert(!gate.CanDispatch(1000));
        });
        Check("one-cycle-target-is-checked-after-exit-step-not-instead-of-it", () =>
        {
            var loops = new MultiLoopDriver(1, false);
            var next = ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.Result), true);
            Assert(next == ExitAction.LeaveResult && ExitReceiptPolicy.ShouldClear(confirm, scope, next));
            Assert(loops.CompletedLoops == 0);
            Assert(ExitUiPolicy.Decide(new(false, false, true, false, false, ExitUiKind.None), true) == ExitAction.Complete);
            Assert(loops.ShouldStopAfterCurrentRun());
        });
        Check("rest-area-remains-pending-until-entrance-return", () =>
        {
            Assert(ExitUiPolicy.Decide(new(false, false, false, true, false, ExitUiKind.None), true) == ExitAction.ApproachRestExit);
            Assert(ExitUiPolicy.Decide(new(false, false, true, false, false, ExitUiKind.None), true) == ExitAction.Complete);
        });
        Check("hoh-21-30-infinite-loop-delayed-result-still-advances", () =>
        {
            Assert(FreeExplorationPolicy.TryCreate(2, 21, 30, 1, true, default, false, false, out var plan, out _));
            var loops = new MultiLoopDriver(1, true, 2);
            var gate = new EntryDispatchGate(300);
            gate.Observe("confirmation", 0); gate.RecordDispatch(200, confirm);
            var waiting = ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.Incomplete), false);
            Assert(waiting == ExitAction.Wait && !ExitReceiptPolicy.ShouldClear(confirm, scope, waiting));
            // Mirrors 08:48:21 confirmation followed by the recognized result at 08:48:26.
            var next = ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.Result), false);
            Assert(next == ExitAction.LeaveResult && ExitReceiptPolicy.ShouldClear(gate.Pending!.Value, scope, next));
            gate.ClearReceipt(); gate.Observe("result", 5000);
            Assert(!gate.CanDispatch(5149) && gate.CanDispatch(5150));
            Assert(ExitUiPolicy.Decide(new(false, false, true, false, false, ExitUiKind.None), true) == ExitAction.Complete);
            var decision = FarmingSessionPolicy.Decide(plan!, new(21, true, false, false, false));
            Assert(decision.CountCycle && decision.DeleteOwnedSave && decision.NextFloor == 21 && decision.Error.Length == 0);
            Assert(!loops.ShouldStopAfterCurrentRun());
            loops.IncrementLoop(); Assert(loops.CompletedLoops == 3 && loops.InfiniteLoop);
        });
        Check("scoreboard-delays-do-not-expire-recognized-next-step", () =>
        {
            foreach (long delay in new long[] { 0, 1000, 5000, 30000 })
            {
                var gate = new EntryDispatchGate(300);
                gate.Observe("confirmation", 0); gate.RecordDispatch(200, confirm);
                var loading = ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.Incomplete), true);
                Assert(loading == ExitAction.Wait && !ExitReceiptPolicy.ShouldClear(confirm, scope, loading));
                var next = ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.Result), true);
                Assert(ExitReceiptPolicy.ShouldClear(gate.Pending!.Value, scope, next));
                gate.ClearReceipt(); gate.Observe("ready-scoreboard", 500 + delay);
                Assert(!gate.CanDispatch(649 + delay) && gate.CanDispatch(650 + delay));
            }
        });
        Check("scoreboard-without-prior-confirmation-still-requires-ready-stable-ui", () =>
        {
            var gate = new EntryDispatchGate(300);
            foreach (var ui in new[] { ExitUiKind.Incomplete, ExitUiKind.Unknown })
                Assert(ExitUiPolicy.Decide(new(false, true, false, false, false, ui), true) == ExitAction.Wait);
            Assert(ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.Result), true) == ExitAction.LeaveResult);
            gate.Observe("ready-scoreboard", 10000);
            Assert(!gate.CanDispatch(10149) && gate.CanDispatch(10150));
            gate.RecordDispatch(10150, result);
            Assert(!gate.CanDispatch(10449, true) && gate.CanDispatch(10450, true));
        });
        Check("scoreboard-followup-confirmation-must-finish-before-loop-completes", () =>
        {
            var loops = new MultiLoopDriver(2, false);
            var next = ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.DutyConfirmation), true);
            Assert(next == ExitAction.ConfirmDutyExit && ExitReceiptPolicy.ShouldClear(result, scope, next));
            Assert(loops.CompletedLoops == 0);
            Assert(ExitUiPolicy.Decide(new(true, false, false, false, false, ExitUiKind.None), true) == ExitAction.Wait);
            Assert(ExitUiPolicy.Decide(new(false, false, false, true, false, ExitUiKind.None), true) == ExitAction.ApproachRestExit);
            Assert(loops.CompletedLoops == 0);
            Assert(ExitUiPolicy.Decide(new(false, false, true, false, false, ExitUiKind.None), true) == ExitAction.Complete);
            loops.IncrementLoop();
            Assert(loops.CompletedLoops == 1 && loops.ShouldStopAfterCurrentRun());
        });
        return count;
    }

    private static void Assert(bool condition)
    { if (!condition) throw new Exception("Exit receipt regression failed."); }
}
