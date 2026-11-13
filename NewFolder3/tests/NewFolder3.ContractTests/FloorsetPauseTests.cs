using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using Dalamud.Plugin.Services;

namespace NewFolder3;

internal static class FloorsetPauseTests
{
    private static FarmingPlan Plan => new(FarmingMode.DeepProgression, SaveUse.Create,
        1, 1, false, false, true, true, false);
    private static FarmingAttemptResult Clear(int floor = 1) => new(floor, true, false, false, false);
    private static FarmingSession Armed(FarmingPlan? plan = null)
    {
        var session = new FarmingSession(plan ?? Plan, 0);
        Assert(session.SetPauseAfterFloorsetClear(true), "Expected a supported pause request.");
        return session;
    }

    public static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("floorset-pause-is-opt-in", OptIn),
            ("floorset-pause-can-be-cancelled-before-clear", CancelRequest),
            ("floorset-pause-does-not-consume-request-on-failure", FailureKeepsRequest),
            ("floorset-pause-rejects-abort-and-incomplete-duty", RejectIncomplete),
            ("floorset-pause-retains-save-source-and-counters", RetainsSession),
            ("floorset-resume-is-one-shot-and-can-be-rearmed", ResumeIsOneShot),
            ("floorset-resume-preserves-next-save-validation", ValidationSurvivesResume),
            ("floorset-pause-cannot-bypass-diagnostic-stop", ExplicitStopWins),
            ("floorset-pause-rejects-reusable-and-unowned-save", RejectUnsupported),
            ("floorset-pause-cannot-defer-hoard-cleanup", HoardCleanupRequired),
            ("hoard-cleanup-verifies-before-pause-and-resume-does-not-delete-twice", HoardScenarioCleanup),
            ("hoard-cleanup-failure-cannot-pause-or-count", CleanupFailure),
            ("hoard-without-pause-still-cleans-and-counts-once", UnpausedCleanup),
            ("progression-terminal-cleanup-precedes-pause", TerminalCleanup),
            ("ordinary-scenarios-reject-final-boss-and-no-save-plans", RejectWrongSaveScenario),
            ("no-save-policy-is-exclusive-to-valid-final-boss-practice", NoSaveScope),
            ("final-boss-scenario-rejects-ordinary-save-plans", FinalBossScenarioScope),
            ("floorset-scenario-holds-until-resume-without-counting", ScenarioHoldsAndResumes),
            ("floorset-scenario-waits-through-duty-transition", ScenarioWaitsForTransition),
            ("floorset-continued-session-can-pause-again", LaterFloorset),
            ("checkpoint-scenario-stops-at-50-without-delete-count-or-reentry", CheckpointScenarioStops)
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine($"  ok  {name}"); }
        return tests.Length;
    }

    private static bool Pause(FarmingSession session, FarmingAttemptResult result) =>
        session.TryPauseAfterFloorsetClear(result, FarmingSessionPolicy.Decide(session.Plan, result));

    private static void OptIn() => Assert(!Pause(new FarmingSession(Plan, 0), Clear()), "Normal continuation must stay automatic.");
    private static void CancelRequest()
    {
        var session = Armed();
        session.SetPauseAfterFloorsetClear(false);
        Assert(!Pause(session, Clear()) && !session.IsPaused, "Cancelling must prevent the pending pause.");
    }
    private static void FailureKeepsRequest()
    {
        var session = Armed();
        var failed = new FarmingAttemptResult(1, false, true, false, false);
        Assert(!Pause(session, failed) && session.PauseAfterFloorsetClear, "Failure is not a ten-floor clear.");
        session.ConfirmOwnedSaveDeleted();
        session.CompleteAttempt(FarmingSessionPolicy.Decide(Plan, failed), 0);
        session.BindCreatedSave(0);
        Assert(session.Failures == 1 && Pause(session, Clear()), "Request must survive recovery until a successful clear.");
    }
    private static void RejectIncomplete()
    {
        var session = Armed();
        Assert(!Pause(session, new(1, false, false, false, false)), "Missing completion cannot pause.");
        Assert(!Pause(session, new(1, true, false, false, true)), "Aborted attempt cannot pause.");
        Assert(!Pause(session, new(2, true, false, false, false)), "Invalid floorset cannot pause.");
    }
    private static void RetainsSession()
    {
        var session = Armed();
        var source = session.PrepareSource();
        source.Observe([new(0, false, 1, "Floor 1", "same-job")], out _, out _);
        Assert(Pause(session, Clear()), "Clear should pause.");
        Assert(session.OwnedSlot == 0 && session.NextFloor == 1 && session.Failures == 0 &&
            session.Discoveries == 0 && ReferenceEquals(source, session.Source) && session.PausedAfterFloor == 10,
            "Pause must retain the live session without advancing, deleting or counting.");
        Assert(!session.SetPauseAfterFloorsetClear(false) && session.IsPaused,
            "Changing a checkbox must not silently resume a paused task.");
    }
    private static void ResumeIsOneShot()
    {
        var session = Armed();
        Assert(!session.Resume(), "Cannot resume before pausing.");
        Assert(Pause(session, Clear()) && session.Resume() && !session.Resume(), "Resume must only consume one pause.");
        Assert(!Pause(session, Clear()), "Continuing must not immediately pause on the same result.");
        Assert(session.SetPauseAfterFloorsetClear(true) && Pause(session, Clear(11)), "A later group can be armed again.");
    }
    private static void ValidationSurvivesResume()
    {
        var session = Armed();
        session.PrepareSource().Observe([new(0, false, 1, "Floor 1", "same-job")], out _, out _);
        Pause(session, Clear()); session.Resume();
        session.CompleteAttempt(FarmingSessionPolicy.Decide(Plan, Clear()), 0);
        var source = session.PrepareSource();
        Assert(!source.Observe([new(1, false, 11, "Floor 11", "same-job")], out _, out _), "Other save slots remain forbidden.");
        Assert(!source.Observe([new(0, false, 21, "Floor 21", "same-job")], out _, out _), "Unexpected progression remains forbidden.");
        Assert(!source.Observe([new(0, false, 11, "Floor 11", "changed-job")], out _, out _), "Changed save identity remains forbidden.");
        Assert(source.Observe([new(0, false, 11, "Floor 11", "same-job")], out _, out _), "Resume must enter the expected owned save.");
    }
    private static void ExplicitStopWins() => Assert(!Pause(Armed(Plan with { StopAfterFloor = 10 }), Clear()),
        "An explicit diagnostic stop must not be changed into a resumable pause.");
    private static void RejectUnsupported()
    {
        var prepared = new FarmingSession(Plan with { Mode = FarmingMode.Aetherpool, SaveUse = SaveUse.Prepared });
        Assert(!prepared.SetPauseAfterFloorsetClear(true), "Early-abandon modes are outside this feature.");
        var unowned = new FarmingSession(Plan); unowned.SetPauseAfterFloorsetClear(true);
        Assert(!Pause(unowned, Clear()), "Unknown save ownership cannot be held as a valid clear.");
    }
    private static void HoardCleanupRequired()
    {
        var plan = Plan with { Mode = FarmingMode.Hoard, StartFloor = 21, BandedEnabled = true };
        var session = Armed(plan); var result = Clear(21); var decision = FarmingSessionPolicy.Decide(plan, result);
        Assert(decision.DeleteOwnedSave && !Pause(session, result) && !session.IsPaused && session.OwnedSlot == 0,
            "A completed hoard run must delete its owned save before a floorset pause.");
        Assert(!session.TryPauseAfterFloorsetClear(result, decision, true), "An occupied slot is not verified cleanup.");
        session.ConfirmOwnedSaveDeleted();
        Assert(!Pause(session, result), "A missing slot alone is not proof of this attempt's deletion.");
        Assert(session.TryPauseAfterFloorsetClear(result, decision, true), "Verified cleanup permits pause.");
        session.Resume(); session.CompleteAttempt(decision, 0);
        Assert(session.OwnedSlot == -1 && session.NextFloor == 21, "Continuing must preserve normal hoard cleanup policy.");
    }

    private static (FarmingScenario Scenario, RunContext Context, FarmingSession Session) BoundaryScenario(FarmingPlan? plan = null,
        int entryFloor = 1, IDeleteSaveFlow? cleanup = null, bool armPause = true)
    {
        var session = armPause ? Armed(plan) : new FarmingSession(plan ?? Plan, 0);
        var context = new RunContext(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        context.FarmingPlan = session.Plan;
        context.MarkDutyCompleted();
        var scenario = new FarmingScenario(session, deleteSaveFlow: cleanup);
        // Reproduce the actual boundary after the rest-exit flow has succeeded.
        SetField(scenario, "_context", context); SetField(scenario, "_entered", true);
        SetField(scenario, "_entryFloor", entryFloor); SetField(scenario, "_restDone", true);
        return (scenario, context, session);
    }
    private sealed class FakeCleanup : IDeleteSaveFlow
    {
        public int Prepared, Updates;
        public bool Ready, Fail;
        private RunContext? _context;
        public void Prepare(RunContext context) { Prepared++; _context = context; }
        public bool Update(IFramework framework)
        { Updates++; if (Fail) _context!.StatusIsError = true; return Ready || Fail; }
    }
    private static void HoardScenarioCleanup()
    {
        var cleanup = new FakeCleanup();
        var (scenario, context, session) = BoundaryScenario(Plan with { Mode = FarmingMode.Hoard, StartFloor = 31 }, 31, cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Assert(cleanup.Prepared == 1 && cleanup.Updates == 1 && !session.IsPaused && session.OwnedSlot == 0,
                "Cleanup must run while the pause request is still armed.");
            cleanup.Ready = true; scenario.Update(null!);
            Assert(session.IsPaused && session.OwnedSlot == -1 && !scenario.CountsAsCycle && !scenario.IsComplete,
                "Pause is allowed only after verified deletion, before counters and re-entry.");
            int calls = cleanup.Updates;
            for (int i = 0; i < 20; i++) scenario.Update(null!);
            Assert(cleanup.Updates == calls, "Paused ticks cannot delete another save.");
            session.Resume(); scenario.Update(null!); scenario.Update(null!);
            Assert(scenario.IsComplete && scenario.CountsAsCycle && cleanup.Updates == calls && cleanup.Prepared == 1,
                "Resume must finalize the same attempt once without re-running cleanup.");
        }
    }
    private static void CleanupFailure()
    {
        var cleanup = new FakeCleanup { Fail = true };
        var (scenario, context, session) = BoundaryScenario(Plan with { Mode = FarmingMode.Hoard, StartFloor = 21 }, 21, cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Assert(context.StatusIsError && scenario.IsComplete && !session.IsPaused && !scenario.CountsAsCycle && session.OwnedSlot == 0,
                "Failed cleanup must stop without pretending the slot is empty or the cycle complete.");
        }
    }
    private static void UnpausedCleanup()
    {
        var cleanup = new FakeCleanup { Ready = true };
        var (scenario, context, session) = BoundaryScenario(Plan with { Mode = FarmingMode.Hoard, StartFloor = 31 }, 31, cleanup, false);
        using (context) using (scenario)
        {
            scenario.Update(null!); scenario.Update(null!);
            Assert(scenario.IsComplete && scenario.CountsAsCycle && session.OwnedSlot == -1 && cleanup.Updates == 1,
                "Normal automatic runs must still delete once then count.");
        }
    }
    private static void TerminalCleanup()
    {
        var cleanup = new FakeCleanup { Ready = true };
        var (scenario, context, session) = BoundaryScenario(Plan, 91, cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Assert(session.IsPaused && session.OwnedSlot == -1 && cleanup.Updates == 1,
                "The verified terminal progression boundary must clean its owned save before pausing.");
            session.Resume(); scenario.Update(null!);
            Assert(scenario.CountsAsCycle && session.NextFloor == 1 && cleanup.Updates == 1, "Restart from one after finalization.");
        }
    }
    private static void RejectWrongSaveScenario()
    {
        foreach (var plan in new[] { Plan with { SaveUse = SaveUse.None }, Plan with { Mode = FarmingMode.FinalBoss, SaveUse = SaveUse.None, StartFloor = 99 } })
        {
            var cleanup = new FakeCleanup();
            using var context = new RunContext(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
            using var scenario = new FarmingScenario(new FarmingSession(plan), deleteSaveFlow: cleanup);
            scenario.Initialize(context);
            Assert(context.StatusIsError && scenario.IsComplete && !scenario.ShouldLoop && cleanup.Prepared == 0,
                "No-save practice cannot enter an ordinary save lifecycle.");
        }
    }
    private static void NoSaveScope()
    {
        foreach (var mode in Enum.GetValues<FarmingMode>().Where(m => m != FarmingMode.FinalBoss))
        {
            var decision = FarmingSessionPolicy.Decide(Plan with { Mode = mode, SaveUse = SaveUse.None }, Clear(91));
            Assert(!decision.CountCycle && !decision.DeleteOwnedSave && decision.Error.Length > 0,
                "No-save on ordinary modes must fail closed, not silently bypass cleanup.");
        }
        var practice = Plan with { Mode = FarmingMode.FinalBoss, SaveUse = SaveUse.None, StartFloor = 99 };
        var valid = FarmingSessionPolicy.Decide(practice, Clear(99));
        Assert(valid.CountCycle && !valid.DeleteOwnedSave && valid.Error.Length == 0, "Only real standalone practice gets the exemption.");
        foreach (var save in new[] { SaveUse.Create, SaveUse.Prepared })
            Assert(FarmingSessionPolicy.Decide(practice with { SaveUse = save }, Clear(99)).Error.Length > 0, "Wrong save type must stop.");
    }
    private static void FinalBossScenarioScope()
    {
        foreach (var plan in new[] { Plan, Plan with { Mode = FarmingMode.FinalBoss, StartFloor = 99 },
            Plan with { Mode = FarmingMode.FinalBoss, SaveUse = SaveUse.None, StartFloor = 91 } })
        {
            using var context = new RunContext(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
            using var scenario = new FinalBossScenario(plan);
            scenario.Initialize(context);
            Assert(context.StatusIsError && scenario.IsComplete && !scenario.CountsAsCycle,
                "Ordinary/save-bearing plans must never use the save-free completion path.");
        }
    }
    private static void CheckpointScenarioStops()
    {
        var plan = Plan with { StartFloor = 31, StopAfterFloor = 50, UsesDiagnosticCheckpoints = true };
        var (scenario, context, session) = BoundaryScenario(plan, 41);
        using (context) using (scenario)
        {
            session.CompleteAttempt(FarmingSessionPolicy.Decide(plan, Clear(31)), 0);
            Assert(session.NextFloor == 41, "First group must continue to 41.");
            scenario.Update(null!);
            for (int i = 0; i < 100; i++) scenario.Update(null!);
            Assert(scenario.IsComplete && !scenario.ShouldLoop && !scenario.CountsAsCycle && !session.IsPaused,
                "Explicit stop must finish the host rather than re-enter or offer temporary resume.");
            Assert(session.NextFloor == 41 && session.OwnedSlot == 0 && context.StatusLine.Contains("50"),
                "Successful 50 boundary must preserve save and not advance to 51.");
            Assert(typeof(FarmingScenario).GetField("_delete", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario) == null,
                "Stop must precede save deletion.");
        }
    }

    private static void ScenarioHoldsAndResumes()
    {
        var (scenario, context, session) = BoundaryScenario();
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Assert(session.IsPaused && !scenario.IsComplete && !scenario.CountsAsCycle, "The host must not see a completed attempt while held.");
            for (int i = 0; i < 100; i++) scenario.Update(null!);
            Assert(session.IsPaused && session.NextFloor == 1 && session.OwnedSlot == 0, "Idle ticks must not re-enter or delete the save.");
            session.Resume(); scenario.Update(null!);
            Assert(scenario.IsComplete && session.NextFloor == 11 && session.OwnedSlot == 0 && !scenario.CountsAsCycle,
                "Continue must complete the same attempt exactly once and advance to 11.");
            scenario.Update(null!);
            Assert(session.NextFloor == 11, "A repeated tick cannot advance another group.");
        }
    }
    private static void ScenarioWaitsForTransition()
    {
        var (scenario, context, session) = BoundaryScenario();
        using (context) using (scenario)
        {
            typeof(DutyState).GetProperty(nameof(DutyState.IsTransitioning))!.SetValue(context.Duty, true);
            scenario.Update(null!);
            Assert(!session.IsPaused && session.PauseAfterFloorsetClear && !scenario.IsComplete, "Loading must finish before entering pause.");
            context.Duty.MarkOutsideDuty(); scenario.Update(null!);
            Assert(session.IsPaused, "The completed outside boundary must then pause.");
        }
    }
    private static void LaterFloorset()
    {
        var session = Armed();
        for (int floor = 1; floor < 91; floor += 10)
        {
            var result = Clear(floor);
            Assert(Pause(session, result) && session.PausedAfterFloor == floor + 9, "Each armed floorset must pause at its own boundary.");
            session.Resume(); session.CompleteAttempt(FarmingSessionPolicy.Decide(Plan, result), 0);
            Assert(session.NextFloor == floor + 10 && session.OwnedSlot == 0, "Continuation must retain the original save through 90.");
            session.SetPauseAfterFloorsetClear(true);
        }
    }
    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
