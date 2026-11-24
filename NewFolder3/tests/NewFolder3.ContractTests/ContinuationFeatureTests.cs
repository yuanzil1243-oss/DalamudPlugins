using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class ContinuationFeatureTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  continuation-" + name); }
        var valid = new ContinueExplorationFacts(true, false, true, true, false, true,
            123, 31, 1, 1, 51, 51, 60, true);
        foreach (bool territory in new[] { false, true })
            foreach (bool running in new[] { false, true })
                Check($"button-visible-{territory}-{running}", () =>
                    Assert(ContinueExplorationPolicy.IsVisible(territory, running) == (territory && !running)));
        Check("slot-free-current-duty", () =>
        {
            Assert(ContinueExplorationPolicy.TryAttach(valid, out var group, out _) && group == 51);
            Assert(typeof(ContinueExplorationFacts).GetProperties().All(p => !p.Name.Contains("Slot")));
        });
        var invalid = new[]
        {
            valid with { Running = true }, valid with { InDeepDungeonTerritory = false },
            valid with { InDuty = false }, valid with { ObservationValid = false },
            valid with { Transitioning = true }, valid with { PlayerAlive = false },
            valid with { Character = 0 }, valid with { Job = 0 },
            valid with { DungeonId = 3, TerritoryDungeonId = 3 },
            valid with { DungeonId = 0 }, valid with { TerritoryDungeonId = 2 },
            valid with { Floor = 0 }, valid with { Floor = 201 },
            valid with { Floor = 61 }, valid with { Floor = 50 },
            valid with { RangeStart = 0 }, valid with { RangeStart = 52, RangeEnd = 61 },
            valid with { RangeEnd = 59 }, valid with { RangeEnd = 201 },
            valid with { FloorClassified = false }, valid with { Job = 19 },
            valid with { DungeonId = 4, TerritoryDungeonId = 4, Floor = 1, RangeStart = 1, RangeEnd = 1 }
        };
        foreach (var facts in invalid)
            Check("invalid-current-duty-" + count, () =>
                Assert(!ContinueExplorationPolicy.TryAttach(facts, out var group, out var error) &&
                    group == 0 && error.Length > 0));
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            int max = dungeon == 1 ? 200 : 100;
            Check("all-current-duty-floors-" + dungeon, () =>
            {
                for (int floor = 1; floor <= max; floor++)
                {
                    int start = (floor - 1) / 10 * 10 + 1;
                    var facts = valid with { DungeonId = dungeon, TerritoryDungeonId = dungeon,
                        Floor = floor, RangeStart = start, RangeEnd = start + 9 };
                    Assert(ContinueExplorationPolicy.TryAttach(facts, out var group, out _) && group == start);
                }
            });
            Check("upstream-supported-jobs-" + dungeon, () =>
            {
                foreach (uint job in new uint[] { 19, 23, 31, 38, 42 })
                {
                    var facts = valid with { DungeonId = dungeon, TerritoryDungeonId = dungeon, Job = job };
                    Assert(ContinueExplorationPolicy.TryAttach(facts, out _, out _) ==
                        (dungeon == 4 || job is 23 or 31 or 38));
                }
            });
            int highest = dungeon switch { 1 => 51, 2 => 21, _ => 71 };
            Check("highest-new-save-floor-" + dungeon, () =>
                Assert(OrdinaryDescentPolicy.HighestStartFloor(dungeon) == highest));
            FarmingPlan Create(bool ordinary, int start)
            {
                Assert(FarmingPlan.TryCreate(FarmingMode.DeepProgression, SaveUse.Create, start, 2, true,
                    false, true, true, false, out var plan, out _, dungeon, ordinary));
                return plan!;
            }
            var descent = Create(true, highest);
            var original = Create(false, 1);
            Check("ordinary-and-achievement-separate-" + dungeon, () =>
            {
                Assert(descent.OrdinaryDescent && descent.DisplayName == "普通下底" && descent.StartFloor == highest);
                Assert(!original.OrdinaryDescent && original.DisplayName == "死靈術士" && original.StartFloor == 1);
                Assert(descent.Mode == original.Mode && !descent.BandedEnabled && descent.OpenGold && descent.OpenSilver);
                Assert(JsonSerializer.Deserialize<FarmingPlan>(JsonSerializer.Serialize(descent)) == descent);
            });
            Check("ordinary-rejects-other-new-save-starts-" + dungeon, () =>
            {
                for (int floor = 0; floor <= max + 1; floor++)
                    Assert(FarmingPlan.TryCreate(FarmingMode.DeepProgression, SaveUse.Create, floor, 1, false,
                        false, true, true, false, out _, out _, dungeon, true) == (floor == highest));
                Assert(!FarmingPlan.TryCreate(FarmingMode.DeepProgression, SaveUse.Create, highest, 1, false,
                    false, true, true, false, out _, out _, dungeon));
            });
            Check("ordinary-restarts-from-highest-" + dungeon, () =>
            {
                var failed = FarmingSessionPolicy.Decide(descent, new(highest + 10, false, true, false, false));
                Assert(failed.RecoverFailure && failed.NextFloor == highest && failed.DeleteOwnedSave);
                var terminal = FarmingSessionPolicy.Decide(descent, new(max - 9, true, false, false, false));
                Assert(terminal.CountCycle && terminal.NextFloor == highest && terminal.DeleteOwnedSave);
                var next = FarmingSessionPolicy.Decide(descent, new(highest, true, false, false, false));
                Assert(!next.CountCycle && next.NextFloor == highest + 10 && !next.DeleteOwnedSave);
            });
            Check("ordinary-upstream-pause-boundary-" + dungeon, () =>
            {
                var paused = descent with { StopAfterFloor = highest + 9 };
                Assert(FarmingSessionPolicy.ReachedStopBoundary(paused, new(highest, true, false, false, false)));
                Assert(!FarmingSessionPolicy.ReachedStopBoundary(paused, new(highest, false, true, false, false)));
            });
        }
        Check("ordinary-not-a-new-enum-value", () =>
            Assert(Enum.GetValues<FarmingMode>().Length == 4 && (int)FarmingMode.DeepProgression == 3));
        Check("ordinary-rejects-wrong-modes-and-saves", () =>
        {
            foreach (var mode in Enum.GetValues<FarmingMode>())
                foreach (var save in Enum.GetValues<SaveUse>())
                    Assert(OrdinaryDescentPolicy.IsValidStart(4, mode, save, 71) ==
                        (mode == FarmingMode.DeepProgression && save == SaveUse.Create));
            Assert(OrdinaryDescentPolicy.HighestStartFloor(3) == 0);
        });
        Check("existing-settings-fingerprint-preserved", () =>
        {
            var settings = new FarmingSettings();
            var oldShape = new { settings.DungeonId, settings.Mode, settings.Preferences };
            Assert(JsonSerializer.Serialize(settings) == JsonSerializer.Serialize(oldShape));
            Assert(!JsonSerializer.Serialize(new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1,
                1, false, false, true, true, false)).Contains("OrdinaryDescent"));
        });
        Check("ordinary-preferences-independent", () =>
        {
            var settings = new FsdSettings();
            settings.Farming.Mode = FarmingMode.DeepProgression;
            var original = settings.Farming.GetPreferences(settings);
            original.Cycles = 7;
            settings.Farming.StartFromHighestFloor = true;
            var ordinary = settings.Farming.GetPreferences(settings);
            ordinary.Cycles = 2;
            Assert(!ReferenceEquals(original, ordinary));
            var restored = JsonSerializer.Deserialize<FsdSettings>(JsonSerializer.Serialize(settings))!;
            Assert(restored.Farming.StartFromHighestFloor && restored.Farming.GetPreferences(restored).Cycles == 2);
            restored.Farming.StartFromHighestFloor = false;
            Assert(restored.Farming.GetPreferences(restored).Cycles == 7);
        });
        return count;
    }

    private static void Assert(bool condition)
    { if (!condition) throw new InvalidOperationException("Continuation/descent contract failed."); }
}
