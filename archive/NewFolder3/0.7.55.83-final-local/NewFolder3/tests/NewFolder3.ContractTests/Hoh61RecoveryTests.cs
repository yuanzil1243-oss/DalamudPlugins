using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace NewFolder3;

internal static class Hoh61RecoveryTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Set(object o, string n, object v) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, v);
    private static object Call(object o, string n, params object[] a) => o.GetType().GetMethod(n, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, a)!;
    private static int Limit(object o) => (int)o.GetType().GetProperty("PathQueryLimit", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh61-local-query-exhaustion-escalates-without-target-failure", () => {
                using var f = new Fixture(); Set(f.Controller, "_pathQueries", 12);
                Check((bool)Call(f.Controller, "TryBeginRegionalReset", "no-safe-local-path-without-new-enemies"), "must try a different stage");
                Check(f.Context.CombatRecoveryBudget.TotalFailures == 0 && Limit(f.Controller) == 16, "search rejection is not a moved-and-failed attempt");
                Check(f.Events.SequenceEqual(new[] { "hoh-local-search-exhausted" }), "explicit transition evidence"); }),
            ("hoh61-regional-reset-does-not-repeat-on-the-same-failed-target", () => {
                using var f = new Fixture();
                Check((bool)Call(f.Controller, "TryBeginRegionalReset", "first"), "first reset");
                Call(f.Controller, "EndAttempt");
                Check(!(bool)Call(f.Controller, "TryBeginRegionalReset", "second"), "ending an unsuccessful attempt cannot renew the reset"); }),
            ("hoh61-target-reset-releases-regional-state", () => {
                using var f = new Fixture(); Call(f.Controller, "TryBeginRegionalReset", "first");
                f.Controller.ResetEngagedTargetProgress();
                Check((bool)Call(f.Controller, "TryBeginRegionalReset", "new-target"), "new target has its own bounded reset"); }),
            ("hoh61-local-candidates-not-limited-to-first-four", () => {
                using var f = new Fixture(); Check(Limit(f.Controller) == 12, "twelve local paths before regional stage"); }),
            ("hoh61-pt-and-boss-do-not-enter-regional-reset", () => {
                foreach (var (dungeon, floor) in new (uint, byte)[] { (4, 94), (2, 70), (1, 61) })
                { using var f = new Fixture(dungeon, floor); Check(Limit(f.Controller) == 4 && !(bool)Call(f.Controller, "TryBeginRegionalReset", "test"), "isolated from other dungeons and bosses"); } }),
            ("hoh61-close-idle-enemy-can-be-retreated-from", () => {
                Check(PassageCombatPolicy.SafeHohRecoveryPath(new[] { new Vector3(-4, 0, 0) }, Vector3.Zero,
                    new[] { new Vector3(.5f, 0, 0) }), "existing close envelope must not prevent retreat"); }),
            ("hoh61-close-idle-enemy-cannot-be-approached-or-crossed", () => {
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(new[] { new Vector3(4, 0, 0) }, Vector3.Zero,
                    new[] { new Vector3(.5f, 0, 0) }), "remove blanket two-meter exemption"); }),
            ("hoh61-distant-idle-enemy-still-blocks-route", () => {
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(new[] { new Vector3(20, 0, 0) }, Vector3.Zero,
                    new[] { new Vector3(10, 0, 0) }), "regional movement cannot pull a new enemy"); }),
            ("hoh61-rejection-reports-blocker-and-measured-clearance", () => {
                var enemy = new Vector3(10, 0, 0);
                Check(!PassageCombatPolicy.TrySafeHohRecoveryPath(new[] { new Vector3(20, 0, 0) }, Vector3.Zero,
                    new[] { enemy }, out var blocker, out float initial, out float nearest, out var reason) &&
                    blocker == enemy && initial == 10 && nearest == 0 && reason == "unengaged-enemy-on-local-path", "actionable rejection evidence"); }),
            ("hoh61-24-meter-candidate-not-rejected-by-22-meter-path-cap", () => {
                Check(PassageCombatPolicy.SafeHohRecoveryPath(new[] { new Vector3(24, 0, 0) }, Vector3.Zero,
                    Array.Empty<Vector3>()), "aligned with existing 35-meter endpoint path cap"); }),
            ("hoh61-path-still-has-length-and-height-bounds", () => {
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(new[] { new Vector3(36, 0, 0) }, Vector3.Zero, Array.Empty<Vector3>()), "length");
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(new[] { new Vector3(4, 4, 0) }, Vector3.Zero, Array.Empty<Vector3>()), "height"); }),
            ("hoh61-nonfinite-enemy-data-fails-closed", () => {
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(new[] { new Vector3(4, 0, 0) }, Vector3.Zero,
                    new[] { new Vector3(float.NaN, 0, 0) }), "no implicit safety through NaN"); }),
            ("hoh61-flight-no-stock-reserve-on-normal-floors", () => {
                foreach (int floor in new[] { 1, 21, 61, 81, 91 }) foreach (int stock in new[] { 1, 2, 3 })
                    Check(HohItemPolicy.AllowPomander(floor, FloorInitPlanner.FlightPomanderSlotIndex, stock, false, false, false), "free speed use subject to executor gates"); }),
            ("hoh61-flight-overcap-allowed-in-hoh-combat-only", () => {
                Check(ProgressionItemPolicy.AllowsCombat(5, false, FloorItemUsePurpose.GoldChestOvercap, 2), "free a full chest while fighting");
                Check(!ProgressionItemPolicy.AllowsCombat(5, false, FloorItemUsePurpose.GoldChestOvercap, 4), "PT unchanged"); }),
            ("hoh61-flight-before-boss-and-duplicate-effect-still-blocked", () => {
                foreach (int floor in new[] { 9, 29, 69, 89, 99 })
                    Check(!ProgressionItemPolicy.CanBenefitNextFloor(2, floor, 100), "no flight before boss or non-mob floor");
                var s = new GeneralAutoPomanderSnapshot { CanAttemptPomanderUse = true, FlightUsable = true, FlightActive = true, NextFloorIsMob = true };
                Check(!GeneralAutoPomanderPlanner.ShouldUseSlot(s, 5), "cannot use repeatedly while next-floor effect already active"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        internal readonly List<string> Events = new();
        internal readonly CombatPositionRecoveryController Controller;
        internal Fixture(uint dungeon = 2, byte floor = 61)
        {
            Context.FarmingPlan = new(FarmingMode.FreeExploration, SaveUse.Create, 1, 1, false, false, true, true, false);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty, dungeon);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty, floor);
            Controller = new(Context, null!, null!, null, Context.CombatRecoveryBudget, null, _ => { }, (n, _) => Events.Add(n));
        }
        public void Dispose() { Controller.Dispose(); Context.Dispose(); }
    }
}
