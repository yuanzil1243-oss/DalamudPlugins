using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace NewFolder3;

internal static class ModeContinuationTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  mode-continuation-" + name); }
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            int max = dungeon == 1 ? 200 : 100;
            foreach (string mode in new[] { "progression", "ordinary", "free", "relic" })
                foreach (bool keep in new[] { false, true })
                    Check($"plan-and-first-attached-attempt-{dungeon}-{mode}-{keep}", () =>
                    {
                        var settings = Settings(dungeon, mode);
                        var p = settings.Farming.GetPreferences(settings); p.Cycles = 3; p.Infinite = true; p.KeepSave = keep;
                        int group = mode == "relic" ? AetherpoolExchangePolicy.GetDungeon(dungeon)!.StartFloor : 11;
                        var plan = Create(settings, group);
                        Assert(plan.ManagedContinuation && plan.ExternalSave && !plan.CurrentDutyOnly && plan.ContinuationStartFloor == group);
                        Assert(plan.KeepSave == keep && plan.TargetCycles == (keep ? 1 : 3) && plan.Infinite == !keep);
                        int restart = mode == "progression" ? 1 : mode == "ordinary" ? OrdinaryDescentPolicy.HighestStartFloor(dungeon)
                            : mode == "relic" ? group : TaskContinuationPolicy.HighestStartAtOrBelow(dungeon, max);
                        Assert(plan.StartFloor == restart);
                        var session = new FarmingSession(plan); session.AttachCurrentDuty(group);
                        Assert(session.NextFloor == group && session.OwnedSlot == -1 && session.PendingDutyAttachment);
                        session.ConfirmDutyAttachment(); Assert(!session.PendingDutyAttachment && session.NeedsContinuationSource);
                        var slot = Slot(1, group + 10);
                        session.BindContinuationSource(slot);
                        Assert(!session.NeedsContinuationSource && session.OwnedSlot == 1 && session.Source.Pinned == slot);
                        var success = FarmingSessionPolicy.Decide(plan, new(mode == "relic" ? group : max - 9, true, false, false, false));
                        Assert(success.CountCycle && success.Error.Length == 0 && !success.RecoverFailure);
                        Assert(success.DeleteOwnedSave == !keep && success.StopAfterAttempt == keep);
                        if (!keep) Assert(success.NextFloor == restart);
                        var failure = FarmingSessionPolicy.Decide(plan, new(group, false, true, false, false));
                        Assert(!failure.CountCycle && failure.DeleteOwnedSave == !keep && failure.RecoverFailure == !keep);
                        Assert(keep ? failure.Error.Length > 0 : failure.Error.Length == 0 && failure.NextFloor == restart);
                        Assert(FloorsetPausePolicy.Supports(plan));
                    });

            Check("every-free-target-restarts-highest-legal-" + dungeon, () =>
            {
                for (int end = 10; end <= max; end += 10)
                {
                    var settings = Settings(dungeon, "free"); settings.Farming.GetFreeExplorationPreferences().EndFloor = end;
                    var plan = Create(settings, 1);
                    Assert(plan.StartFloor == FreeExplorationPolicy.NewSaveStarts(dungeon).Last(x => x < end));
                    Assert(FarmingSessionPolicy.Decide(plan, new(1, true, false, false, false)).Error.Length == 0);
                }
            });
            Check("three-successful-rounds-after-current-partial-" + dungeon, () =>
            {
                foreach (string mode in new[] { "progression", "ordinary", "free" })
                {
                    var settings = Settings(dungeon, mode); var p = settings.Farming.GetPreferences(settings); p.Cycles = 3;
                    var plan = Create(settings, 31); var session = new FarmingSession(plan);
                    session.AttachCurrentDuty(31); session.ConfirmDutyAttachment(); session.BindContinuationSource(Slot(0, 41));
                    var loops = new MultiLoopDriver(3, false);
                    for (int round = 0; round < 3; round++)
                    {
                        while (true)
                        {
                            var decision = FarmingSessionPolicy.Decide(plan, new(session.NextFloor, true, false, false, false));
                            Assert(decision.Error.Length == 0);
                            session.CompleteAttempt(decision, 0);
                            if (!decision.CountCycle) continue;
                            Assert(decision.DeleteOwnedSave && !decision.StopAfterAttempt);
                            Assert(loops.ShouldStopAfterCurrentRun() == (round == 2)); loops.IncrementLoop();
                            session.ConfirmOwnedSaveDeleted(); Assert(session.NextFloor == plan.StartFloor);
                            if (round != 2) Assert(session.BindCreatedSave(0));
                            break;
                        }
                    }
                    Assert(loops.CompletedLoops == 3);
                }
            });
            Check("terminal-switch-and-retention-are-independent-" + dungeon, () =>
            {
                var settings = Settings(dungeon, "free"); var p = settings.Farming.GetPreferences(settings); p.KeepSave = true;
                var plan = Create(settings, max - 9);
                var receipt = new TerminalRewardExitFacts { Enabled = true, StayRequested = true, RoomObserved = true,
                    DutyCompleted = true, InDuty = true, PlayerReady = true, DungeonId = dungeon, Floor = max };
                Assert(TerminalRewardExitPolicy.ShouldContinue(plan, receipt));
                Assert(!TerminalRewardExitPolicy.ShouldContinue(plan, receipt with { Enabled = false }));
                var done = FarmingSessionPolicy.Decide(plan, new(max - 9, true, false, false, false));
                Assert(done.CountCycle && done.StopAfterAttempt && !done.DeleteOwnedSave);
                settings.Farming.GetFreeExplorationPreferences().EndFloor = 50;
                plan = Create(settings, 21);
                done = FarmingSessionPolicy.Decide(plan, new(41, true, false, false, false));
                Assert(done.CountCycle && done.StopAfterAttempt && !done.DeleteOwnedSave); // No terminal switch involved.
            });
        }
        Check("fixed-mode-floor-guards", () =>
        {
            var settings = Settings(4, "hoard");
            Assert(TryCreate(settings, 21, out _, out _, 0));
            Assert(!TryCreate(settings, 31, out _, out _, 0));
            Assert(TryCreate(settings, 31, out _, out _, 1));
            Assert(!TryCreate(settings, 21, out _, out _, 1));
            foreach (uint dungeon in new uint[] { 1, 2, 4 })
            {
                settings = Settings(dungeon, "relic"); int start = AetherpoolExchangePolicy.GetDungeon(dungeon)!.StartFloor;
                Assert(TryCreate(settings, start, out _, out _) && !TryCreate(settings, start + 10, out _, out _));
            }
            settings = Settings(4, "free"); settings.Farming.GetFreeExplorationPreferences().EndFloor = 30;
            Assert(TryCreate(settings, 21, out _, out _) && !TryCreate(settings, 31, out _, out _));
            foreach (var mode in new[] { FarmingMode.Aetherpool, FarmingMode.HoardDiscovery })
            {
                settings = Settings(4, "hoard"); settings.Farming.Mode = mode;
                Assert(TryCreate(settings, 21, out _, out _) && !TryCreate(settings, 31, out _, out _));
            }
        });
        Check("fresh-native-list-resolution-and-ambiguity", () =>
        {
            var source = Slot(1, 40) with { Enterable = false };
            var empty = new SaveSlotSnapshot(0, true, 0, "空檔");
            Assert(ContinuationSavePolicy.TryResolve([empty, source], 31, 100, out var result, out _) && result == source);
            Assert(!ContinuationSavePolicy.TryResolve([source with { Index = 0 }, source], 31, 100, out _, out _));
            Assert(!ContinuationSavePolicy.TryResolve([empty, source with { StartFloor = 51 }], 31, 100, out _, out _));
            Assert(!ContinuationSavePolicy.TryResolve([empty, source with { RowFingerprint = null }], 31, 100, out _, out _));
            Assert(!ContinuationSavePolicy.TryResolve([empty, source with { Identity = "" }], 31, 100, out _, out _));
            Assert(!ContinuationSavePolicy.TryResolve([source], 31, 100, out _, out _));
            var binding = new PreparedSaveBinding(maxFloor: 100) { ContinuationResultFloor = 31 };
            Assert(binding.Observe([empty, source], out result, out _) && result == source);
            try { _ = new DungeonEntryController(DeepDungeon.Fsd.Dalamud.GameState.DungeonCatalog.PilgrimsTraverse, 31, prepared: binding);
                throw new Exception("Read-only result binding allowed entry."); }
            catch (ArgumentException) { }
        });
        Check("external-source-binding-preserves-current-and-next-origins", () =>
        {
            var settings = Settings(1, "ordinary"); var plan = Create(settings, 11);
            var session = new FarmingSession(plan, 1);
            session.RestoreCheckpoint(Slot(1, 11), 0, 123, () => null, _ => { });
            Assert(session.NextFloor == 11 && session.Plan.StartFloor == 51 && !session.NeedsContinuationSource);
            Assert(session.PrepareSource().RequiredSlot == 1 && session.Source.ExpectedFloor == 11);
            session.MarkResumed();
            var decision = FarmingSessionPolicy.Decide(plan, new(191, true, false, false, false));
            session.ConfirmOwnedSaveDeleted(); session.CompleteAttempt(decision, 0);
            Assert(session.NextFloor == 51 && session.OwnedSlot == -1);
        });
        Check("new-save-permission-never-inferred-for-old-records", () =>
        {
            var old = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false, false, true, true, false)
                { ExternalSave = true, DungeonId = 1 };
            var restored = JsonSerializer.Deserialize<FarmingPlan>(JsonSerializer.Serialize(old))!;
            Assert(!restored.ManagedContinuation && !restored.KeepSave && restored.ContinuationStartFloor == null);
            Assert(TaskContinuationPolicy.PreserveSave(restored));
            Assert(!FarmingSessionPolicy.Decide(restored, new(191, true, false, false, false)).DeleteOwnedSave);
        });
        Check("managed-checkpoint-and-live-bronze-survive", () =>
        {
            var plan = Create(Settings(1, "ordinary"), 11); var session = new FarmingSession(plan);
            Assert(session.TrySetTargets(new(false, true, true, true)) && session.OpenBronze);
            plan = plan with { OpenBronze = true };
            Assert(new FloorsetPauseCheckpoint(123, 31, plan, FloorsetResumeKind.ContinueSave, Slot(0, 21), 0, 0).IsValid());
            Assert(new FloorsetPauseCheckpoint(123, 31, plan, FloorsetResumeKind.RestartCycle, null, 0, 0).IsValid());
            Assert(!FloorsetPausePolicy.HasNextCycle(plan with { KeepSave = true }, 0));
        });
        Check("damaged-managed-checkpoints-are-refused", () =>
        {
            var plan = Create(Settings(1, "ordinary"), 11);
            foreach (var invalid in new[] { plan with { ExternalSave = false }, plan with { ExternalSaveCharacter = 0 },
                plan with { ExternalSaveJob = 0 }, plan with { ContinuationStartFloor = null },
                plan with { ContinuationStartFloor = 12 }, plan with { StartFloor = 21 } })
                Assert(!FloorsetPausePolicy.Supports(invalid));
        });
        Check("live-cycle-update-cannot-override-retention", () =>
        {
            var plan = TaskContinuationPolicy.WithRetention(Create(Settings(1, "ordinary"), 11), true);
            var host = (DeepDungeon.Fsd.Dalamud.Runtime.RunHost)RuntimeHelpers.GetUninitializedObject(typeof(DeepDungeon.Fsd.Dalamud.Runtime.RunHost));
            var context = (DeepDungeon.Fsd.Dalamud.Runtime.RunContext)RuntimeHelpers.GetUninitializedObject(typeof(DeepDungeon.Fsd.Dalamud.Runtime.RunContext));
            context.FarmingPlan = plan;
            typeof(DeepDungeon.Fsd.Dalamud.Runtime.RunHost).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, context);
            var loops = new MultiLoopDriver(1, false);
            typeof(DeepDungeon.Fsd.Dalamud.Runtime.RunHost).GetField("_multiLoopDriver", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(host, loops);
            host.SetCycleTargets(99, true);
            Assert(loops.TargetLoops == 1 && !loops.InfiniteLoop);
        });
        Check("usage-footer-covers-new-rules", () =>
        {
            string notes = string.Join("\n", FeatureUsageNotes.Items);
            foreach (string word in new[] { "最大加速度", "保留存檔", "第 1 輪", "GCD", "防踢", "循環", "固定層段", "聚魔柄" })
                Assert(notes.Contains(word));
        });
        return count;
    }

    private static FsdSettings Settings(uint dungeon, string mode)
    {
        var s = new FsdSettings(); s.Farming.DungeonId = dungeon;
        s.Farming.Mode = mode == "hoard" ? FarmingMode.Hoard : FarmingMode.DeepProgression;
        s.Farming.StartFromHighestFloor = mode == "ordinary"; s.Farming.FreeExploration = mode == "free";
        s.Farming.RelicMaterial = mode == "relic";
        return s;
    }
    private static SaveSlotSnapshot Slot(int index, int floor) => new(index, false, floor, $"{floor}", "機工士")
        { RowFingerprint = new string('A', 64) };
    private static FarmingPlan Create(FsdSettings s, int floor)
    { Assert(TryCreate(s, floor, out var p, out var error), error); return p!; }
    private static bool TryCreate(FsdSettings s, int floor, out FarmingPlan? p, out string error, int scenarioIndex = 0)
    {
        var e = (FsdEngine)RuntimeHelpers.GetUninitializedObject(typeof(FsdEngine));
        typeof(FsdEngine).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e, s);
        typeof(FsdEngine).GetField("_fsfScenarioIndex", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(e, scenarioIndex);
        object?[] args = [s.Farming.DungeonId, floor, 123ul, 31u, null, null];
        bool ok = (bool)typeof(FsdEngine).GetMethod("TryCreateContinuationPlan", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e, args)!;
        p = (FarmingPlan?)args[4]; error = (string)args[5]!; return ok;
    }
    private static void Assert(bool value, string? message = null)
    { if (!value) throw new InvalidOperationException("Mode continuation contract failed: " + message); }
}
