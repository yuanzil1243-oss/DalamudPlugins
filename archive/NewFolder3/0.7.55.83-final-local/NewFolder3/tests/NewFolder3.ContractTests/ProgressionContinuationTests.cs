using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class ProgressionContinuationTests
{
    public static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("progression-ten-floorsets-use-one-owned-save", FullProgressionRetainsSave),
            ("target100-completes-one-cycle-instead-of-diagnostic-stop", Target100CountsCycle),
            ("legacy-ui-hold-on-failure-no-longer-overrides-retry", LegacyHoldSetting),
            ("checkpoint-31-to-50-retains-save-and-stops-at-50", CheckpointRangeRetainsSave),
            ("free-exploration-resume-preserves-existing-save-on-success-and-failure", ExistingSavePreserved),
            ("checkpoint-failure-restarts-nearby-with-fresh-binding", CheckpointFailureResetsBinding),
            ("one-cycle-does-not-stop-at-first-ten-floors", OneCycleMeansSummit),
            ("in-place-resume-is-consumed-after-success", ResumeConsumedAfterSuccess),
            ("in-place-resume-is-consumed-after-failure", ResumeConsumedAfterFailure),
            ("progression-missing-owned-save-cannot-select-other-slot", MissingOwnedSaveStops),
            ("progression-wrong-next-floor-still-stops", WrongFloorStops),
            ("progression-changed-identity-still-stops", ChangedIdentityStops),
            ("progression-diagnostic-stop-boundary-is-preserved", DiagnosticBoundaryPreserved),
            ("hoard-cycle-still-deletes-and-restarts", HoardCyclePreserved),
            ("prepared-entry-allows-first-npc-interaction", FirstNpcInteraction),
            ("prepared-entry-waits-for-npc-response", NpcResponseGrace),
            ("prepared-entry-retries-lost-menu-request", LostMenuRequestRetries),
            ("prepared-entry-menu-retry-count-is-bounded", MenuRetriesBounded),
            ("prepared-entry-commence-blocks-further-entry-requests", CommenceBlocksRequests),
            ("prepared-entry-retry-state-resets-per-attempt", RetryReset)
        ];
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"  ok  {name}");
        }
        return tests.Length;
    }

    private static FarmingPlan Plan => new(FarmingMode.DeepProgression, SaveUse.Create,
        1, 1, false, false, true, true, false);
    private static void Target100CountsCycle()
    {
        foreach (var mode in new[] { FarmingMode.DeepProgression, FarmingMode.FreeExploration })
        {
            var plan = Plan with { Mode = mode, StopAfterFloor = 100 };
            var result = new FarmingAttemptResult(91, true, false, false, false);
            Assert(!FarmingSessionPolicy.ReachedStopBoundary(plan, result), "100 must use normal reward/exit/completion path");
            var decision = FarmingSessionPolicy.Decide(plan, result);
            Assert(decision.CountCycle && decision.Error.Length == 0, "count complete climb");
        }
    }
    private static void LegacyHoldSetting()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<CheckpointEntrySettings>("{\"Enabled\":true,\"HoldOnFailure\":true}")!;
        var request = settings.CreateRequest(FarmingMode.FreeExploration, FsdStartScenario.Farming);
        Assert(typeof(FsdStartRequest).GetProperty("HoldOnFailure") == null && typeof(FarmingPlan).GetProperty("HoldOnFailure") == null, "removed hold must not be reactivated through API or persisted state");
        var decision = FarmingSessionPolicy.Decide(Plan, new(91, false, true, false, false));
        Assert(decision.RecoverFailure && decision.DeleteOwnedSave && decision.NextFloor == 1, "failed necromancer attempt retries from one");
    }
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static SaveSlotSnapshot Save(int floor, int index = 0, string identity = "same-character-job") =>
        new(index, false, floor, $"Floor {floor}", identity);
    private static FarmingSessionDecision Clear(FarmingPlan plan, int floor) =>
        FarmingSessionPolicy.Decide(plan, new(floor, true, false, false, false));

    private static void CheckpointRangeRetainsSave()
    {
        var plan = Plan with { Mode = FarmingMode.FreeExploration, StartFloor = 31, StopAfterFloor = 50, UsesDiagnosticCheckpoints = true };
        var session = new FarmingSession(plan);
        Assert(session.NextFloor == 31 && session.BindCreatedSave(0), "Start from selected checkpoint.");
        Assert(session.PrepareSource().Observe([Save(31)], out _, out _), "Pin newly created save.");
        var first = new FarmingAttemptResult(31, true, false, false, false);
        Assert(!FarmingSessionPolicy.ReachedStopBoundary(plan, first), "40 must continue.");
        var decision = FarmingSessionPolicy.Decide(plan, first);
        Assert(!decision.CountCycle && !decision.DeleteOwnedSave && decision.NextFloor == 41, "Retain source for 41.");
        session.CompleteAttempt(decision, 0);
        Assert(session.PrepareSource().Observe([Save(41), Save(31, 1, "unrelated")], out var source, out _)
            && source.Index == 0, "Continuation must retain original slot/identity.");
        Assert(!session.PrepareSource().Observe([Save(41, 1, "unrelated")], out _, out _), "Never fall through to another save.");
        var second = new FarmingAttemptResult(41, true, false, false, false);
        Assert(FarmingSessionPolicy.ReachedStopBoundary(plan, second), "Stop after clear 50 before cleanup/reentry.");
        session.SetPauseAfterFloorsetClear(true);
        Assert(!session.TryPauseAfterFloorsetClear(second, FarmingSessionPolicy.Decide(plan, second)), "Stop boundary wins over temporary pause.");
        Assert(session.OwnedSlot == 0 && session.Source.Pinned?.StartFloor == 41, "Boundary checking must preserve owned save.");
    }

    private static void CheckpointFailureResetsBinding()
    {
        var plan = Plan with { Mode = FarmingMode.FreeExploration, StartFloor = 31, StopAfterFloor = 50, UsesDiagnosticCheckpoints = true };
        var session = new FarmingSession(plan);
        session.BindCreatedSave(0);
        Assert(session.PrepareSource().Observe([Save(31)], out _, out _), "Pin source.");
        var failure = new FarmingAttemptResult(41, false, true, false, false);
        Assert(!FarmingSessionPolicy.ReachedStopBoundary(plan, failure), "Death at 50 is not successful completion.");
        var decision = FarmingSessionPolicy.Decide(plan, failure);
        Assert(decision.RecoverFailure && decision.DeleteOwnedSave && decision.NextFloor == 31 && !decision.CountCycle, "Restart 31.");
        session.ConfirmOwnedSaveDeleted();
        session.CompleteAttempt(decision, 0);
        Assert(session.NextFloor == 31 && session.OwnedSlot == -1 && session.Source.Pinned == null && session.Failures == 1,
            "New attempt must not inherit stale save binding.");
    }

    private static void ExistingSavePreserved()
    {
        var plan = Plan with
        {
            Mode = FarmingMode.FreeExploration,
            StartFloor = 71,
            StopAfterFloor = 100,
            UsesDiagnosticCheckpoints = true,
            PreserveExternalSave = true,
            PreserveSaveOnCompletion = true,
            DeleteExternalSaveOnFailure = true
        };
        var failure = FarmingSessionPolicy.Decide(plan, new(71, false, true, false, false));
        Assert(failure.DeleteOwnedSave && failure.RecoverFailure && failure.NextFloor == 71,
            "Failed continuation must delete the external save and retry from the failed floorset start.");
        var clear = FarmingSessionPolicy.Decide(plan, new(91, true, false, false, false));
        Assert(clear.CountCycle && !clear.DeleteOwnedSave && clear.NextFloor == 71,
            "Successful test completion must preserve the external save.");
        var session = new FarmingSession(plan, ownedSlot: 0);
        session.CompleteAttempt(failure, 0);
        Assert(!session.Plan.PreserveExternalSave && !session.Plan.DeleteExternalSaveOnFailure,
            "After deleting the external save, subsequent retries must manage their newly created save normally.");
    }

    private static void FullProgressionRetainsSave()
    {
        var plan = Plan;
        var session = new FarmingSession(plan);
        Assert(session.BindCreatedSave(0), "New save must bind to the task.");
        for (int floor = 1; floor < 91; floor += 10)
        {
            Assert(session.NextFloor == floor, "Unexpected attempt start.");
            var decision = Clear(plan, floor);
            Assert(!decision.CountCycle && !decision.DeleteOwnedSave && decision.Error.Length == 0,
                "An intermediate clear must neither finish the climb nor delete its save.");
            session.CompleteAttempt(decision, 0);
            var source = session.PrepareSource();
            Assert(source.Observe([Save(floor + 10), Save(21, 1, "unrelated-save")], out var selected, out _),
                "The same owned save must advance through each successive floorset.");
            Assert(selected.Index == 0 && selected.StartFloor == floor + 10,
                "Continuation must select only the expected owned slot and next floor.");
        }
        var summit = Clear(plan, 91);
        Assert(summit.CountCycle && summit.DeleteOwnedSave && summit.NextFloor == 1,
            "Only the summit finishes a normal climb.");
        session.ConfirmOwnedSaveDeleted();
        session.CompleteAttempt(summit, 0);
        Assert(session.OwnedSlot == -1 && session.Source.Pinned == null && session.NextFloor == 1,
            "A completed climb must prepare a fresh save, without retaining an old source.");
    }

    private static void OneCycleMeansSummit()
    {
        var loops = new MultiLoopDriver(1, false);
        for (int floor = 1; floor < 91; floor += 10)
        {
            var decision = Clear(Plan, floor);
            Assert(!decision.CountCycle, "Intermediate floorsets must not reach the single-cycle target.");
        }
        Assert(loops.CompletedLoops == 0 && Clear(Plan, 91).CountCycle && loops.ShouldStopAfterCurrentRun(),
            "A one-cycle target ends at the summit.");
    }

    private static void ResumeConsumedAfterSuccess()
    {
        var session = new FarmingSession(Plan, 0, resumeCurrentDuty: true);
        Assert(session.ResumeCurrentDuty, "The first attached attempt must resume in place.");
        session.CompleteAttempt(Clear(Plan, 1), 0);
        Assert(!session.ResumeCurrentDuty && session.NextFloor == 11 && session.OwnedSlot == 0,
            "The next attempt must re-enter its owned save, not pretend it is already inside.");
    }

    private static void ResumeConsumedAfterFailure()
    {
        var plan = Plan with { StartFloor = 21, UsesDiagnosticCheckpoints = true };
        var session = new FarmingSession(plan, 0, resumeCurrentDuty: true);
        var decision = FarmingSessionPolicy.Decide(plan, new(21, false, true, false, false));
        Assert(decision.RecoverFailure && decision.DeleteOwnedSave, "A failure must retain its cleanup requirements.");
        session.ConfirmOwnedSaveDeleted();
        session.CompleteAttempt(decision, 0);
        Assert(!session.ResumeCurrentDuty && session.Failures == 1 && session.OwnedSlot == -1,
            "Failure recovery must create an entry attempt, never reuse an in-place resume marker.");
    }

    private static FarmingSession ContinuedSession()
    {
        var session = new FarmingSession(Plan, 0);
        session.CompleteAttempt(Clear(Plan, 1), 0);
        return session;
    }

    private static void MissingOwnedSaveStops()
    {
        var source = ContinuedSession().PrepareSource();
        Assert(!source.Observe([new(0, true, 0, "Empty"), Save(11, 1)], out _, out _),
            "An unrelated slot must never replace a missing owned save.");
    }

    private static void WrongFloorStops()
    {
        Assert(!ContinuedSession().PrepareSource().Observe([Save(21)], out _, out _),
            "Skipping or repeating an unexpected floorset must still stop.");
    }

    private static void ChangedIdentityStops()
    {
        var session = ContinuedSession();
        Assert(session.PrepareSource().Observe([Save(11)], out _, out _), "Pin original source.");
        session.CompleteAttempt(Clear(Plan, 11), 0);
        Assert(!session.PrepareSource().Observe([Save(21, identity: "replaced-character-job")], out _, out _),
            "Next-floor advancement must not authorize changed source identity.");
    }

    private static void DiagnosticBoundaryPreserved()
    {
        var plan = Plan with { StopAfterFloor = 10 };
        Assert(FarmingSessionPolicy.ReachedStopBoundary(plan, new(1, true, false, false, false)),
            "An explicit ten-floor review boundary must still stop.");
        Assert(!FarmingSessionPolicy.ReachedStopBoundary(Plan, new(1, true, false, false, false)),
            "A normal climb has no implicit ten-floor review boundary.");
        var held = FarmingSessionPolicy.Decide(plan, new(1, false, true, false, false));
        Assert(held.DeleteOwnedSave && held.RecoverFailure && !held.CountCycle, "An explicit success boundary must not preserve a failed save.");
    }

    private static void HoardCyclePreserved()
    {
        var plan = new FarmingPlan(FarmingMode.Hoard, SaveUse.Create, 21, 1, true, true, true, true, false);
        var decision = Clear(plan, 21);
        Assert(decision.CountCycle && decision.DeleteOwnedSave && decision.NextFloor == 21,
            "The already tested hoard loop must retain its delete-and-create policy.");
    }

    private static void FirstNpcInteraction() =>
        Assert(new PreparedEntryRetryState().CanInteractNpc(Now), "Entry must not delay its first interaction.");

    private static void NpcResponseGrace()
    {
        var state = new PreparedEntryRetryState();
        state.RecordNpcInteraction(Now);
        for (int ms = 350; ms < 2500; ms += 350)
            Assert(!state.CanInteractNpc(Now.AddMilliseconds(ms)), "350 ms ticks must not spam NPC interactions.");
        Assert(state.CanInteractNpc(Now + PreparedEntryRetryState.NpcResponseGrace), "A missing response must allow a later retry.");
    }

    private static void LostMenuRequestRetries()
    {
        var state = new PreparedEntryRetryState();
        Assert(state.DecideMenu(Now) == PreparedEntryMenuAction.Request, "First request must be permitted.");
        state.RecordMenuRequest(Now);
        Assert(state.DecideMenu(Now.AddMilliseconds(350)) == PreparedEntryMenuAction.Wait, "Wait for asynchronous menu response.");
        Assert(state.DecideMenu(Now.AddSeconds(2)) == PreparedEntryMenuAction.Request, "A lost opening request must be retried.");
    }

    private static void MenuRetriesBounded()
    {
        var state = new PreparedEntryRetryState();
        for (int i = 0; i < PreparedEntryRetryState.MaximumMenuRequests; i++)
        {
            var now = Now.AddSeconds(i * 2);
            Assert(state.DecideMenu(now) == PreparedEntryMenuAction.Request, "Expected a bounded retry.");
            state.RecordMenuRequest(now);
        }
        Assert(state.DecideMenu(Now.AddMilliseconds(5999)) == PreparedEntryMenuAction.Wait,
            "The final request must get its full response grace.");
        Assert(state.DecideMenu(Now.AddSeconds(6)) == PreparedEntryMenuAction.Stop && state.MenuRequests == 3,
            "An unresponsive menu must stop with a finite retry count.");
    }

    private static void CommenceBlocksRequests()
    {
        var state = new PreparedEntryRetryState();
        state.RecordCommence();
        Assert(state.CommenceDispatched && !state.CanInteractNpc(Now.AddMinutes(1)) &&
            state.DecideMenu(Now.AddMinutes(1)) == PreparedEntryMenuAction.Wait,
            "A dispatched Commence must wait for loading, without repeating entry actions.");
    }

    private static void RetryReset()
    {
        var state = new PreparedEntryRetryState();
        state.RecordNpcInteraction(Now);
        state.RecordMenuRequest(Now);
        state.RecordCommence();
        state.Reset();
        Assert(state.MenuRequests == 0 && !state.CommenceDispatched && state.CanInteractNpc(Now) &&
            state.DecideMenu(Now) == PreparedEntryMenuAction.Request,
            "Every new entry attempt needs fresh retry and confirmation state.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
