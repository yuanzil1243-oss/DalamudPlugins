using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime;

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
        Check("resume-defaults-without-old-record", () =>
        {
            var settings = new FarmingSettings { DungeonId = 1, Mode = FarmingMode.DeepProgression };
            var p = settings.GetCurrentDutyChestPreferences(1);
            Assert(p.OpenGold && p.OpenSilver && !p.OpenBronze && p.BandedEnabled == false);
            Assert(settings.Preferences.Count == 0 && settings.FreeExplorationProfiles == null);
        });
        Check("resume-copies-stored-chests-without-mode-coercion", () =>
        {
            var settings = new FarmingSettings { DungeonId = 1, Mode = FarmingMode.DeepProgression };
            var old = new FarmingPreferences { OpenGold = false, OpenSilver = true, OpenBronze = true, Cycles = 9, Infinite = true };
            settings.Preferences["DeepProgression:Create"] = old;
            string before = JsonSerializer.Serialize(settings.Preferences);
            var p = settings.GetCurrentDutyChestPreferences(1);
            Assert(!p.OpenGold && p.OpenSilver && p.OpenBronze && p.Cycles == 1 && !p.Infinite);
            Assert(JsonSerializer.Serialize(settings.Preferences) == before && !ReferenceEquals(old, p));
            p.OpenBronze = false;
            Assert(old.OpenBronze);
        });
        Check("resume-copies-free-profile-without-changing-it", () =>
        {
            var settings = new FarmingSettings { DungeonId = 1, FreeExploration = true };
            var old = settings.GetFreeExplorationPreferences();
            old.OpenGold = false; old.OpenSilver = false; old.OpenBronze = true; old.Cycles = 8;
            string before = JsonSerializer.Serialize(settings.FreeExplorationProfiles);
            var p = settings.GetCurrentDutyChestPreferences(1);
            Assert(!p.OpenGold && !p.OpenSilver && p.OpenBronze);
            p.OpenSilver = true;
            Assert(JsonSerializer.Serialize(settings.FreeExplorationProfiles) == before);
        });
        Check("resume-recognizes-each-saved-mode-profile", () =>
        {
            foreach (var (mode, highest, key) in new[] {
                (FarmingMode.Hoard, false, "Hoard:Create"),
                (FarmingMode.Aetherpool, false, "Aetherpool:Prepared"),
                (FarmingMode.HoardDiscovery, false, "HoardDiscovery:Prepared"),
                (FarmingMode.DeepProgression, false, "DeepProgression:Create"),
                (FarmingMode.DeepProgression, true, "OrdinaryDescent:Create") })
            {
                var settings = new FarmingSettings { DungeonId = 4, Mode = mode, StartFromHighestFloor = highest };
                settings.Preferences[key] = new() { OpenBronze = true };
                Assert(settings.GetCurrentDutyChestPreferences(4).OpenBronze);
            }
        });
        Check("resume-actual-dungeon-never-uses-other-dungeon-selection", () =>
        {
            var settings = new FarmingSettings { DungeonId = 4, FreeExploration = true };
            settings.GetFreeExplorationPreferences().OpenBronze = true;
            var actual = settings.GetCurrentDutyChestPreferences(1);
            Assert(actual.OpenGold && actual.OpenSilver && !actual.OpenBronze && settings.DungeonId == 4);
        });
        Check("resume-independent-dungeon-choices-survive-reload", () =>
        {
            var settings = new FarmingSettings();
            foreach (uint dungeon in new uint[] { 1, 2, 4 })
            {
                var p = settings.GetCurrentDutyChestPreferences(dungeon);
                p.OpenGold = dungeon == 1; p.OpenSilver = dungeon == 2; p.OpenBronze = dungeon == 4;
            }
            var restored = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(settings))!;
            foreach (uint dungeon in new uint[] { 1, 2, 4 })
            {
                var p = restored.GetCurrentDutyChestPreferences(dungeon);
                Assert(p.OpenGold == (dungeon == 1) && p.OpenSilver == (dungeon == 2) && p.OpenBronze == (dungeon == 4));
            }
        });
        Check("resume-saved-choice-wins-over-later-mode-change", () =>
        {
            var settings = new FarmingSettings { DungeonId = 1, Mode = FarmingMode.DeepProgression };
            settings.GetCurrentDutyChestPreferences(1).OpenBronze = true;
            settings.Preferences["DeepProgression:Create"] = new() { OpenBronze = false };
            Assert(settings.GetCurrentDutyChestPreferences(1).OpenBronze);
        });
        Check("resume-rejects-unsupported-dungeon-before-creating-profile", () =>
        {
            var settings = new FarmingSettings();
            try { settings.GetCurrentDutyChestPreferences(3); throw new Exception("Unsupported dungeon accepted"); }
            catch (ArgumentOutOfRangeException) { Assert(settings.CurrentDutyChestProfiles == null); }
        });
        var resume = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 51, 1, false,
            false, true, true, false) { DungeonId = 1, CurrentDutyOnly = true, ExternalSave = true };
        Check("resume-all-eight-chest-choices-reach-real-floor-policy", () =>
        {
            for (int bits = 0; bits < 8; bits++)
            {
                var targets = new FarmingTargets(false, (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0);
                var provider = new RunOptionsProvider(new() { BandedEnabled = false });
                Assert(CurrentDutyChestPolicy.TryApply(resume, provider, targets));
                var policy = FloorWorkPolicyBuilder.Build(resume, provider.Current);
                Assert(policy.OpenGold == targets.Gold && policy.OpenSilver == targets.Silver && policy.OpenBronze == targets.Bronze);
                Assert(!policy.SearchHoard && !policy.HarvestChests && !policy.DiscoveryOnly);
            }
        });
        Check("resume-bronze-on-then-off-publishes-new-option-snapshots", () =>
        {
            var provider = new RunOptionsProvider(new() { BandedEnabled = false,
                LeaveMode = LeaveMode.AfterFinishDungeon, RequireValidatedAbandonPrompt = true, LocalAggressiveChestInteraction = true });
            var before = provider.Current;
            long version = provider.Version;
            Assert(CurrentDutyChestPolicy.TryApply(resume, provider, new(false, true, true, true)));
            var enabled = provider.Current;
            Assert(!before.OpenBronze && enabled.OpenBronze && provider.Version == version + 1);
            Assert(CurrentDutyChestPolicy.TryApply(resume, provider, new(false, true, true, false)));
            Assert(enabled.OpenBronze && !provider.Current.OpenBronze && provider.Version == version + 2);
            Assert(provider.Current.LeaveMode == LeaveMode.AfterFinishDungeon && provider.Current.RequireValidatedAbandonPrompt &&
                provider.Current.LocalAggressiveChestInteraction == true && !provider.Current.BandedEnabled);
            Assert(!FloorWorkPolicyBuilder.Build(resume, provider.Current).OpenBronze);
        });
        Check("resume-live-update-rejects-other-tasks-and-hoard", () =>
        {
            foreach (var other in new FarmingPlan?[] { null, resume with { CurrentDutyOnly = false },
                resume with { ExternalSave = false }, resume with { DungeonId = 3 } })
            {
                var provider = new RunOptionsProvider(new());
                Assert(!CurrentDutyChestPolicy.TryApply(other, provider, new(false, true, true, true)) && provider.Version == 0);
            }
            var validProvider = new RunOptionsProvider(new());
            Assert(!CurrentDutyChestPolicy.TryApply(resume, validProvider, new(true, true, true, true)) && validProvider.Version == 0);
        });
        Check("upstream-progression-bronze-lock-remains-unchanged", () =>
        {
            Assert(FarmingTargetPolicy.LocksSilverBronze(FarmingMode.DeepProgression));
            Assert(!FarmingTargetPolicy.Constrain(FarmingMode.DeepProgression, new(false, true, true, true)).Bronze);
            Assert(!FarmingPlan.TryCreate(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false,
                false, true, true, true, out _, out _, 1));
        });
        return count;
    }

    private static void Assert(bool condition)
    { if (!condition) throw new InvalidOperationException("Continuation/descent contract failed."); }
}
