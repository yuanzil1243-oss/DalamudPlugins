using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.GameState;

namespace NewFolder3;

internal static class FailedAttemptRecoveryTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  failed-result-" + name); }
        var evidence = new FailedResultFacts(true, true, false, true, true, false, false);
        Check("dead-native-scoreboard-does-not-require-wipe-count", () => Assert(FailedAttemptRecoveryPolicy.ShouldObserveFailure(evidence)));
        foreach (var e in new[] { evidence with { Entered = false }, evidence with { SameAttempt = false },
            evidence with { Transitioning = true }, evidence with { PlayerDead = false },
            evidence with { ResultRecognized = false }, evidence with { Completed = true }, evidence with { Failed = true } })
            Check("reject-incomplete-stale-alive-success-or-duplicate-" + count,
                () => Assert(!FailedAttemptRecoveryPolicy.ShouldObserveFailure(e)));
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            foreach (string mode in new[] { "progression", "ordinary", "free" })
                Check("native-enterable-save-retried-" + dungeon + "-" + mode, () =>
                {
                    int start = mode == "progression" ? 1 : OrdinaryDescentPolicy.HighestStartFloor(dungeon);
                    var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, start, 3, false, false, true, true, false)
                        { DungeonId = dungeon, OrdinaryDescent = mode == "ordinary", FreeExploration = mode == "free", EndFloor = mode == "free" ? start + 19 : null };
                    int entry = start + 10;
                    var original = Slot(0, entry);
                    var source = Slot(0, entry) with { Progress = "native retry available; failure count unchanged" };
                    var rows = new[] { source, Slot(1, 1) };
                    Assert(FailedAttemptRecoveryPolicy.TryResolve(rows, 0, entry, original, out var verified, out _));
                    var decision = FarmingSessionPolicy.Decide(plan, new(entry, false, true, false, false));
                    var retry = FailedAttemptRecoveryPolicy.Reconcile(plan, decision, verified);
                    Assert(retry is { RecoverFailure: true, CountCycle: false, DeleteOwnedSave: false, Error.Length: 0 } && retry.NextFloor == entry);
                    var session = new FarmingSession(plan, 0);
                    session.BindFailedSaveCheckpoint(verified); session.CompleteAttempt(retry, 0);
                    Assert(session.OwnedSlot == 0 && session.NextFloor == entry && session.Source.Pinned == source);
                    var prepared = session.PrepareSource();
                    Assert(prepared.Observe(rows, out var after, out _) && after == source);
                    Assert(!prepared.Observe(new[] { source with { Identity = "changed" }, rows[1] }, out _, out _));
                    var loops = new MultiLoopDriver(3, false); Assert(loops.CompletedLoops == 0);
                    var invalid = source with { Enterable = false };
                    Assert(FailedAttemptRecoveryPolicy.TryResolve(new[] { invalid, rows[1] }, 0, entry, source, out verified, out _));
                    var restart = FailedAttemptRecoveryPolicy.Reconcile(plan, decision, verified);
                    Assert(restart.DeleteOwnedSave && restart.RecoverFailure && !restart.CountCycle && restart.NextFloor == start);
                });
            Check("retention-cannot-be-overridden-" + dungeon, () =>
            {
                var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false, false, true, true, false)
                    { DungeonId = dungeon, KeepSave = true };
                var decision = FarmingSessionPolicy.Decide(plan, new(11, false, true, false, false));
                Assert(FailedAttemptRecoveryPolicy.Reconcile(plan, decision, Slot(0, 11)) == decision);
                Assert(decision.StopAfterAttempt && !decision.DeleteOwnedSave && !decision.RecoverFailure);
            });
            Check("relic-retries-enterable-save-" + dungeon, () =>
            {
                int floor = dungeon == 1 ? 51 : 21;
                Assert(RelicFarmingPolicy.TryCreate(dungeon, SaveUse.Create, floor, 3, false,
                    new(false, true, true, false), out var plan, out _));
                var decision = FarmingSessionPolicy.Decide(plan!, new(floor, false, true, false, false));
                var retry = FailedAttemptRecoveryPolicy.Reconcile(plan!, decision, Slot(0, floor));
                Assert(retry.RecoverFailure && !retry.DeleteOwnedSave && !retry.CountCycle && retry.NextFloor == floor);
                var restart = FailedAttemptRecoveryPolicy.Reconcile(plan!, decision, Slot(0, floor) with { Enterable = false });
                Assert(restart.RecoverFailure && restart.DeleteOwnedSave && !restart.CountCycle && restart.NextFloor == floor);
            });
        }
        var source0 = Slot(0, 11); var source1 = Slot(1, 11);
        foreach (var rows in new[] { new[] { source0 }, new[] { source0, source0 },
            new[] { source0 with { Empty = true }, source1 }, new[] { source0 with { Identity = "other" }, source1 },
            new[] { source0 with { RowFingerprint = null }, source1 }, new[] { source0 with { StartFloor = 1 }, source1 },
            new[] { source0 with { StartFloor = 21 }, source1 } })
            Check("reject-unknown-or-changed-save-" + count,
                () => Assert(!FailedAttemptRecoveryPolicy.TryResolve(rows, 0, 11, source0, out _, out _)));
        Check("failed-source-binding-is-read-only", () =>
        {
            var binding = new PreparedSaveBinding(requiredSlot: 0) { FailedAttemptFloor = 11 };
            binding.PinCheckpoint(source0);
            var invalid = source0 with { Enterable = false };
            Assert(binding.Observe(new[] { invalid, source1 }, out var read, out _) && read == invalid);
            bool refused = false;
            try { _ = new DungeonEntryController(DungeonCatalog.PilgrimsTraverse, 11, prepared: binding); }
            catch (ArgumentException) { refused = true; }
            Assert(refused);
            _ = new DungeonEntryController(DungeonCatalog.PilgrimsTraverse, 11, prepared: binding, verifyOnly: true);
        });
        return count;
    }
    private static SaveSlotSnapshot Slot(int index, int floor) => new(index, false, floor, floor + "F", "identity-" + index)
        { RowFingerprint = new string((char)('a' + index), 64) };
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Failed result recovery contract"); }
}
