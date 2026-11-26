using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud;

namespace NewFolder3;

internal static class RelicPresetTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(string name, Action test) { test(); passed++; Console.WriteLine("  ok  relic-preset-" + name); }
        Check("legacy-settings-still-exchange-and-start-51", () =>
        {
            var settings = JsonSerializer.Deserialize<FarmingSettings>("{\"DungeonId\":1,\"RelicMaterial\":true}")!;
            Require(!settings.RelicPotd1To50 && !settings.RelicStrengthOnly && settings.GetRelicStartFloor() == 51);
            Require(settings.ConfigureGripExchange(Plan(1, 51)).GripExchange == new GripExchangeOptions());
            Require(!JsonSerializer.Serialize(settings).Contains("RelicPotd1To50") && !JsonSerializer.Serialize(settings).Contains("RelicStrengthOnly"));
        });
        Check("new-options-persist-separately-from-mode-and-chest-preferences", () =>
        {
            var s = new FarmingSettings { DungeonId = 1, RelicMaterial = true, RelicPotd1To50 = true, RelicStrengthOnly = true,
                GripExchange = new() { Threshold = 60, BatchCount = 1, EnabledForHoard = true } };
            s.GetRelicMaterialPreferences().Cycles = 7;
            var next = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(s))!;
            Require(next.GetRelicStartFloor() == 1 && next.RelicStrengthOnly && next.GetRelicMaterialPreferences().Cycles == 7);
            Require(next.GripExchange is { Threshold: 60, BatchCount: 1, EnabledForHoard: true });
            next.RelicStrengthOnly = false;
            Require(next.ConfigureGripExchange(Plan(1, 1)).GripExchange == new GripExchangeOptions(60, 1));
        });
        Check("one-to-fifty-contains-five-complete-groups", () =>
        {
            var p = Plan(1, 1);
            Require(p is { StartFloor: 1, EndFloor: 50, RelicMaterial: true, FreeExploration: false });
            foreach (int floor in new[] { 1, 11, 21, 31, 41 }) Require(RelicFarmingPolicy.ContainsEntry(p, floor));
            foreach (int floor in new[] { 0, 2, 10, 50, 51, 61 }) Require(!RelicFarmingPolicy.ContainsEntry(p, floor));
            foreach (var invalid in new[] { p with { EndFloor = null }, p with { EndFloor = 60 }, p with { DungeonId = 2 },
                p with { DungeonId = 4 }, p with { StartFloor = 11 }, p with { FreeExploration = true }, p with { OpenSilver = false } })
                Require(!RelicFarmingPolicy.IsValid(invalid));
        });
        Check("five-groups-one-cycle-owned-save-kept-until-fifty", () =>
        {
            var plan = Plan(1, 1);
            var session = new FarmingSession(plan, 0);
            int complete = 0;
            for (int round = 0; round < 3; round++)
            {
                if (round > 0) session.BindCreatedSave(0);
                foreach (int entry in new[] { 1, 11, 21, 31, 41 })
                {
                    Require(session.NextFloor == entry && session.OwnedSlot == 0);
                    var decision = FarmingSessionPolicy.Decide(plan, new(entry, true, false, false, false));
                    Require(decision.Error.Length == 0 && !decision.RecoverFailure && decision.CountCycle == (entry == 41));
                    Require(decision.DeleteOwnedSave == (entry == 41) && decision.NextFloor == (entry == 41 ? 1 : entry + 10));
                    if (decision.CountCycle) complete++;
                    if (decision.DeleteOwnedSave) session.ConfirmOwnedSaveDeleted();
                    session.CompleteAttempt(decision, 0);
                    if (entry < 41)
                    {
                        var rows = new[] { Slot(0, entry + 10), new SaveSlotSnapshot(1, true, 0, "") };
                        Require(session.PrepareSource().Observe(rows, out var source, out _) && source.Index == 0);
                    }
                }
            }
            Require(complete == 3 && session.NextFloor == 1 && session.OwnedSlot == -1);
        });
        foreach (int entry in new[] { 1, 11, 21, 31, 41 })
        {
            int floor = entry;
            Check("failure-at-" + floor + "-retries-enterable-or-restarts-unenterable", () =>
            {
                var p = Plan(1, 1);
                var decision = FarmingSessionPolicy.Decide(p, new(floor, false, true, false, false));
                Require(!decision.CountCycle && decision.RecoverFailure && decision.DeleteOwnedSave && decision.NextFloor == 1);
                var source = Slot(0, floor);
                var resume = FailedAttemptRecoveryPolicy.Reconcile(p, decision, source);
                Require(!resume.CountCycle && resume.RecoverFailure && !resume.DeleteOwnedSave && resume.NextFloor == floor);
                Require(FailedAttemptRecoveryPolicy.Reconcile(p, decision, source with { Enterable = false }) == decision);
            });
        }
        Check("only-final-completed-group-can-exchange", () =>
        {
            var p = Plan(1, 1) with { GripExchange = new() };
            foreach (int entry in new[] { 1, 11, 21, 31, 41 })
            {
                var result = new FarmingAttemptResult(entry, true, false, false, false);
                Require(AetherpoolExchangePolicy.ShouldRunPostExitExchange(p, false, false, false, result) == (entry == 41));
                Require(!AetherpoolExchangePolicy.ShouldRunPostExitExchange(p, false, false, false, result with { DutyFailed = true }));
            }
            Require(!AetherpoolExchangePolicy.ShouldRunPostExitExchange(p, false, false, false, new(51, true, false, false, false)));
        });
        Check("strength-only-skips-exchange-for-every-relic-dungeon-and-preset", () =>
        {
            foreach (var (dungeon, start) in new[] { (1u, 1), (1u, 51), (2u, 21), (4u, 21) })
            {
                var s = new FarmingSettings { DungeonId = dungeon, RelicMaterial = true, RelicStrengthOnly = true,
                    GripExchange = new() { EnabledForHoard = true } };
                var configured = s.ConfigureGripExchange(Plan(dungeon, start) with { GripExchange = new() });
                Require(configured.GripExchange == null && RelicFarmingPolicy.IsValid(configured));
                var result = new FarmingAttemptResult(RelicFarmingPolicy.GetEndFloor(configured) - 9, true, false, false, false);
                Require(!AetherpoolExchangePolicy.ShouldRunPostExitExchange(configured, false, false, false, result));
                Require(FarmingSessionPolicy.Decide(configured, result).CountCycle);
            }
        });
        Check("first-floor-low-aetherpool-not-blocked-by-51-entry-reserve", () =>
        {
            Require(RelicFarmingPolicy.EntryReserve(Plan(1, 1)) == 0);
            Require(RelicFarmingPolicy.EntryReserve(Plan(1, 51)) == 14);
            Require(RelicFarmingPolicy.EntryReserve(Plan(2, 21)) == 14);
            Require(RelicFarmingPolicy.EntryReserve(Plan(4, 21)) == 5);
            Require(AetherpoolExchangePolicy.ComputePlannedExchanges(50, 50, new(), 14) == 3);
        });
        Check("preset-and-no-exchange-do-not-leak-to-other-modes-or-dungeons", () =>
        {
            var s = new FarmingSettings { DungeonId = 2, RelicPotd1To50 = true, RelicStrengthOnly = true,
                GripExchange = new() { EnabledForHoard = true, Threshold = 60, BatchCount = 1 } };
            Require(s.GetRelicStartFloor() == 21);
            s.DungeonId = 4; Require(s.GetRelicStartFloor() == 21);
            var hoard = new FarmingPlan(FarmingMode.Hoard, SaveUse.Create, 21, 2, false, true, true, true, true);
            Require(s.ConfigureGripExchange(hoard).GripExchange == new GripExchangeOptions(60, 1));
            Require(s.ConfigureGripExchange(hoard with { StartFloor = 31 }).GripExchange == null);
            Require(s.ConfigureGripExchange(hoard with { Mode = FarmingMode.DeepProgression }).GripExchange == null);
        });
        Check("pause-midrange-keeps-save-and-exchange-preference", () =>
        {
            var p = Plan(1, 1);
            foreach (int next in new[] { 11, 21, 31, 41 })
            {
                var checkpoint = new FloorsetPauseCheckpoint(123, 31, p, FloorsetResumeKind.ContinueSave, Slot(0, next), 0, 0);
                Require(checkpoint.IsValid());
                var loaded = JsonSerializer.Deserialize<FloorsetPauseCheckpoint>(JsonSerializer.Serialize(checkpoint))!;
                Require(loaded.IsValid() && loaded.Plan.EndFloor == 50 && loaded.Plan.GripExchange == null);
                Require(!(checkpoint with { Slot = Slot(0, 51) }).IsValid());
            }
        });
        Check("in-duty-selected-preset-and-no-exchange-are-carried-into-plan", () =>
        {
            foreach (int floor in new[] { 1, 11, 21, 31, 41, 51 })
            {
                var settings = new FsdSettings(); settings.Farming.DungeonId = 1;
                settings.Farming.RelicMaterial = true; settings.Farming.RelicPotd1To50 = true;
                settings.Farming.RelicStrengthOnly = true;
                var engine = (FsdEngine)RuntimeHelpers.GetUninitializedObject(typeof(FsdEngine));
                typeof(FsdEngine).GetField("_configuration", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(engine, settings);
                object?[] args = [1u, floor, 123ul, 31u, null, null];
                bool ok = (bool)typeof(FsdEngine).GetMethod("TryCreateContinuationPlan", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(engine, args)!;
                Require(ok == (floor <= 41));
                if (ok)
                {
                    var plan = (FarmingPlan)args[4]!;
                    Require(plan is { StartFloor: 1, EndFloor: 50, GripExchange: null, ManagedContinuation: true });
                    Require(TaskContinuationPolicy.IsValidManagedPlan(plan));
                }
            }
        });
        Check("strength-and-target-chests-apply-beyond-first-ten", () =>
        {
            var p = Plan(1, 1);
            var ready = new RelicOffenseItemSnapshot(1, 49, true, true, true, true, false, false, false, false, false, false, false);
            Require(RelicOffenseItemPolicy.Decide(p, ready) == RelicOffenseItem.Strength);
            Require(RelicOffenseItemPolicy.Decide(p, ready with { Floor = 51 }) == RelicOffenseItem.None);
            var session = new FarmingSession(p);
            Require(!session.TrySetTargets(new(false, true, false, false)) && session.OpenSilver);
            Require(session.TrySetTargets(new(true, true, true, true)) && session.BandedEnabled);
        });
        Check("retain-save-and-abort-never-delete-or-restart", () =>
        {
            var p = TaskContinuationPolicy.WithRetention(Plan(1, 1), true);
            Require(p.TargetCycles == 1 && !p.Infinite);
            var middle = FarmingSessionPolicy.Decide(p, new(11, true, false, false, false));
            Require(!middle.CountCycle && !middle.DeleteOwnedSave && !middle.StopAfterAttempt && middle.NextFloor == 21);
            var final = FarmingSessionPolicy.Decide(p, new(41, true, false, false, false));
            Require(final.CountCycle && !final.DeleteOwnedSave && final.StopAfterAttempt);
            var abort = FarmingSessionPolicy.Decide(p, new(11, false, false, false, true));
            Require(!abort.CountCycle && !abort.DeleteOwnedSave && !abort.RecoverFailure && abort.StopAfterAttempt);
        });
        return passed;
    }

    private static FarmingPlan Plan(uint dungeon, int start)
    {
        Require(RelicFarmingPolicy.TryCreate(dungeon, SaveUse.Create, start, 3, false,
            new(false, true, true, false), out var plan, out _));
        return plan!;
    }
    private static SaveSlotSnapshot Slot(int index, int floor) => new(index, false, floor, floor.ToString(), "MCH")
        { RowFingerprint = new string('A', 64) };
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Relic preset regression."); }
}
