using System.Reflection;
using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using Dalamud.Plugin.Services;

namespace NewFolder3;

internal static class HohLocalTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);
    private static FarmingPlan Plan => new(FarmingMode.FreeExploration, SaveUse.Create, 21, 1, false, false, true, true, false)
        { DungeonId = 2, StopAfterFloor = 100, UsesDiagnosticCheckpoints = true };
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static bool Create(int floor, uint dungeon, out FarmingPlan? plan) => FarmingPlan.TryCreate(
        FarmingMode.FreeExploration, SaveUse.Create, floor, 1, false, false, true, true, false, out plan, out _, dungeon);
    private static bool Allow(int floor, uint slot, int count = 1, bool boss = false, bool emergency = false, bool overflow = false) =>
        HohItemPolicy.AllowPomander(floor, slot, count, boss, emergency, overflow);
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh-catalog-ten-unique-territories", () => {
                uint[] territories = [770, 771, 772, 782, 773, 783, 774, 784, 775, 785];
                Check(DungeonCatalog.HeavenOnHigh.TerritoryFloorRanges.Count == 10, "exactly 100 floors");
                for (int i = 0; i < 10; i++) {
                    Check(DungeonCatalog.TryGetByTerritoryId(territories[i], out var d) && d.DungeonId == 2, "correct territory identity");
                    Check(d.TerritoryFloorRanges[territories[i]] == (i * 10 + 1, i * 10 + 10), "range must not shift"); } }),
            ("hoh-npc-and-new-starts", () => {
                var d = DungeonCatalog.HeavenOnHigh;
                Check(d.NpcDataId == 1025846 && d.FloorsetNeedleByStartFloor.Keys.Order().SequenceEqual(new[] { 1, 21 }), "HoH entry not PT"); }),
            ("hoh-plan-permits-only-new-1-or-21", () => {
                foreach (int f in new[] { 1, 21 }) Check(Create(f, 2, out var p) && p!.DungeonId == 2, "valid HoH start");
                foreach (int f in new[] { 11, 31, 51, 71, 91, 101 }) Check(!Create(f, 2, out _), "cannot invent high-floor new saves"); }),
            ("hoh-mode-and-hoard-scope", () => {
                Check(!FarmingPlan.TryCreate(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false, false, true, true, false, out _, out _, 2), "unsupported mode");
                Check(!FarmingPlan.TryCreate(FarmingMode.FreeExploration, SaveUse.Create, 21, 1, false, true, true, true, false, out _, out _, 2), "no hoard mode"); }),
            ("hoh-legacy-pt-defaults-unchanged", () => {
                Check(Create(71, 4, out var pt) && pt!.DungeonId == 4, "PT high checkpoint retained");
                Check(new FarmingSettings().DungeonId == 4 && new FsdFarmingRequest(FarmingMode.FreeExploration, SaveUse.Create, 31, 1, false, new(false, true, true, false)).DungeonId == 4, "legacy defaults"); }),
            ("hoh-preferences-isolated-from-pt", () => {
                var s = new FarmingSettings { Mode = FarmingMode.FreeExploration }; var legacy = new FsdSettings();
                var pt = s.GetPreferences(legacy); pt.Cycles = 3; pt.PreserveSaveOnCompletion = true;
                s.DungeonId = 2; var hoh = s.GetPreferences(legacy);
                Check(!ReferenceEquals(pt, hoh) && !hoh.PreserveSaveOnCompletion && hoh.BandedEnabled == false, "no PT bleed");
                hoh.Cycles = 5; s.DungeonId = 4; Check(s.GetPreferences(legacy).Cycles == 3, "PT untouched"); }),
            ("hoh-entry-settings-roundtrip-separate", () => {
                var s = new FarmingSettings { DungeonId = 2, Mode = FarmingMode.FreeExploration };
                s.ActiveCheckpointEntry.ResumeSlot = 1; s.ActiveCheckpointEntry.StopAfterFloor = 90;
                var copy = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(s))!;
                Check(copy.ActiveCheckpointEntry.StartFloor == 21 && copy.ActiveCheckpointEntry.ResumeSlot == 1 && copy.ActiveCheckpointEntry.StopAfterFloor == 90, "HoH persistence");
                copy.DungeonId = 4; Check(copy.ActiveCheckpointEntry.StartFloor == 31 && copy.ActiveCheckpointEntry.ResumeSlot == null, "PT persistence isolated"); }),
            ("hoh-menu-1-never-matches-21", () => {
                Check(HohLocalPolicy.SelectStartOption(["从21层进入", "从1层进入", "返回"], 1, out int i) == CheckpointMenuResult.Selected && i == 1, "exact complete number"); }),
            ("hoh-menu-order-and-fullwidth", () => {
                Check(HohLocalPolicy.SelectStartOption(["Back", "Enter from floor ２１", "Enter from floor １"], 21, out int i) == CheckpointMenuResult.Selected && i == 1, "normalize fullwidth/order"); }),
            ("hoh-menu-locked-and-duplicate-stop", () => {
                Check(HohLocalPolicy.SelectStartOption(["第1层", "返回"], 21, out _) == CheckpointMenuResult.Missing, "locked 21");
                Check(HohLocalPolicy.SelectStartOption(["第21层", "Floor 21"], 21, out int i) == CheckpointMenuResult.Ambiguous && i == -1, "duplicate"); }),
            ("hoh-menu-unrelated-is-not-a-start-menu", () => {
                Check(HohLocalPolicy.SelectStartOption(["固定队伍", "随机队伍"], 21, out _) == CheckpointMenuResult.NotFloorset, "party menu");
                Check(HohLocalPolicy.SelectStartOption(["1–10", "21–30"], 21, out _) == CheckpointMenuResult.NotFloorset, "unknown range format not silently chosen"); }),
            ("hoh-failure-restarts-at-actual-checkpoint", () => {
                foreach (int f in new[] { 31, 71, 91 }) {
                    var d = FarmingSessionPolicy.Decide(Plan, new(f, false, true, false, false));
                    Check(d.NextFloor == 21 && d.RecoverFailure && d.DeleteOwnedSave && !d.CountCycle, "not a PT71 restart"); }
                Check(FarmingSessionPolicy.Decide(Plan with { StartFloor = 1 }, new(1, false, true, false, false)).NextFloor == 1, "first group restarts1"); }),
            ("hoh-midgroup-continuation-retains-save", () => {
                var d = FarmingSessionPolicy.Decide(Plan, new(21, true, false, false, false));
                Check(d.NextFloor == 31 && !d.DeleteOwnedSave && !d.CountCycle, "continue owned slot"); }),
            ("hoh-stop-obeys-delete-or-preserve", () => {
                foreach (bool keep in new[] { false, true }) {
                    var d = FarmingSessionPolicy.Decide(Plan with { StopAfterFloor = 30, PreserveSaveOnCompletion = keep }, new(21, true, false, false, false));
                    Check(d.DeleteOwnedSave == !keep && !d.CountCycle && !d.RecoverFailure, "selected boundary"); } }),
            ("hoh-classifier-99-mob-and100-result", () => {
                Check(DeepDungeonFloorClassifier.Classify(2, 99) == DeepDungeonFloorKind.Mob && DeepDungeonFloorClassifier.Classify(2, 100) == DeepDungeonFloorKind.Result, "no boss100");
                Check(DeepDungeonFloorClassifier.Classify(2, 90) == DeepDungeonFloorKind.Boss && DeepDungeonFloorClassifier.Classify(4, 99) == DeepDungeonFloorKind.Boss, "PT unchanged"); }),
            ("hoh-shrine-arrival-or-old-completion-insufficient", () => {
                Check(!HohLocalPolicy.CanConfirmShrine(0, 4, false, 3, 4, 0, 5, false), "no interaction");
                Check(!HohLocalPolicy.CanConfirmShrine(4, 4, false, 3, 3, 5, 5, false), "old completion/exit"); }),
            ("hoh-shrine-requires-same-object-and-state-change", () => {
                Check(!HohLocalPolicy.CanConfirmShrine(4, 8, false, 3, 4, 0, 5, false), "identity changed");
                Check(!HohLocalPolicy.CanConfirmShrine(4, 4, true, 3, 4, 0, 5, false), "still targetable");
                Check(!HohLocalPolicy.CanConfirmShrine(4, 4, false, 3, 4, 0, 5, true), "failed overrides"); }),
            ("hoh-shrine-accepts-fresh-completion-or-new-exit", () => {
                Check(HohLocalPolicy.CanConfirmShrine(4, 4, false, 3, 4, 5, 5, false), "fresh completion");
                Check(HohLocalPolicy.CanConfirmShrine(4, 4, false, 3, 3, 0, 5, false), "new exit"); }),
            ("hoh-serenity-reserve-two-before91-release-only-by-deep-ban-adapter", () => {
                foreach (int f in new[] { 21, 81, 84, 90 }) Check(!Allow(f, 10) && !Allow(f, 10, 2) && Allow(f,10,3), "keep2");
                Check(Allow(91,10) && Allow(99,10), "91plus stock is released subject to item-ban adapter"); }),
            ("hoh-resurrection-starts71", () => Check(!Allow(69, 14, 3, overflow: true) && !Allow(70, 14) && Allow(71, 14) && Allow(99, 14), "last three groups")),
            ("hoh-uncoordinated-items-remain-disabled", () => {
                foreach (uint slot in new uint[] { 6, 12, 13 }) Check(!Allow(94, slot, 3, emergency: true, overflow: true), "do not waste unsafely"); }),
            ("hoh-every-boss-uses-strength-steel-and-frailty-when-stocked", () => {
                foreach (int floor in new[] { 10, 20, 30, 40, 50, 60, 70, 80, 90 })
                    foreach (uint slot in new uint[] { 2, 3, 11 }) Check(Allow(floor, slot, boss: true), "fixed boss combination");
                Check(!Allow(84, 11, emergency: true) && !Allow(84, 11) && Allow(84, 11, 3), "81-89 preserve two frailties even in emergency"); }),
            ("hoh-low-floor-frailty-only-relieves-confirmed-full-overcap", () => {
                foreach (int floor in Enumerable.Range(1, 80).Where(f => f % 10 != 0))
                {
                    Check(!Allow(floor, HohItemPolicy.Frailty, 1, overflow: true) &&
                        !Allow(floor, HohItemPolicy.Frailty, 2, overflow: true), "low floors must not spend reserved frailty stock to free space");
                    Check(!Allow(floor, HohItemPolicy.Frailty, 3), "a full bag alone is not an overcap event");
                    Check(Allow(floor, HohItemPolicy.Frailty, 3, overflow: true), "confirmed new frailty may replace one of three");
                }
                Check(ProgressionItemPolicy.AllowsCombat(HohItemPolicy.Frailty, false, FloorItemUsePurpose.GoldChestOvercap, 2),
                    "low-floor gold-overcap relief may dispatch during combat");
                Check(ProgressionItemPolicy.AllowsCombat(HohItemPolicy.Frailty, false, FloorItemUsePurpose.HohFrailty, 2) &&
                    !ProgressionItemPolicy.AllowsCombat(HohItemPolicy.Frailty, false, FloorItemUsePurpose.HohFrailty, 4),
                    "HoH frailty maintenance purpose does not leak into PT"); }),
            ("hoh-native-pomander-slots-match-game-data", () => {
                Check(HohItemPolicy.Frailty == 11 && HohItemPolicy.Petrification == 15, "DeepDungeon2 slots: item17/action11275, item19/action11277");
                Check(!Allow(50, 15, boss: true), "never use petrification as boss frailty"); }),
            ("hoh-buffs-enabled-on-later-normal-floors", () => {
                Check(!Allow(50, 2, 3) && Allow(61, 2, 2) && !Allow(70, 3, 3) && Allow(71, 3, 3), "late normal use");
                Check(Allow(60, 3, boss: true) && Allow(50, 3, boss: true), "all bosses allow steel"); }),
            ("hoh-flight-allows-speed-use-before-any-eligible-normal-floor", () => {
                foreach (int f in new[] { 83, 84, 85, 93, 94, 95 }) Check(Allow(f, 5), "preempt difficult floor");
                Check(Allow(82, 5) && Allow(96, 5), "user allows free flight use; next-floor boss gate remains separate"); }),
            ("hoh-next-floor-items-respect-boss-and-boundary", () => {
                foreach (uint slot in new uint[] { 4, 5, 6 }) {
                    Check(ProgressionItemPolicy.AffectsNextFloor(slot), "nextfloor semantics");
                    Check(!ProgressionItemPolicy.CanBenefitNextFloor(2, 89, 100), "no use beforeboss");
                    Check(!ProgressionItemPolicy.CanBenefitNextFloor(2, 99, 100), "no use before shrine"); } }),
            ("hoh-emergency-needs-living-player-and-attackers", () => {
                Check(!HohItemPolicy.IsEmergency(1, 100, 0) && !HohItemPolicy.IsEmergency(0, 100, 3) && !HohItemPolicy.IsEmergency(50, 0, 3), "no idle/dead/invalid trigger");
                Check(HohItemPolicy.IsEmergency(34, 100, 1) && HohItemPolicy.IsEmergency(69, 100, 2) && !HohItemPolicy.IsEmergency(70, 100, 2), "thresholds"); }),
            ("hoh-magicite-saves-two-before91", () => {
                foreach (int floor in new[] { 84, 85, 86 }) {
                    foreach (int stock in new[] { 1, 2 })
                        Check(!HohItemPolicy.ShouldUseMagicite(floor, stock, false, false, true, false), "reserve2");
                    Check(!HohItemPolicy.ShouldUseMagicite(floor, 3, false, false, true, false), "retain preferred magicite for combined four-item reserve"); }
                Check(HohItemPolicy.ShouldUseMagicite(98, 2, false, false, true, false), "last two floors release reserve"); }),
            ("hoh-magicite-all-three-retained-until-jinba-window", () => {
                int stock = 3;
                Check(!HohItemPolicy.ShouldUseMagicite(84, stock, false, false, true, false), "priority is to retain all three magicites");
                Check(!HohItemPolicy.ShouldUseMagicite(85, stock, false, false, true, false) &&
                    !HohItemPolicy.ShouldUseMagicite(86, stock, false, false, true, false), "ordinary early floors cannot spend the reserve");
                foreach (int floor in Enumerable.Range(91, 3))
                    Check(!HohItemPolicy.ShouldUseMagicite(floor, stock, false, false, true, false), "carry stock through93");
                Check(HohItemPolicy.ShouldUseMagicite(94, stock, false, false, true, false), "first reserved use in Jinba window");
                stock--;
                Check(HohItemPolicy.ShouldUseMagicite(95, stock, false, false, true, false), "second reserve can clear next dangerous floor"); }),
            ("hoh-magicite-third-loot-can-clear94through96-once-each", () => {
                int stock = 2;
                Check(!HohItemPolicy.ShouldUseMagicite(93, stock, false, false, true, false), "reserve before94");
                stock++;
                foreach (int floor in new[] { 94, 95, 96 }) {
                    Check(HohItemPolicy.ShouldUseMagicite(floor, stock, false, false, true, false), "one use scheduled for each final floor");
                    stock--;
                    Check(!HohItemPolicy.ShouldUseMagicite(floor, stock, false, false, true, true), "confirmed use prevents another automatic clear this floor");
                }
                Check(stock == 0, "three reserved stones consumed across three floors"); }),
            ("hoh-magicite-91through93-holds-even-three-unless-emergency", () => {
                foreach (int floor in Enumerable.Range(91, 3))
                    foreach (int stock in new[] { 1, 2, 3 }) {
                        Check(!HohItemPolicy.ShouldUseMagicite(floor, stock, false, false, true, false), "not on entry or normal combat");
                        Check(HohItemPolicy.ShouldUseMagicite(floor, stock, false, true, true, false), "survival may spend reserve");
                    }
                foreach (int floor in Enumerable.Range(94,6))
                    Check(HohItemPolicy.ShouldUseMagicite(floor, 1, false, false, true, false), "Jinba window and final leftovers release last stock"); }),
            ("hoh-magicite-gate-door-enemies-and-inventory", () => {
                foreach (int floor in new[] { 97, 98, 99 }) {
                    Check(!HohItemPolicy.ShouldUseMagicite(floor, 3, true, false, true, false), "door open");
                    Check(!HohItemPolicy.ShouldUseMagicite(floor, 3, false, false, false, false), "no enemies");
                    Check(!HohItemPolicy.ShouldUseMagicite(floor, 0, false, true, true, false), "no stock");
                } }),
            ("hoh-magicite-no-boss-or-result-or-repeat", () => {
                foreach (int f in new[] { 0, 80, 90, 100, 101 }) Check(!HohItemPolicy.ShouldUseMagicite(f, 3, false, true, true, false), "ordinaryfloors only");
                Check(!HohItemPolicy.ShouldUseMagicite(94, 3, false, true, true, true), "once per floor"); }),
            ("hoh-magicite-last-stock-emergency-and99", () => {
                Check(HohItemPolicy.ShouldUseMagicite(82, 1, false, true, true, false), "break reserve for survival");
                Check(HohItemPolicy.ShouldUseMagicite(99, 1, false, false, true, false) && HohItemPolicy.ShouldUseMagicite(97, 1, false, false, true, false), "do not save last stock beyond Jinba window or final combat floor"); }),
            ("hoh-pomander-state-limits-sight-witching-frailty", () => {
                var b = new HohFloorItemBudget();
                b.RecordPomanderConfirmed(1, Now); Check(!b.AllowsPomander(1, Now.AddHours(1)), "no repeated sight");
                b.RecordPomanderConfirmed(9, Now); Check(!b.AllowsPomander(9, Now.AddSeconds(59)) && b.AllowsPomander(9, Now.AddSeconds(60)), "witching cooldown");
                b.RecordPomanderConfirmed(11, Now); Check(!b.AllowsPomander(11, Now.AddSeconds(179)) && b.AllowsPomander(11, Now.AddMinutes(3)), "frailty duration"); }),
            ("hoh-floor-budget-resets-by-floor-instance", () => {
                var old = new HohFloorItemBudget(); old.RecordMagiciteConfirmed(); old.RecordPomanderConfirmed(1, Now);
                var next = new HohFloorItemBudget();
                Check(old.MagiciteConfirmed && !next.MagiciteConfirmed && next.AllowsPomander(1, Now), "separate floor instances"); }),
            ("hoh-magicite-dispatch-is-not-consumption-proof", () => {
                var s = new FloorItemUseSession(); var p = s.Begin(new(FloorItemUseKind.Stone, 2), FloorItemUsePurpose.HohMagicite, 2, Now, "test", false, 0, 0, 0, 0);
                Check(s.Observe(2, false, Now.AddMilliseconds(100)) != FloorItemUseConfirmationDecisionKind.Confirmed, "unchangedstock");
                Check(s.Observe(1, false, Now.AddMilliseconds(200)) == FloorItemUseConfirmationDecisionKind.Confirmed, "nativecount provesuse");
                s.Confirm(p); Check(s.Pending == null, "release channel"); }),
            ("hoh-native-rest-exit-identities-isolated", () => {
                uint Id(string name, DungeonData? d) { using var f = new PilgrimsTraverseRestExitFlow(dungeon: d); return (uint)typeof(PilgrimsTraverseRestExitFlow).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!; }
                Check(Id("RestExitEventObjDataId", DungeonCatalog.HeavenOnHigh) == 2009523 && Id("RestExitWarpRowId", DungeonCatalog.HeavenOnHigh) == 131298, "HoH exits");
                Check(Id("RestExitEventObjDataId", null) == 2014757 && Id("RestExitWarpRowId", null) == 131612, "PT defaults"); }),
            ("hoh-scenario-does-not-delete-while-terminal-unverified", () => {
                using var f = new Fixture(Plan); f.Context.TerminalRewardsRequired = true; f.Scenario.Update(null!);
                Check(f.Scenario.IsComplete && f.Context.StatusIsError && !f.Scenario.CountsAsCycle && f.Cleanup.Prepared == 0 && f.Session.OwnedSlot == 1, "stop and preserve until terminal proof exists"); }),
            ("hoh-scenario-complete-waits-for-verified-owned-cleanup", () => {
                using var f = new Fixture(Plan); f.Scenario.Update(null!);
                Check(f.Cleanup.Prepared == 1 && !f.Scenario.IsComplete, "wait for deletion");
                f.Cleanup.Ready = true; f.Scenario.Update(null!); f.Scenario.Update(null!);
                Check(f.Scenario.IsComplete && f.Scenario.CountsAsCycle && f.Session.OwnedSlot == -1, "verified owned cleanup"); }),
            ("hoh-scenario-complete-preserve-keeps-owned-slot", () => {
                using var f = new Fixture(Plan with { PreserveSaveOnCompletion = true }); f.Scenario.Update(null!);
                Check(f.Scenario.IsComplete && !f.Scenario.ShouldLoop && f.Scenario.CountsAsCycle && f.Cleanup.Prepared == 0 && f.Session.OwnedSlot == 1, "explicit preserve"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        internal readonly FarmingSession Session;
        internal readonly FarmingScenario Scenario;
        internal readonly CleanupFlow Cleanup = new();
        internal Fixture(FarmingPlan plan)
        {
            Session = new(plan, 1); Context.FarmingPlan = plan; Context.MarkDutyCompleted();
            Scenario = new(Session, deleteSaveFlow: Cleanup);
            Set("_context", Context); Set("_entered", true); Set("_entryFloor", 91); Set("_restDone", true);
        }
        private void Set(string field, object value) => typeof(FarmingScenario).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Scenario, value);
        public void Dispose() { Scenario.Dispose(); Context.Dispose(); }
    }
    private sealed class CleanupFlow : IDeleteSaveFlow
    {
        internal int Prepared; internal bool Ready;
        public void Prepare(RunContext context) => Prepared++;
        public bool Update(IFramework framework) => Ready;
    }
}
