using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class FailureRestartAuditTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        { test(); count++; Console.WriteLine("  ok  failure-restart-" + name); }
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            Check("progression-midway-" + dungeon, () =>
            {
                var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 3, false, false, true, true, false)
                    { DungeonId = dungeon };
                var failure = FarmingSessionPolicy.Decide(plan, new(31, false, true, false, false));
                Assert(failure is { CountCycle: false, NextFloor: 1, DeleteOwnedSave: true, RecoverFailure: true, Error.Length: 0 });
                var session = new FarmingSession(plan, 0); session.CompleteAttempt(failure, 0);
                Assert(session.NextFloor == 1 && session.Failures == 1);
                session.ConfirmOwnedSaveDeleted(); Assert(session.OwnedSlot == -1);
            });
            Check("free-midway-start21-target50-" + dungeon, () =>
            {
                int start = dungeon == 1 ? 51 : 21, end = start + 29;
                Assert(FreeExplorationPolicy.TryCreate(dungeon, start, end, 3, false, default, false, false, out var plan, out _));
                var failure = FarmingSessionPolicy.Decide(plan!, new(start + 10, false, true, false, false));
                Assert(failure is { CountCycle: false, DeleteOwnedSave: true, RecoverFailure: true, Error.Length: 0 } && failure.NextFloor == start);
                var session = new FarmingSession(plan!, 1); session.CompleteAttempt(failure, 0);
                Assert(session.NextFloor == start && session.Failures == 1);
            });
            Check("external-failure-never-deletes-or-restarts-" + dungeon, () =>
            {
                var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false, false, true, true, false)
                    { DungeonId = dungeon, ExternalSave = true };
                var failure = FarmingSessionPolicy.Decide(plan, new(31, false, true, false, false));
                Assert(!failure.CountCycle && !failure.DeleteOwnedSave && !failure.RecoverFailure && failure.Error.Length > 0);
            });
            Check("ordinary-descent-restarts-highest-new-save-start-" + dungeon, () =>
            {
                int highest = OrdinaryDescentPolicy.HighestStartFloor(dungeon);
                var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, highest, 3, false, false, true, true, false)
                    { DungeonId = dungeon, OrdinaryDescent = true };
                var failure = FarmingSessionPolicy.Decide(plan, new(highest + 10, false, true, false, false));
                Assert(failure is { CountCycle: false, DeleteOwnedSave: true, RecoverFailure: true, Error.Length: 0 } && failure.NextFloor == highest);
            });
            foreach (string mode in new[] { "progression", "ordinary", "free" })
                Check("mixed-failures-until-three-successes-" + dungeon + "-" + mode, () =>
                {
                    int highest = OrdinaryDescentPolicy.HighestStartFloor(dungeon);
                    int start = mode == "progression" ? 1 : highest;
                    var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, start, 3, false, false, true, true, false)
                        { DungeonId = dungeon, OrdinaryDescent = mode == "ordinary" };
                    if (mode == "free")
                    {
                        Assert(FreeExplorationPolicy.TryCreate(dungeon, start, start + 19, 3, false, default, false, false, out var free, out _));
                        plan = free!;
                    }
                    var session = new FarmingSession(plan, 0);
                    var loops = new MultiLoopDriver(3, false);
                    int endEntry = (plan.EndFloor ?? plan.MaxFloor) - 9;
                    for (int success = 0; success < 3; success++)
                    {
                        for (int fail = 0; fail < 2; fail++)
                        {
                            var failure = FarmingSessionPolicy.Decide(plan, new(start + 10, false, true, false, false));
                            Assert(failure.RecoverFailure && !failure.CountCycle && failure.DeleteOwnedSave && failure.Error.Length == 0);
                            session.ConfirmOwnedSaveDeleted(); session.CompleteAttempt(failure, 0);
                            Assert(session.NextFloor == start && loops.CompletedLoops == success);
                            Assert(session.BindCreatedSave(0));
                        }
                        while (session.NextFloor <= endEntry)
                        {
                            var decision = FarmingSessionPolicy.Decide(plan, new(session.NextFloor, true, false, false, false));
                            Assert(decision.Error.Length == 0 && !decision.RecoverFailure);
                            session.CompleteAttempt(decision, 0);
                            if (!decision.CountCycle) continue;
                            Assert(decision.DeleteOwnedSave && loops.ShouldStopAfterCurrentRun() == (success == 2));
                            session.ConfirmOwnedSaveDeleted();
                            loops.IncrementLoop(); // Success only; failure never touches the loop counter.
                            break;
                        }
                    }
                    Assert(loops.CompletedLoops == 3 && session.Failures == 6 && session.NextFloor == start);
                });
        }
        Check("diagnostic-hold-does-not-clear-save", () =>
        {
            var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false, false, true, true, false)
                { HoldOnFailure = true };
            var failure = FarmingSessionPolicy.Decide(plan, new(11, false, true, false, false));
            Assert(!failure.DeleteOwnedSave && !failure.RecoverFailure && failure.Error.Length > 0);
        });
        Check("stop-target-is-not-a-failure-boundary", () =>
        {
            var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false, false, true, true, false)
                { StopAfterFloor = 30 };
            var result = new FarmingAttemptResult(21, false, true, false, false);
            Assert(!FarmingSessionPolicy.ReachedStopBoundary(plan, result));
            Assert(FarmingSessionPolicy.Decide(plan, result).RecoverFailure);
        });
        Check("ambiguous-exit-does-not-trigger-destructive-retry", () =>
        {
            FreeExplorationPolicy.TryCreate(4, 21, 50, 3, false, default, false, false, out var plan, out _);
            var unknown = FarmingSessionPolicy.Decide(plan!, new(31, false, false, false, false));
            Assert(!unknown.DeleteOwnedSave && !unknown.RecoverFailure && unknown.Error.Length > 0);
        });
        return count;
    }
    private static void Assert(bool condition)
    { if (!condition) throw new InvalidOperationException("Failure restart audit contract failed."); }
}
