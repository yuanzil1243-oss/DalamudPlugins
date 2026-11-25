using System.Reflection;
using System.Runtime.CompilerServices;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace NewFolder3;

internal static class PauseCancellationTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        { test(); count++; Console.WriteLine("  ok  pause-cancel-" + name); }
        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 3, false, false, true, true, false);
        var slot = new SaveSlotSnapshot(1, false, 11, "第11朝聖路", "機工士", true)
            { RowFingerprint = new string('A', 64) };
        LocalPauseRequest Request() => new(0, (_, _, _, _) => throw new Exception("Cancellation must not persist a pause."));

        Check("no-request-is-a-no-op", () =>
        {
            var session = new FarmingSession(plan);
            Assert(!session.HasPendingFloorsetPause && !session.TryCancelPendingFloorsetPause());
        });
        Check("local-request-is-withdrawn", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request() };
            Assert(session.HasPendingFloorsetPause && session.TryCancelPendingFloorsetPause());
            Assert(!session.HasPendingFloorsetPause && session.LocalPause == null);
        });
        Check("original-request-is-withdrawn", () =>
        {
            var session = new FarmingSession(plan) { PersistPause = _ => throw new Exception("Must not persist.") };
            Assert(session.TryCancelPendingFloorsetPause() && session.PersistPause == null);
        });
        Check("both-pause-mechanisms-are-cleared-together", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request(), PersistPause = _ => "must not run" };
            Assert(session.TryCancelPendingFloorsetPause());
            Assert(session.LocalPause == null && session.PersistPause == null);
        });
        Check("repeated-cancel-does-not-rearm", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request() };
            Assert(session.TryCancelPendingFloorsetPause());
            Assert(!session.TryCancelPendingFloorsetPause() && !session.HasPendingFloorsetPause);
        });
        Check("request-can-be-made-again-after-cancel", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request() };
            Assert(session.TryCancelPendingFloorsetPause());
            var next = Request(); session.LocalPause = next;
            Assert(session.HasPendingFloorsetPause && ReferenceEquals(session.LocalPause, next));
            Assert(session.TryCancelPendingFloorsetPause());
        });
        Check("plan-loop-targets-and-chest-options-retained", () =>
        {
            var session = new FarmingSession(plan with { Infinite = true, FreeExploration = true, EndFloor = 30 }) { LocalPause = Request() };
            var original = session.Plan;
            Assert(session.TrySetTargets(new(false, false, true, true)));
            Assert(session.TryCancelPendingFloorsetPause());
            Assert(ReferenceEquals(session.Plan, original) && session.Plan.TargetCycles == 3 && session.Plan.Infinite);
            Assert(!session.OpenGold && session.OpenSilver && session.OpenBronze);
        });
        Check("source-and-resume-validation-are-retained", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request() };
            Func<string?> resume = () => null;
            Action<string> reject = _ => { };
            session.RestoreCheckpoint(slot, 2, 123, resume, reject);
            var source = session.Source;
            Assert(session.TryCancelPendingFloorsetPause());
            Assert(ReferenceEquals(session.Source, source) && source.Pinned == slot && source.RequireCheckpointMatch);
            Assert(session.OwnedSlot == 1 && session.NextFloor == 11 && session.Failures == 2);
            Assert(session.ResumeCharacter == 123 && session.ConfirmResume == resume && session.RejectResumeSource == reject);
        });
        Check("normal-progress-and-failure-count-retained", () =>
        {
            var session = new FarmingSession(plan, 0) { LocalPause = Request() };
            session.RestoreLocalFailureCount(2);
            var decision = FarmingSessionPolicy.Decide(plan, new(1, true, false, false, false));
            session.CompleteAttempt(decision, 2);
            Assert(session.TryCancelPendingFloorsetPause());
            Assert(session.NextFloor == 11 && session.OwnedSlot == 0 && session.Failures == 2 && session.Discoveries == 2);
        });
        Check("farming-scenario-withdraws-request-without-stopping", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request() };
            var scenario = new FarmingScenario(session);
            Assert(scenario.CanCancelFloorsetPause && scenario.TryCancelFloorsetPause());
            Assert(!scenario.CanCancelFloorsetPause && !scenario.IsComplete && scenario.ShouldLoop);
        });
        Check("farming-scenario-also-withdraws-original-request", () =>
        {
            var session = new FarmingSession(plan) { PersistPause = _ => "must not run" };
            var scenario = new FarmingScenario(session);
            Assert(scenario.TryCancelFloorsetPause() && session.PersistPause == null && !scenario.IsComplete);
        });
        Check("completed-pause-cannot-be-cancelled-as-a-pending-request", () =>
        {
            var request = Request(); var session = new FarmingSession(plan) { LocalPause = request };
            var scenario = new FarmingScenario(session); Set(scenario, "_complete", true);
            Assert(!scenario.CanCancelFloorsetPause && !scenario.TryCancelFloorsetPause());
            Assert(ReferenceEquals(session.LocalPause, request));
        });
        Check("errors-are-not-cleared-by-cancel", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request() };
            var scenario = new FarmingScenario(session);
            var ctx = (RunContext)RuntimeHelpers.GetUninitializedObject(typeof(RunContext));
            ctx.StatusIsError = true; ctx.StatusLine = "retain failure"; Set(scenario, "_context", ctx);
            Assert(!scenario.TryCancelFloorsetPause() && session.HasPendingFloorsetPause);
            Assert(ctx.StatusIsError && ctx.StatusLine == "retain failure");
        });
        Check("pause-owned-verifier-is-released", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request() };
            var scenario = new FarmingScenario(session);
            Set(scenario, "_localPauseVerificationActive", true);
            Set(scenario, "_verification", new DungeonEntryController(DungeonCatalog.PilgrimsTraverse, 11, verifyOnly: true));
            Assert(scenario.TryCancelFloorsetPause());
            Assert(Get(scenario, "_verification") == null && Get(scenario, "_localPauseVerificationActive") is false);
        });
        Check("normal-prepared-save-verifier-is-not-cancelled", () =>
        {
            var session = new FarmingSession(plan) { LocalPause = Request() };
            var scenario = new FarmingScenario(session);
            var verifier = new DungeonEntryController(DungeonCatalog.PilgrimsTraverse, 11, verifyOnly: true);
            Set(scenario, "_verification", verifier);
            Assert(scenario.TryCancelFloorsetPause());
            Assert(ReferenceEquals(Get(scenario, "_verification"), verifier));
        });
        Check("legacy-pause-verifier-is-released", () =>
        {
            var session = new FarmingSession(plan) { PersistPause = _ => "must not run" };
            var scenario = new FarmingScenario(session);
            Set(scenario, "_verification", new DungeonEntryController(DungeonCatalog.PilgrimsTraverse, 11, verifyOnly: true));
            Assert(scenario.TryCancelFloorsetPause() && Get(scenario, "_verification") == null);
        });
        Check("current-duty-request-can-be-withdrawn-and-rearmed", () =>
        {
            var scenario = new CurrentDutyResumeScenario(plan with { CurrentDutyOnly = true }, 21) { PauseAfterGroup = true };
            Assert(scenario.CanCancelFloorsetPause && scenario.TryCancelFloorsetPause());
            Assert(!scenario.PauseAfterGroup && !scenario.CanCancelFloorsetPause && !scenario.TryCancelFloorsetPause());
            Assert(!scenario.IsComplete && !scenario.ShouldLoop && !scenario.CountsAsCycle);
            scenario.PauseAfterGroup = true; Assert(scenario.TryCancelFloorsetPause());
        });
        Check("completed-current-duty-retains-original-stop", () =>
        {
            var scenario = new CurrentDutyResumeScenario(plan, 21) { PauseAfterGroup = true };
            Set(scenario, "_complete", true);
            Assert(!scenario.TryCancelFloorsetPause() && scenario.PauseAfterGroup && scenario.IsComplete);
        });
        Check("current-duty-error-remains-an-error", () =>
        {
            var scenario = new CurrentDutyResumeScenario(plan, 21) { PauseAfterGroup = true };
            var ctx = (RunContext)RuntimeHelpers.GetUninitializedObject(typeof(RunContext));
            ctx.StatusIsError = true; Set(scenario, "_context", ctx);
            Assert(!scenario.TryCancelFloorsetPause() && scenario.PauseAfterGroup && ctx.StatusIsError);
        });
        Check("cancellation-does-not-extend-a-one-cycle-target", () =>
        {
            FreeExplorationPolicy.TryCreate(2, 21, 30, 1, false, default, false, false, out var free, out _);
            var session = new FarmingSession(free!) { LocalPause = Request() };
            Assert(session.TryCancelPendingFloorsetPause());
            var result = FarmingSessionPolicy.Decide(session.Plan, new(21, true, false, false, false));
            Assert(result.CountCycle && result.DeleteOwnedSave && result.Error.Length == 0);
            Assert(new MultiLoopDriver(1, false).ShouldStopAfterCurrentRun());
        });
        return count;
    }

    private static void Set(object obj, string field, object value) =>
        obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(obj, value);
    private static object? Get(object obj, string field) =>
        obj.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj);
    private static void Assert(bool condition)
    { if (!condition) throw new InvalidOperationException("Pause cancellation regression failed."); }
}
