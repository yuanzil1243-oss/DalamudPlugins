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
            Enabled = true, StayRequested = true, RoomObserved = true,
            DutyCompleted = true, InDuty = true, PlayerReady = true, DungeonId = 1, Floor = 200
        };
        Check("old-config-default-off", () =>
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
        Check("off-keeps-upstream-stop", () =>
            Assert(!TerminalRewardExitPolicy.ShouldContinue(plan, receipt with { Enabled = false })));
        Check("confirmed-receipt-releases-stay", () =>
            Assert(TerminalRewardExitPolicy.ShouldContinue(plan, receipt)));
        foreach (var invalid in new[]
        {
            receipt with { StayRequested = false }, receipt with { RoomObserved = false },
            receipt with { RewardsPending = true }, receipt with { DutyCompleted = false },
            receipt with { DutyFailed = true }, receipt with { AttemptAborted = true },
            receipt with { HasError = true }, receipt with { StateReadFailed = true },
            receipt with { InDuty = false }, receipt with { Transitioning = true },
            receipt with { PlayerReady = false }, receipt with { DungeonId = 2 },
            receipt with { Floor = 190 }, receipt with { Floor = 199 }, receipt with { Floor = 0 }
        })
            Check("incomplete-or-unsafe-evidence-" + count, () =>
                Assert(!TerminalRewardExitPolicy.ShouldContinue(plan, invalid)));
        Check("unknown-plan-preserves-stop", () =>
            Assert(!TerminalRewardExitPolicy.ShouldContinue(null, receipt)));
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
        return count;
    }

    private static void Assert(bool success)
    {
        if (!success) throw new InvalidOperationException("Terminal reward exit regression failed.");
    }
}
