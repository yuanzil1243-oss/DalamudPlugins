using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class RecoveryAndPauseTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        { test(); count++; Console.WriteLine("  ok  recovery-pause-" + name); }
        Check("watchdog-waits-six-eligible-seconds", () =>
        {
            var guard = new GcdStallWatchdog();
            Assert(!guard.Update(1000, 42, true, false));
            Assert(!guard.Update(6999, 42, true, false));
            Assert(guard.Update(7000, 42, true, false));
        });
        Check("one-attempt-per-stall-including-native-rejection", () =>
        {
            var guard = new GcdStallWatchdog(); guard.Update(1, 42, true, false);
            Assert(guard.Update(6001, 42, true, false));
            Assert(!guard.Update(12001, 42, true, false));
            Assert(!guard.Update(18001, 42, false, false));
            Assert(!guard.Update(24001, 42, true, false));
            Assert(guard.Attempted);
        });
        Check("only-observed-gcd-progress-rearms-same-target", () =>
        {
            var guard = new GcdStallWatchdog(); guard.Update(1, 42, true, false);
            Assert(guard.Update(6001, 42, true, false));
            Assert(!guard.Update(6100, 42, false, true) && !guard.Attempted);
            Assert(!guard.Update(10000, 42, false, true));
            Assert(!guard.Update(11000, 42, true, false));
            Assert(!guard.Update(16999, 42, true, false));
            Assert(guard.Update(17000, 42, true, false));
        });
        Check("hold-cast-cooldown-and-range-restart-timer", () =>
        {
            var guard = new GcdStallWatchdog(); guard.Update(1000, 42, true, false);
            guard.Update(6500, 42, false, false);
            Assert(!guard.Update(20000, 42, true, false));
            Assert(!guard.Update(25999, 42, true, false));
            Assert(guard.Update(26000, 42, true, false));
        });
        Check("target-change-never-carries-idle-time", () =>
        {
            var guard = new GcdStallWatchdog(); guard.Update(1, 42, true, false);
            Assert(!guard.Update(6001, 43, true, false));
            Assert(guard.Update(12001, 43, true, false));
        });
        Check("zero-target-stop-and-clock-reset", () =>
        {
            var guard = new GcdStallWatchdog(); guard.Update(1000, 42, true, false);
            Assert(!guard.Update(9000, 0, true, false));
            Assert(!guard.Update(10000, 42, true, false));
            Assert(!guard.Update(1, 42, true, false));
            Assert(!guard.Attempted);
            guard.Reset(); Assert(!guard.Update(7000, 42, true, false));
        });
        Check("disabled-old-settings-preserve-fingerprint", () =>
        {
            var settings = JsonSerializer.Deserialize<FarmingSettings>("{\"DungeonId\":4}")!;
            Assert(!settings.GcdStallProtection && !JsonSerializer.Serialize(settings).Contains("GcdStallProtection"));
            settings.GcdStallProtection = true;
            Assert(JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(settings))!.GcdStallProtection);
        });
        Check("unsupported-jobs-have-no-guessed-action", () =>
        { foreach (uint job in new uint[] { 0, 8, 16, 18, 36, 99 }) Assert(GcdRecoveryActions.ForJob(job) == 0); });
        Check("combat-job-base-gcd-map", () =>
        {
            foreach (uint job in new uint[] { 19, 20, 21, 22, 23, 24, 25, 27, 28, 30, 31, 32, 33, 34, 35, 37, 38, 39, 40, 41, 42 })
                Assert(GcdRecoveryActions.ForJob(job) > 0);
            Assert(GcdRecoveryActions.ForJob(31) == 2866 && GcdRecoveryActions.ForJob(28) == 17869);
        });

        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 3, false, false, true, true, false);
        var slot = new SaveSlotSnapshot(1, false, 11, "第11朝聖路", "機工士", true) { RowFingerprint = new string('A', 64) };
        var saved = new FloorsetPauseCheckpoint(123, 31, plan, FloorsetResumeKind.ContinueSave, slot, 0, 2);
        Check("mid-progression-checkpoint-roundtrip", () =>
        { Assert(saved.IsValid()); Assert(JsonSerializer.Deserialize<FloorsetPauseCheckpoint>(JsonSerializer.Serialize(saved)) == saved); });
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            var normal = plan with { DungeonId = dungeon };
            Check("all-dungeons-progression-" + dungeon, () => Assert((saved with { Plan = normal }).IsValid()));
            var highest = normal with { OrdinaryDescent = true, StartFloor = OrdinaryDescentPolicy.HighestStartFloor(dungeon) };
            Check("highest-start-pause-" + dungeon, () => Assert((saved with { Plan = highest,
                Slot = slot with { StartFloor = highest.StartFloor + 10 } }).IsValid()));
            FreeExplorationPolicy.TryCreate(dungeon, 1, 30, 3, false, new(true, false, true, true), false, false,
                out var free, out _);
            Check("free-range-midgroup-pause-" + dungeon, () => Assert((saved with { Plan = free! }).IsValid()));
        }
        var hoard = new FarmingPlan(FarmingMode.Hoard, SaveUse.Create, 21, 3, false, true, true, true, false);
        var fresh = saved with { Plan = hoard, Kind = FloorsetResumeKind.RestartCycle, Slot = null, CompletedCycles = 1 };
        Check("hoard-after-cleanup-resumes-next-cycle", () => Assert(fresh.IsValid()));
        Check("finite-last-cycle-never-creates-resume-lock", () =>
        { Assert(!FloorsetPausePolicy.HasNextCycle(hoard, 3)); Assert(!(fresh with { CompletedCycles = 3 }).IsValid()); });
        Check("infinite-cycle-checkpoint-keeps-completed-count", () =>
            Assert((fresh with { Plan = hoard with { Infinite = true }, CompletedCycles = 20 }).IsValid()));
        foreach (var mode in new[] { FarmingMode.Aetherpool, FarmingMode.HoardDiscovery })
        {
            var prepared = new FarmingPlan(mode, SaveUse.Prepared, 0, 5, false,
                mode == FarmingMode.HoardDiscovery, false, mode == FarmingMode.Aetherpool, false);
            Check("prepared-mode-pause-" + mode, () => Assert((saved with { Plan = prepared,
                Kind = FloorsetResumeKind.ReusePrepared, CompletedCycles = 2 }).IsValid()));
        }
        var external = plan with { ExternalSave = true, ExternalSaveCharacter = 123, ExternalSaveJob = 31, TargetCycles = 1 };
        Check("external-midgroup-resume-protected", () =>
        { Assert((saved with { Plan = external }).IsValid()); Assert(!FloorsetPausePolicy.HasNextCycle(external, 0)); });
        Check("external-never-resumes-via-empty-slot", () =>
            Assert(!(fresh with { Plan = external, CompletedCycles = 0 }).IsValid()));
        Check("external-character-job-binding", () =>
            Assert(!(saved with { Plan = external, Character = 456 }).IsValid()));
        foreach (var bad in new[]
        {
            saved with { Character = 0 }, saved with { Job = 0 }, saved with { Failures = -1 },
            saved with { Slot = null }, saved with { Slot = slot with { Empty = true } },
            saved with { Slot = slot with { Enterable = false } }, saved with { Slot = slot with { Index = 2 } },
            saved with { Slot = slot with { StartFloor = 1 } }, saved with { Slot = slot with { StartFloor = 12 } },
            saved with { Slot = slot with { RowFingerprint = null } }, saved with { Kind = (FloorsetResumeKind)99 },
            saved with { Plan = plan with { CurrentDutyOnly = true } }, saved with { Plan = plan with { HoldAfterClear = true } },
            saved with { Plan = plan with { HoldOnFailure = true } }, saved with { Plan = plan with { StopAfterFloor = 30 } },
            saved with { Plan = plan with { Mode = (FarmingMode)99 } }, saved with { Plan = plan with { StartFloor = 12 } }
        }) Check("invalid-record-refused-" + count, () => Assert(!bad.IsValid()));
        Check("release-archives-only-exact-task-bytes", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "nf3-pause-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string path = Path.Combine(dir, "FsdFloorsetPause.json"), other = Path.Combine(dir, "other-save.fixture");
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(saved);
                File.WriteAllBytes(path, bytes); File.WriteAllText(other, "keep game data");
                bool refused = false;
                try { PauseCheckpointArchive.Archive(path, new string('0', 64)); } catch (IOException) { refused = true; }
                Assert(refused && File.ReadAllBytes(path).SequenceEqual(bytes));
                string archive = PauseCheckpointArchive.Archive(path, PauseCheckpointArchive.Hash(bytes));
                Assert(!File.Exists(path) && File.ReadAllBytes(archive).SequenceEqual(bytes));
                Assert(File.ReadAllText(other) == "keep game data" && Path.GetDirectoryName(archive) == Path.Combine(dir, "FsdPauseArchive"));
            }
            finally { Directory.Delete(dir, true); }
        });
        return count;
    }

    private static void Assert(bool condition)
    { if (!condition) throw new InvalidOperationException("Recovery/pause contract failed."); }
}
