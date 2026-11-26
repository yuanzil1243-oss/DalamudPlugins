using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class TerminalRewardExitTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        {
            test();
            count++;
            Console.WriteLine("  ok  terminal-exit-" + name);
        }
        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 2, false,
            false, true, true, false) { DungeonId = 1 };
        var receipt = new TerminalRewardExitFacts
        {
            StayRequested = true, RoomObserved = true,
            DutyCompleted = true, InDuty = true, PlayerReady = true, DungeonId = 1, Floor = 200
        };
        Check("legacy-config-representation-preserved-without-runtime-toggle", () =>
        {
            var old = JsonSerializer.Deserialize<FarmingSettings>("{\"DungeonId\":1}");
            Assert(old is { AutoExitAfterTerminalReward: false });
            Assert(!JsonSerializer.Serialize(old).Contains("AutoExitAfterTerminalReward"));
        });
        Check("setting-roundtrip-preserves-preferences", () =>
        {
            var settings = new FarmingSettings { DungeonId = 2, AutoExitAfterTerminalReward = true };
            settings.Preferences["DeepProgression:Create"] = new() { Cycles = 7, Infinite = false };
            var restored = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(settings))!;
            Assert(restored.AutoExitAfterTerminalReward && restored.DungeonId == 2 &&
                restored.Preferences["DeepProgression:Create"].Cycles == 7);
        });
        Check("legacy-false-cannot-disable-default-exit", () =>
        {
            var settings = JsonSerializer.Deserialize<FarmingSettings>("{\"AutoExitAfterTerminalReward\":false}")!;
            Assert(!settings.AutoExitAfterTerminalReward && TerminalRewardExitPolicy.ShouldContinue(plan, receipt));
        });
        Check("confirmed-receipt-releases-stay", () =>
            Assert(TerminalRewardExitPolicy.ShouldContinue(plan, receipt)));
        foreach (var invalid in new[]
        {
            receipt with { StayRequested = false }, receipt with { RoomObserved = false },
            receipt with { RewardsPending = true }, receipt with { DutyCompleted = false },
            receipt with { DutyFailed = true }, receipt with { AttemptAborted = true },
            receipt with { HasError = true }, receipt with { StateReadFailed = true },
            receipt with { Transitioning = true },
            receipt with { PlayerReady = false }, receipt with { DungeonId = 2 },
            receipt with { Floor = 190 }, receipt with { Floor = 199 }, receipt with { Floor = 0 }
        })
            Check("incomplete-or-unsafe-evidence-" + count, () =>
                Assert(!TerminalRewardExitPolicy.ShouldContinue(plan, invalid)));
        Check("unknown-plan-preserves-stop", () =>
            Assert(!TerminalRewardExitPolicy.ShouldContinue(null, receipt)));
        Check("legacy-true-and-false-use-same-reward-confirmation", () =>
        {
            foreach (bool legacy in new[] { false, true })
            {
                var settings = new FarmingSettings { AutoExitAfterTerminalReward = legacy };
                var restored = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(settings))!;
                Assert(restored.AutoExitAfterTerminalReward == legacy);
                Assert(TerminalRewardExitPolicy.Decide(plan, receipt).Action == TerminalRewardExitAction.Continue);
                Assert(TerminalRewardExitPolicy.Decide(plan, receipt with { RewardsPending = true }).Action == TerminalRewardExitAction.Wait);
            }
        });
        Check("loading-or-unavailable-player-waits-without-ending-task", () =>
        {
            foreach (var transient in new[]
            {
                receipt with { Transitioning = true }, receipt with { StateReadFailed = true },
                receipt with { PlayerReady = false }
            })
                Assert(TerminalRewardExitPolicy.Decide(plan, transient).Action == TerminalRewardExitAction.Wait);
            Assert(TerminalRewardExitPolicy.Decide(plan, receipt).Action == TerminalRewardExitAction.Continue);
        });
        Check("unconfirmed-reward-waits-without-exit-or-completion", () =>
        {
            foreach (var pending in new[]
            {
                receipt with { StayRequested = false }, receipt with { RoomObserved = false },
                receipt with { RewardsPending = true }, receipt with { DutyCompleted = false }
            })
                Assert(TerminalRewardExitPolicy.Decide(plan, pending).Action == TerminalRewardExitAction.Wait);
        });
        Check("confirmed-reward-manual-exit-still-reaches-protected-cleanup", () =>
        {
            var outside = receipt with { InDuty = false, DungeonId = 0, Floor = 0 };
            Assert(TerminalRewardExitPolicy.Decide(plan, outside) == new TerminalRewardExitDecision(
                TerminalRewardExitAction.Continue, "reward-confirmed-already-outside"));
            Assert(TerminalRewardExitPolicy.Decide(plan, outside with { RewardsPending = true }).Action ==
                TerminalRewardExitAction.Wait);
            Assert(TerminalRewardExitPolicy.Decide(plan, outside with { DutyFailed = true }).Action ==
                TerminalRewardExitAction.RetainAndStop);
        });
        Check("native-final-exit-identities-cover-all-supported-dungeons", () =>
        {
            foreach (var (dungeon, floor) in new (uint, int)[] { (1, 200), (2, 100), (4, 100) })
            {
                Assert(TerminalRewardExitPolicy.TryGetExitBaseId(dungeon, floor, out var exit) && exit == 2005809);
                Assert(!TerminalRewardExitPolicy.TryGetExitBaseId(dungeon, floor - 10, out _));
                Assert(!TerminalRewardExitPolicy.TryGetExitBaseId(dungeon, 30, out _));
            }
            Assert(!TerminalRewardExitPolicy.TryGetExitBaseId(0, 100, out _));
        });
        Check("wrong-terminal-or-failed-run-retains-save", () =>
        {
            foreach (var rejected in new[]
            {
                receipt with { DungeonId = 2 }, receipt with { Floor = 190 },
                receipt with { DutyFailed = true }, receipt with { AttemptAborted = true },
                receipt with { HasError = true }
            })
                Assert(TerminalRewardExitPolicy.Decide(plan, rejected).Action == TerminalRewardExitAction.RetainAndStop);
        });
        foreach (var diagnostic in new[]
        {
            plan with { StopAfterFloor = 200 }, plan with { HoldAfterClear = true },
            plan with { HoldOnFailure = true }, plan with { Mode = FarmingMode.Hoard },
            plan with { FreeExploration = true, EndFloor = 190 }
        })
            Check("other-mode-or-diagnostic-" + count, () =>
                Assert(!TerminalRewardExitPolicy.ShouldContinue(diagnostic, receipt)));
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            var normal = plan with { DungeonId = dungeon };
            int max = normal.MaxFloor, entry = normal.TerminalEntryFloor;
            var terminal = receipt with { DungeonId = dungeon, Floor = max };
            Check("terminal-self-created-cleanup-" + dungeon, () =>
            {
                Assert(TerminalRewardExitPolicy.ShouldContinue(normal, terminal));
                var decision = FarmingSessionPolicy.Decide(normal, new(entry, true, false, false, false));
                Assert(decision.CountCycle && decision.DeleteOwnedSave && decision.NextFloor == 1 &&
                    !decision.RecoverFailure && decision.Error.Length == 0);
                var session = new FarmingSession(normal, 1);
                session.CompleteAttempt(decision, 0);
                Assert(session.NextFloor == normal.StartFloor && session.OwnedSlot == 1);
                session.ConfirmOwnedSaveDeleted();
                Assert(session.OwnedSlot == -1); // Only after the native delete flow verifies empty.
            });
            Check("highest-start-restarts-at-selected-start-" + dungeon, () =>
            {
                var highest = normal with
                { OrdinaryDescent = true, StartFloor = OrdinaryDescentPolicy.HighestStartFloor(dungeon) };
                Assert(TerminalRewardExitPolicy.ShouldContinue(highest, terminal));
                var decision = FarmingSessionPolicy.Decide(highest, new(entry, true, false, false, false));
                Assert(decision.CountCycle && decision.DeleteOwnedSave && decision.NextFloor == highest.StartFloor);
            });
            Check("free-to-bottom-cleans-after-full-range-" + dungeon, () =>
            {
                var free = normal with { FreeExploration = true, EndFloor = max };
                Assert(TerminalRewardExitPolicy.ShouldContinue(free, terminal));
                var decision = FarmingSessionPolicy.Decide(free, new(entry, true, false, false, false));
                Assert(decision.CountCycle && decision.DeleteOwnedSave && decision.NextFloor == free.StartFloor);
                var middle = FarmingSessionPolicy.Decide(free, new(1, true, false, false, false));
                Assert(!middle.CountCycle && !middle.DeleteOwnedSave && middle.NextFloor == 11);
            });
            Check("external-exits-without-deletion-" + dungeon, () =>
            {
                var external = normal with { ExternalSave = true, TargetCycles = 1 };
                Assert(TerminalRewardExitPolicy.ShouldContinue(external, terminal));
                var decision = FarmingSessionPolicy.Decide(external, new(entry, true, false, false, false));
                Assert(decision.CountCycle && !decision.DeleteOwnedSave && !decision.RecoverFailure);
                Assert(TerminalRewardExitPolicy.ShouldContinue(external with { CurrentDutyOnly = true }, terminal));
            });
            Check("cleanup-rejects-different-save-" + dungeon, () =>
            {
                var original = new SaveSlotSnapshot(1, false, entry, entry.ToString(), "owned");
                var completed = original with { StartFloor = max, Progress = max.ToString() };
                Assert(OwnedSavePolicy.ValidateForDeletion([completed], 1, entry, original) == null);
                Assert(OwnedSavePolicy.ValidateForDeletion([completed with { Identity = "other" }], 1, entry, original) != null);
                Assert(OwnedSavePolicy.ValidateForDeletion([completed with { Index = 0 }], 1, entry, original) != null);
            });
        }
        Check("reported-hoh100-reward-scoreboard-cleanup-then-loop", () =>
        {
            var normal = plan with { DungeonId = 2, OrdinaryDescent = true, StartFloor = 21, TargetCycles = 3 };
            var reward = receipt with { DungeonId = 2, Floor = 100 };
            foreach (bool infinite in new[] { false, true })
            {
                var loops = new MultiLoopDriver(3, infinite);
                var session = new FarmingSession(normal, 0);
                // Delayed reward/loading must not take the host's retain-and-stop path.
                foreach (var waiting in new[]
                {
                    reward with { RewardsPending = true }, reward with { Transitioning = true },
                    reward with { PlayerReady = false }
                })
                {
                    Assert(TerminalRewardExitPolicy.Decide(normal, waiting).Action == TerminalRewardExitAction.Wait);
                    Assert(loops.CompletedLoops == 0 && session.OwnedSlot == 0);
                }
                Assert(TerminalRewardExitPolicy.Decide(normal, reward).Action == TerminalRewardExitAction.Continue);
                Assert(loops.CompletedLoops == 0);
                // The native exit, not an unverified abandon, starts terminal departure.
                Assert(ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.None, true), false) ==
                    ExitAction.ApproachDutyExit);
                Assert(ExitUiPolicy.Decide(new(false, true, false, false, false, ExitUiKind.RestConfirmation, true), false) ==
                    ExitAction.ConfirmRestExit);
                var pendingScore = new ExitFacts(false, false, true, false, false, ExitUiKind.Incomplete)
                    { ResultWindowPresent = true };
                Assert(ExitUiPolicy.Decide(pendingScore, true) == ExitAction.Wait);
                Assert(loops.CompletedLoops == 0 && session.OwnedSlot == 0);
                Assert(ExitUiPolicy.Decide(pendingScore with { Ui = ExitUiKind.Result }, true) == ExitAction.LeaveResult);
                Assert(ExitUiPolicy.Decide(new(false, false, true, false, false, ExitUiKind.None), true) == ExitAction.Complete);
                var cleanup = FarmingSessionPolicy.Decide(normal, new(91, true, false, false, false));
                Assert(cleanup.CountCycle && cleanup.DeleteOwnedSave && cleanup.NextFloor == 21);
                Assert(session.OwnedSlot == 0 && loops.CompletedLoops == 0);
                session.ConfirmOwnedSaveDeleted();
                session.CompleteAttempt(cleanup, 0);
                Assert(session.OwnedSlot == -1 && session.NextFloor == 21);
                Assert(!loops.ShouldStopAfterCurrentRun());
                loops.IncrementLoop();
                Assert(loops.CompletedLoops == 1 && !loops.ShouldStopAfterCurrentRun());
            }
        });
        Check("final-loop-also-finishes-scoreboard-and-cleanup-before-stop", () =>
        {
            var normal = plan with { DungeonId = 2, OrdinaryDescent = true, StartFloor = 21, TargetCycles = 1 };
            var loops = new MultiLoopDriver(1, false);
            Assert(TerminalRewardExitPolicy.ShouldContinue(normal, receipt with { DungeonId = 2, Floor = 100 }));
            Assert(ExitUiPolicy.Decide(new(false, false, true, false, false, ExitUiKind.Result), true) == ExitAction.LeaveResult);
            Assert(loops.CompletedLoops == 0);
            var cleanup = FarmingSessionPolicy.Decide(normal, new(91, true, false, false, false));
            Assert(cleanup.DeleteOwnedSave && cleanup.CountCycle);
            Assert(ExitUiPolicy.Decide(new(false, false, true, false, false, ExitUiKind.None), true) == ExitAction.Complete);
            Assert(loops.ShouldStopAfterCurrentRun());
        });
        return count;
    }

    private static void Assert(bool success)
    {
        if (!success) throw new InvalidOperationException("Terminal reward exit regression failed.");
    }
}
