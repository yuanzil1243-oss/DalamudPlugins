using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace NewFolder3;

internal static class Hoh54CombatRecoveryTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static HohCombatRecoveryState Recovery()
    { var state = new HohCombatRecoveryState(); state.BeginAttempt(Vector3.Zero); return state; }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh54-blocked-evidence-starts-at-800ms", () => {
                var s = Recovery();
                for (int ms = 0; ms < 800; ms += 200) Check(!s.ShouldStart(Now.AddMilliseconds(ms), true, true, true), "debounce");
                Check(s.ShouldStart(Now.AddMilliseconds(800), true, true, true), "no 15-second fallback for confirmed obstruction"); }),
            ("hoh54-transient-obstruction-does-not-trigger", () => {
                var s = Recovery(); s.ShouldStart(Now, true, true, true); s.ShouldStart(Now.AddMilliseconds(600), true, true, false);
                Check(!s.ShouldStart(Now.AddMilliseconds(800), true, true, true), "clear observation resets the blocked interval"); }),
            ("hoh54-unknown-sight-without-output-falls-back-at-four-seconds", () => {
                var s = Recovery();
                for (int ms = 0; ms < 4000; ms += 200) Check(!s.ShouldStart(Now.AddMilliseconds(ms), true, true, false), "allow a normal attack window");
                Check(s.ShouldStart(Now.AddSeconds(4), true, true, false), "four seconds of continuous in-range attack opportunity"); }),
            ("hoh54-out-of-range-is-not-output-failure", () => {
                var s = Recovery();
                for (int ms = 0; ms <= 10000; ms += 200) Check(!s.ShouldStart(Now.AddMilliseconds(ms), false, true, true), "distant target must approach"); }),
            ("hoh54-no-attack-obligation-does-not-start-recovery", () => {
                var s = Recovery();
                for (int ms = 0; ms <= 10000; ms += 200) Check(!s.ShouldStart(Now.AddMilliseconds(ms), true, false, true), "do not pull a new target"); }),
            ("hoh54-range-loss-resets-fast-evidence", () => {
                var s = Recovery(); s.ShouldStart(Now, true, true, true); s.ShouldStart(Now.AddMilliseconds(600), false, true, true);
                Check(!s.ShouldStart(Now.AddMilliseconds(800), true, true, true), "not continuously blocked in range"); }),
            ("hoh54-evidence-gap-is-not-a-continuous-obstruction", () => {
                var s = Recovery(); s.ShouldStart(Now, true, true, true);
                Check(!s.ShouldStart(Now.AddSeconds(10), true, true, true), "loading or a suspended update cannot count as observations"); }),
            ("hoh54-clock-rollback-resets-evidence", () => {
                var s = Recovery(); s.ShouldStart(Now, true, true, true);
                Check(!s.ShouldStart(Now.AddSeconds(-1), true, true, true), "non-monotonic UTC fails closed"); }),
            ("hoh54-explicit-mechanic-suspension-requires-fresh-evidence", () => {
                var s = Recovery(); s.ShouldStart(Now, true, true, true); s.SuspendEvidence();
                Check(!s.ShouldStart(Now.AddSeconds(5), true, true, true), "required mechanic time does not accumulate"); }),
            ("hoh54-arrival-still-blocked-rejects-before-six-seconds", () => {
                var s = Recovery();
                for (int ms = 0; ms < 800; ms += 200) Check(!s.StillBlockedAtArrival(Now.AddMilliseconds(ms), true, false), "verify obstruction");
                Check(s.StillBlockedAtArrival(Now.AddMilliseconds(800), true, false), "do not wait six seconds at another blocked point"); }),
            ("hoh54-arrival-clear-keeps-normal-attack-window", () => {
                var s = Recovery(); s.StillBlockedAtArrival(Now, true, false); s.StillBlockedAtArrival(Now.AddMilliseconds(600), false, false);
                Check(!s.StillBlockedAtArrival(Now.AddMilliseconds(800), true, false), "clear or unknown geometry cancels early rejection"); }),
            ("hoh54-arrival-protects-normal-cast", () => {
                var s = Recovery(); s.StillBlockedAtArrival(Now, true, false);
                Check(!s.StillBlockedAtArrival(Now.AddSeconds(5), true, true), "casting is not a failed position");
                Check(!s.StillBlockedAtArrival(Now.AddMilliseconds(5200), true, false), "fresh evidence after casting"); }),
            ("hoh54-arrival-gap-cannot-spend-a-position", () => {
                var s = Recovery(); s.StillBlockedAtArrival(Now, true, false);
                Check(!s.StillBlockedAtArrival(Now.AddSeconds(3), true, false), "no observations during gap"); }),
            ("hoh54-first-working-short-step-remains-eligible", () => {
                var s = Recovery(); Check(s.AllowsCandidate(Vector3.Zero, new(2, .2f, 0), false), "ordinary wall-edge step remains valid"); }),
            ("hoh54-safe-exit-starts-at-four-meters", () => {
                var s = Recovery(); Check(!s.AllowsCandidate(Vector3.Zero, new(3, 0, 0), true) &&
                    s.AllowsCandidate(Vector3.Zero, new(4, 0, 0), true), "exit leg must leave the immediate stuck area"); }),
            ("hoh54-first-failed-position-expands-from-original-obstruction", () => {
                var s = Recovery(); s.RejectPosition(new(3, 0, 0));
                Check(s.MinimumOriginDistance == 4 && !s.AllowsCandidate(new(3, 0, 0), new(0, 0, 3), false) &&
                    s.AllowsCandidate(new(3, 0, 0), new(0, 0, 4), false), "replanning is measured from the original obstruction"); }),
            ("hoh60-failures-expand-the-search-to-eight-twelve-and-sixteen", () => {
                var s = Recovery(); s.RejectPosition(new(3, 0, 0)); s.RejectPosition(new(0, 0, 4));
                Check(s.MinimumOriginDistance == 8 && !s.AllowsCandidate(new(0, 0, 4), new(-5, 0, 0), false), "second failure eight meters");
                s.RejectPosition(new(-8, 0, 0)); Check(s.MinimumOriginDistance == 12 &&
                    s.AllowsCandidate(new(-8, 0, 0), new(0, 0, -12), false), "third failure twelve meters");
                s.RejectPosition(new(0, 0, -12)); Check(s.MinimumOriginDistance == 16 &&
                    s.AllowsCandidate(new(0, 0, -12), new(16, 0, 0), false), "fourth failure sixteen meters"); }),
            ("hoh54-new-search-does-not-forget-origin-or-failed-area", () => {
                var s = Recovery(); s.RejectPosition(new(3, 0, 0)); s.BeginAttempt(new(3, .2f, 0));
                Check(s.Origin == Vector3.Zero && s.FailedPositions == 1 &&
                    !s.AllowsCandidate(new(3, 0, 0), new(4.5f, 0, 0), false), "exclude a two-meter failed region across replans/attempts"); }),
            ("hoh54-height-only-motion-is-not-an-exit", () => {
                var s = Recovery(); Check(!s.AllowsCandidate(Vector3.Zero, new(0, .19f, 0), false) &&
                    !s.AllowsCandidate(Vector3.Zero, new(.5f, 7, 0), true), "burrow and bridge height do not count as horizontal progress"); }),
            ("hoh60-candidates-remain-within-expanded-24-meter-area", () => {
                var s = Recovery(); Check(s.AllowsCandidate(new(10, 0, 0), new(18, 0, 0), false) &&
                    !s.AllowsCandidate(new(10, 0, 0), new(35, 0, 0), false), "expanded area remains anchored and bounded"); }),
            ("hoh54-invalid-candidates-fail-closed", () => {
                var s = Recovery(); Check(!s.AllowsCandidate(Vector3.Zero, new(float.NaN, 0, 0), false) &&
                    !s.AllowsCandidate(new(float.PositiveInfinity, 0, 0), Vector3.One, false), "finite positions required"); }),
            ("hoh54-normal-first-step-keeps-nearest-output-point", () => {
                var s = Recovery(); Check(s.CompareCandidates(Vector3.Zero, new(3, 0, 0), false, false,
                    new(9, 0, 0), false, true) < 0, "a first short working output step is allowed"); }),
            ("hoh54-after-failure-wide-platform-ranks-before-near-bridge-point", () => {
                var s = Recovery(); s.RejectPosition(new(3, 0, 0));
                Check(s.CompareCandidates(Vector3.Zero, new(6, 0, 0), false, false,
                    new(9, 0, 0), false, true) > 0, "platform width takes precedence after failed short relocation"); }),
            ("hoh54-safe-exit-prefers-a-platform-on-the-first-leg", () => {
                var s = Recovery(); Check(s.CompareCandidates(Vector3.Zero, new(4, 0, 0), true, false,
                    new(6, 0, 0), true, true) > 0, "nearest bridge point is not automatically the best exit"); }),
            ("hoh54-verified-firing-candidate-still-ranks-before-exit-only", () => {
                var s = Recovery(); s.RejectPosition(new(3, 0, 0));
                Check(s.CompareCandidates(Vector3.Zero, new(9, 0, 0), false, false,
                    new(4, 0, 0), true, true) < 0, "existing output validation is retained"); }),
            ("hoh54-safe-exit-count-is-bounded-and-remembers-landed-region", () => {
                var s = Recovery(); s.CompleteSafeExit(new(4, 0, 0)); s.BeginAttempt(new(4, 0, 0));
                Check(s.CanUseSafeExit && !s.AllowsCandidate(new(4, 0, 0), new(5, 0, 0), false), "no return to the same exit area");
                s.CompleteSafeExit(new(0, 0, 6)); Check(s.CanUseSafeExit && s.SafeExits == 2, "four measured exits are allowed");
                s.CompleteSafeExit(new(-8, 0, 0)); s.CompleteSafeExit(new(0, 0, -12));
                Check(!s.CanUseSafeExit && s.SafeExits == 4, "no unlimited wandering"); }),
            ("hoh54-verified-output-resets-escalation-and-evidence", () => {
                var s = Recovery(); s.RejectPosition(new(3, 0, 0)); s.ShouldStart(Now, true, true, true); s.ObserveOutput();
                Check(s.Origin == null && s.FailedPositions == 0 && s.SafeExits == 0 &&
                    !s.ShouldStart(Now.AddMilliseconds(800), true, true, true), "successful output starts a fresh episode"); }),
            ("hoh54-mechanics-suspension-preserves-obstruction-memory", () => {
                var s = Recovery(); s.RejectPosition(new(3, 0, 0)); s.SuspendEvidence();
                Check(s.Origin == Vector3.Zero && s.MinimumOriginDistance == 4, "a dodge cannot reset short-step escalation"); }),
            ("hoh54-long-safe-bridge-ramp-keeps-ground-validation", () => {
                Vector3[] path = [new(6, .4f, 0), new(12, 0, 0)];
                Check(CombatPositionPolicy.HasContinuousGround(path, Vector3.Zero, p => p, (_, _) => true, out var step) && step < .1f,
                    "a continuous bridge ramp remains traversable"); }),
            ("hoh54-expanded-route-cannot-snap-seven-meters-below-bridge", () => {
                Check(!CombatPositionPolicy.HasContinuousGround([new(9, 0, 0)], Vector3.Zero,
                    p => p - new Vector3(0, 7, 0), (_, _) => true, out _), "ground and packet offset remain separate"); }),
            ("hoh54-expanded-route-cannot-cross-gap-or-wall", () => {
                Check(!CombatPositionPolicy.HasContinuousGround([new(9, 0, 0)], Vector3.Zero,
                    p => p.X is > 3 and < 5 ? null : p, (_, _) => true, out _) &&
                    !CombatPositionPolicy.HasContinuousGround([new(9, 0, 0)], Vector3.Zero, p => p, (_, _) => false, out _), "continuous safe route required"); }),
            ("hoh54-expanded-route-does-not-pull-idle-enemies", () => {
                Check(!PassageCombatPolicy.SafeLocalPath([new(9, 0, 0)], Vector3.Zero, [new(6, 0, 1)]), "distance is no exemption from no-new-pull safety"); }),
            ("hoh54-controller-uses-fast-start-on-normal-hoh-floor", () => {
                using var f = new Fixture(2, 81); for (int ms = 0; ms < 800; ms += 200)
                    Check(!f.Start(Now.AddMilliseconds(ms), CombatSight.Blocked), "controller debounce");
                Check(f.Start(Now.AddMilliseconds(800), CombatSight.Blocked), "controller dispatches to HoH evidence"); }),
            ("hoh54-controller-uses-four-second-in-range-fallback", () => {
                using var f = new Fixture(2, 74); for (int ms = 0; ms < 4000; ms += 200)
                    Check(!f.Start(Now.AddMilliseconds(ms), CombatSight.Unknown), "controller fallback window");
                Check(f.Start(Now.AddSeconds(4), CombatSight.Unknown), "HoH unknown sight fallback"); }),
            ("hoh54-controller-rejects-blocked-arrival-only-in-hoh", () => {
                using var f = new Fixture(2, 81); Check(!f.Reject(Now) && f.Reject(Now.AddMilliseconds(800)), "fast arrival rejection wired"); }),
            ("hoh54-controller-enforces-original-area-and-platform-ranking", () => {
                using var f = new Fixture(2, 81); f.State.BeginAttempt(Vector3.Zero); f.State.RejectPosition(new(3, 0, 0));
                Check(!f.Allows(new(3, 0, 0), new(4.5f, 0, 0)), "failed region applied by actual controller");
                Check(f.Compare(new(6, 0, 0), new(9, 0, 0)) > 0, "wide platform ranks first after failure"); }),
            ("hoh54-controller-preserves-legacy-blocked-start-and-fifteen-second-fallback", () => {
                foreach (var (dungeon, floor) in new (uint, byte)[] { (4, 94), (1, 81), (3, 81), (2, 90) })
                {
                    using var f = new Fixture(dungeon, floor);
                    Check(!f.Start(Now.AddMilliseconds(800), CombatSight.Blocked) && f.Start(Now.AddSeconds(1), CombatSight.Blocked), "legacy one second since verified output");
                    Check(!f.Start(Now.AddSeconds(14), CombatSight.Unknown) && f.Start(Now.AddSeconds(15), CombatSight.Unknown), "legacy unknown fallback unchanged");
                } }),
            ("hoh54-controller-preserves-legacy-candidates-and-arrival-policy", () => {
                foreach (var (dungeon, floor) in new (uint, byte)[] { (4, 94), (1, 81), (3, 81), (2, 90) })
                {
                    using var f = new Fixture(dungeon, floor); f.State.BeginAttempt(Vector3.Zero); f.State.RejectPosition(new(3, 0, 0));
                    Check(f.Allows(Vector3.Zero, new(2, 0, 0)) && f.Compare(new(6, 0, 0), new(9, 0, 0)) < 0 &&
                        !f.Reject(Now) && !f.Reject(Now.AddSeconds(1)), "HoH evidence and sorting cannot leak into other policies");
                } }),
            ("hoh54-legacy-trigger-matrix-matches-the-original-expression", () => {
                foreach (var (dungeon, floor) in new (uint, byte)[] { (4, 94), (1, 81), (3, 81), (2, 90) })
                {
                    using var f = new Fixture(dungeon, floor);
                    foreach (bool aggro in new[] { false, true })
                    foreach (bool inRange in new[] { false, true })
                    foreach (bool hold in new[] { false, true })
                    foreach (bool postDodge in new[] { false, true })
                    foreach (var sight in Enum.GetValues<CombatSight>())
                    foreach (double age in new[] { 0, .5, 1, 14, 15, 16 })
                    {
                        f.ConfigureLegacy(age, hold);
                        bool stalled = (aggro ? age : hold ? 5 : 0) >= 15;
                        bool original = !((!inRange || sight != CombatSight.Blocked || age < 1) && !stalled && !postDodge);
                        Check(f.StartFull(Now, inRange, sight, aggro, hold, postDodge) == original,
                            "shared controller must retain original legacy truth table");
                    }
                } }),
            ("hoh60-hoh-flat-seeds-include-expanded-rings-and-remove-duplicate-heights", () => {
                using var f = new Fixture(2, 81); var seeds = f.Seeds();
                Check(seeds.Count == 65 && seeds.Any(p => p == new Vector3(4, 0, 0)) && seeds.Any(p => p == new Vector3(24, 0, 0)), "expanded search rings remain bounded without duplicate heights"); }),
            ("hoh54-legacy-candidate-seeds-are-byte-for-byte-equivalent-vectors", () => {
                foreach (var (dungeon, floor) in new (uint, byte)[] { (4, 94), (1, 81), (3, 81), (2, 90) })
                {
                    using var f = new Fixture(dungeon, floor); var expected = new List<Vector3>();
                    for (int height = 0; height < 4; height++)
                    {
                        expected.Add(Vector3.Zero);
                        for (int radius = 3; radius <= 12; radius += 3)
                            foreach (var d in CombatPositionPolicy.Directions) expected.Add(new(d.X * radius, 0, d.Y * radius));
                    }
                    Check(f.Seeds().SequenceEqual(expected), "original 132 candidates and their order are preserved");
                } }),
            ("hoh54-controller-target-reset-clears-obstruction-memory", () => {
                using var f = new Fixture(2, 81); f.State.BeginAttempt(Vector3.Zero); f.State.RejectPosition(new(3, 0, 0));
                f.Controller.ResetEngagedTargetProgress(); Check(f.State.Origin == null && f.State.FailedPositions == 0, "new target does not inherit the previous bridge"); })
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        internal readonly CombatPositionRecoveryController Controller;
        internal HohCombatRecoveryState State => (HohCombatRecoveryState)Field("_hohRecovery");
        internal Fixture(uint dungeon, byte floor)
        {
            Context.FarmingPlan = new(FarmingMode.FreeExploration, SaveUse.Create, 1, 1, false, false, true, true, false);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty, floor);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty, dungeon);
            Controller = new(Context, null!, null!, null, new CombatRecoveryBudget(), null, _ => { }, (_, _) => { });
            Set("_lastDamageAt", Now); Set("_lastProgressAt", Now); Set("_startPosition", Vector3.Zero);
        }
        private object Field(string name) => typeof(CombatPositionRecoveryController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Controller)!;
        private void Set(string name, object value) => typeof(CombatPositionRecoveryController).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Controller, value);
        private object Call(string name, params object[] args) => typeof(CombatPositionRecoveryController).GetMethod(name,
            BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Controller, args)!;
        internal bool Start(DateTime now, CombatSight sight) => (bool)Call("ShouldStartRecovery", now, true, sight, true, true, false);
        internal bool StartFull(DateTime now, bool inRange, CombatSight sight, bool aggro, bool attackOpportunity, bool postDodge) =>
            (bool)Call("ShouldStartRecovery", now, inRange, sight, aggro, attackOpportunity, postDodge);
        internal void ConfigureLegacy(double age, bool hold)
        {
            Set("_lastProgressAt", Now.AddSeconds(-age)); Set("_lastDamageAt", Now.AddSeconds(-age));
            var window = (EnemyChaseAttackWindow)Field("_attackWindow"); window.Reset();
            if (hold) window.Observe(true, false, Now.AddSeconds(-5));
        }
        internal List<Vector3> Seeds()
        { Call("AddSeeds", Vector3.Zero, 0f, 0f); return (List<Vector3>)Field("_seeds"); }
        internal bool Reject(DateTime now) => (bool)Call("ShouldRejectBlockedArrival", now, CombatSight.Blocked, false);
        internal bool Allows(Vector3 player, Vector3 point) => (bool)Call("AllowsRecoveryCandidate", player, point, false);
        internal int Compare(Vector3 a, Vector3 b)
        { ((HashSet<Vector3>)Field("_wideCandidates")).Add(b); return (int)Call("CompareRecoveryCandidates", a, b); }
        public void Dispose() { Controller.Dispose(); Context.Dispose(); }
    }
}
