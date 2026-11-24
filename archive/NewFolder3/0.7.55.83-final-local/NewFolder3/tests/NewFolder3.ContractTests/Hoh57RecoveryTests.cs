using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace NewFolder3;

internal static class Hoh57RecoveryTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static object Get(object o, string n) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    private static void Set(object o, string n, object v) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, v);
    private static object Call(object o, string n, params object[] a) => o.GetType().GetMethod(n, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, a)!;

    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh57-one-normal-long-cast-remains-protected", () => {
                var s = new HohRecoveryCastState();
                for (int ms = 0; ms < 12000; ms += 200)
                    Check(s.Observe(Now.AddMilliseconds(ms), true, true, 1, ms / 1000f), "a single normal cast must finish");
                Check(s.Observe(Now.AddSeconds(12), false, false, 0, 0), "allow damage to arrive after the cast");
                Check(!s.Observe(Now.AddSeconds(13), false, false, 0, 0), "end protection after one-second output grace"); }),
            ("hoh57-repeated-same-action-does-not-renew-protection-forever", () => {
                var s = new HohRecoveryCastState(); bool protect = true;
                for (int ms = 0; ms <= 5000; ms += 200)
                    protect = s.Observe(Now.AddMilliseconds(ms), true, true, 1, ms % 2000 / 1000f);
                Check(!protect && s.UnconfirmedCasts == 2, "two no-output casts, including back-to-back casts without an idle frame"); }),
            ("hoh57-two-offensive-actions-can-form-the-same-stall", () => {
                var s = new HohRecoveryCastState(); bool protect = true;
                for (int ms = 0; ms <= 5000; ms += 200)
                    protect = s.Observe(Now.AddMilliseconds(ms), true, true, (uint)(ms / 2000 + 1), ms % 2000 / 1000f);
                Check(!protect && s.UnconfirmedCasts == 2, "changing offensive action is not a new output episode"); }),
            ("hoh57-buffs-heals-and-unknown-actions-never-build-failed-cast-count", () => {
                var s = new HohRecoveryCastState();
                for (int ms = 0; ms <= 20000; ms += 200)
                    Check(s.Observe(Now.AddMilliseconds(ms), true, false, 1, ms % 2000 / 1000f), "non-offensive cast protected");
                Check(s.UnconfirmedCasts == 0, "not offensive attempts"); }),
            ("hoh57-verified-output-resets-previous-offensive-failures", () => {
                var s = Ready(); s.Reset();
                Check(s.Observe(Now.AddSeconds(6), true, true, 1, 0) && s.UnconfirmedCasts == 0, "a successful shot starts a fresh window"); }),
            ("hoh57-required-dodge-cannot-be-counted-as-failed-output", () => {
                using var f = new Fixture(); var casts = (HohRecoveryCastState)Get(f.Controller, "_hohCasts");
                Feed(casts); f.Controller.YieldMovement();
                Check(casts.UnconfirmedCasts == 0 && casts.Observe(Now.AddSeconds(6), true, true, 1, 0), "actual controller yields and resets cast evidence"); }),
            ("hoh57-target-reset-removes-cast-history", () => {
                using var f = new Fixture(); var casts = (HohRecoveryCastState)Get(f.Controller, "_hohCasts");
                Feed(casts); f.Controller.ResetEngagedTargetProgress();
                Check(casts.UnconfirmedCasts == 0 && !(bool)Get(f.Controller, "_castProtected"), "cannot interrupt a new enemy's first cast"); }),
            ("hoh57-sampling-gap-requires-fresh-cast-history", () => {
                var s = Ready(); Check(s.Observe(Now.AddSeconds(20), true, true, 1, 0) && s.UnconfirmedCasts == 0, "time offline is not cast evidence"); }),
            ("hoh57-clock-rollback-requires-fresh-cast-history", () => {
                var s = Ready(); Check(s.Observe(Now.AddSeconds(-1), true, true, 1, 0) && s.UnconfirmedCasts == 0, "clock rollback fails closed"); }),
            ("hoh57-nonoffensive-cast-protected-even-after-prior-failures", () => {
                var s = Ready(); Check(s.Observe(Now.AddMilliseconds(5200), true, false, 55, 0), "do not cancel a buff after an offensive stall"); }),
            ("hoh57-recovery-evidence-survives-cast-protection", () => {
                using var f = new Fixture(); Set(f.Controller, "_castProtected", true);
                for (int ms = 0; ms <= 1200; ms += 200) Check(!f.Start(Now.AddMilliseconds(ms)), "protect first cast");
                Set(f.Controller, "_castProtected", false);
                Check(f.Start(Now.AddMilliseconds(1400)), "the cast end must not reset accumulated obstruction evidence"); }),
            ("hoh57-floor49-repeated-cast-stall-starts-bounded-recovery", () => {
                using var f = new Fixture(); var casts = (HohRecoveryCastState)Get(f.Controller, "_hohCasts"); bool started = false;
                for (int ms = 0; ms <= 5000; ms += 200)
                {
                    Set(f.Controller, "_castProtected", casts.Observe(Now.AddMilliseconds(ms), true, true, 1, ms % 2000 / 1000f));
                    started |= f.Start(Now.AddMilliseconds(ms));
                }
                Check(started, "unchanged HP plus blocked geometry and repeated offensive casts cannot hold forever"); }),
            ("hoh57-arrival-obstruction-survives-the-next-cast", () => {
                using var f = new Fixture(); Set(f.Controller, "_castProtected", true);
                for (int ms = 0; ms <= 1000; ms += 200) Check(!f.Reject(Now.AddMilliseconds(ms)), "normal cast protected at arrival");
                Set(f.Controller, "_castProtected", false);
                Check(f.Reject(Now.AddMilliseconds(1200)), "no fresh 800ms delay after every cast"); }),
            ("hoh57-arrival-output-deadline-is-not-refreshed-by-casts", () => {
                using var f = new Fixture(); Set(f.Controller, "_verifyAt", Now); Set(f.Controller, "_castProtected", true);
                Check(!f.Expired(Now.AddSeconds(8)), "protect a single long normal cast");
                Set(f.Controller, "_castProtected", false);
                Check(f.Expired(Now.AddMilliseconds(8200)) && (DateTime)Get(f.Controller, "_verifyAt") == Now, "no six-second renewal after repeated casts"); }),
            ("hoh57-recovery-movement-state-is-scoped-to-hoh-normal-floors", () => {
                foreach (var (dungeon, floor, expected) in new (uint, byte, bool)[] { (2, 61, true), (2, 90, false), (4, 94, false), (1, 81, false), (3, 81, false) })
                {
                    using var f = new Fixture(dungeon, floor); f.Context.SetHohRecoveryMovement(f.Controller, true);
                    Check(f.Context.HohRecoveryMovementActive == expected && !f.Context.NormalCombatOutputSuppressed, "movement ownership is scoped; output must stay available");
                } }),
            ("hoh57-recovery-movement-owners-cannot-release-each-other", () => {
                using var f = new Fixture(); var other = new object();
                f.Context.SetHohRecoveryMovement(f.Controller, true); f.Context.SetHohRecoveryMovement(other, true);
                f.Controller.ResetEngagedTargetProgress(); Check(f.Context.HohRecoveryMovementActive, "other controller still holds its movement lease");
                f.Context.SetHohRecoveryMovement(other, false); Check(!f.Context.HohRecoveryMovementActive, "all owners released"); }),
            ("hoh57-bmr-yield-releases-recovery-movement-state", () => {
                using var f = new Fixture(); f.Context.SetHohRecoveryMovement(f.Controller, true); f.Controller.YieldMovement();
                Check(!f.Context.HohRecoveryMovementActive, "BMR owns urgent movement and recovery does not leave output disabled"); }),
            ("hoh57-end-attempt-and-dispose-release-output", () => {
                using var f = new Fixture(); f.Context.SetHohRecoveryMovement(f.Controller, true); Call(f.Controller, "EndAttempt");
                Check(!f.Context.HohRecoveryMovementActive, "release on arrival/failure/completion");
                f.Context.SetHohRecoveryMovement(f.Controller, true); f.Controller.Dispose(); Check(!f.Context.HohRecoveryMovementActive, "release on stop/floor disposal"); }),
            ("hoh57-expanded-search-can-reuse-a-closer-verified-firing-point", () => {
                var s = Escalated(); Vector3 point = new(0, 0, -3);
                Check(!s.AllowsCandidate(Vector3.Zero, point, false) && s.AllowsFiringFallback(Vector3.Zero, point), "nine-meter preference cannot veto a real clear shot"); }),
            ("hoh60-firing-fallback-remains-available-after-four-safe-exits", () => {
                var s = new HohCombatRecoveryState(); s.BeginAttempt(Vector3.Zero); s.CompleteSafeExit(new(4, 0, 0)); s.CompleteSafeExit(new(0, 0, 6));
                s.CompleteSafeExit(new(-8, 0, 0)); s.CompleteSafeExit(new(0, 0, -12));
                Check(!s.CanUseSafeExit && s.AllowsFiringFallback(new(0, 0, 6), new(-3, 0, 0)), "bounded exit quota does not veto independently verified output"); }),
            ("hoh57-fallback-cannot-repeat-a-failed-region", () => {
                var s = Escalated(); Check(!s.AllowsFiringFallback(Vector3.Zero, new(3.2f, 0, 0)), "two-meter failed areas remain excluded"); }),
            ("hoh60-search-covers-24m-but-remains-bounded", () => {
                var s = Escalated(); Check(s.AllowsCandidate(Vector3.Zero, new(20, 0, 0), false) &&
                    !s.AllowsCandidate(Vector3.Zero, new(25, 0, 0), false), "widen local search while retaining a hard limit"); }),
            ("hoh60-four-retries-are-scoped-to-hoh", () => {
                using var hoh = new Fixture(2, 61); using var pt = new Fixture(4, 94);
                var property = typeof(CombatPositionRecoveryController).GetProperty("MaximumTargetRecoveryFailures", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Check((int)property.GetValue(hoh.Controller)! == 4 && (int)property.GetValue(pt.Controller)! == 2,
                    "HoH gets four attempts while the Palace retains two"); }),
            ("hoh57-fallback-cannot-use-height-only-or-overlong-move", () => {
                var s = Escalated(); Check(!s.AllowsFiringFallback(Vector3.Zero, new(0, 7, 0)) &&
                    !s.AllowsFiringFallback(Vector3.Zero, new(25, 0, 0)) && !s.AllowsFiringFallback(Vector3.Zero, new(float.NaN, 0, 0)), "movement and widened search bounds remain finite"); }),
            ("hoh61-local-retreat-never-ignores-a-close-enemy-on-the-path", () => {
                Vector3 start = Vector3.Zero; Vector3[] path = [new(10, 0, 0)];
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(path, start, [new(.5f, 0, 0)]) &&
                    !PassageCombatPolicy.SafeLocalPath(path, start, [new(.5f, 0, 0)]), "crossing an existing close enemy is not a retreat");
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(path, start, [new(8, 0, 0)]), "farther unengaged enemies still block an approach"); }),
            ("hoh57-fallback-does-not-create-an-extra-unverified-safe-exit", () => {
                var s = Escalated(); Check(!s.AllowsCandidate(Vector3.Zero, new(0, 0, -3), true), "ground-only safe-exit distance unchanged"); }),
            ("hoh57-fallback-is-not-enabled-outside-hoh", () => {
                foreach (var (dungeon, floor) in new (uint, byte)[] { (4, 94), (1, 81), (3, 81), (2, 90) })
                {
                    using var f = new Fixture(dungeon, floor); var s = (HohCombatRecoveryState)Get(f.Controller, "_hohRecovery"); s.BeginAttempt(Vector3.Zero); s.RejectPosition(new(3, 0, 0));
                    Check(!(bool)Call(f.Controller, "AllowsFiringFallback", Vector3.Zero, new Vector3(0, 0, -3)), "dungeon isolation");
                } }),
            ("hoh57-four-path-query-budget-includes-verified-fallback", () => {
                using var f = new Fixture(); var c = (List<Vector3>)Get(f.Controller, "_candidates");
                var fallback = new Vector3(-3, 0, 0); var exit = new Vector3(0, 0, 9);
                c.AddRange([new(6, 0, 0), new(7, 0, 0), new(8, 0, 0), new(9, 0, 0), fallback, exit]);
                ((HashSet<Vector3>)Get(f.Controller, "_firingFallbacks")).Add(fallback); ((HashSet<Vector3>)Get(f.Controller, "_escapeCandidates")).Add(exit);
                Call(f.Controller, "OrderRecoveryCandidates"); Check(c.Take(4).Contains(fallback) && c.Take(4).Contains(exit) && c.Count == 6, "no starvation by expanded candidates"); }),
            ("hoh57-legacy-arrival-deadline-ignores-hoh-cast-state", () => {
                using var f = new Fixture(4, 94); Set(f.Controller, "_verifyAt", Now); Set(f.Controller, "_castProtected", true);
                Check(f.Expired(Now.AddSeconds(6)), "shared legacy timing unchanged"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private static HohRecoveryCastState Ready() { var s = new HohRecoveryCastState(); Feed(s); return s; }
    private static void Feed(HohRecoveryCastState s)
    { for (int ms = 0; ms <= 5000; ms += 200) s.Observe(Now.AddMilliseconds(ms), true, true, 1, ms % 2000 / 1000f); }
    private static HohCombatRecoveryState Escalated()
    {
        var s = new HohCombatRecoveryState(); s.BeginAttempt(Vector3.Zero);
        s.RejectPosition(new(3, 0, 0)); s.RejectPosition(new(6, 0, 0)); s.RejectPosition(new(0, 0, 6)); return s;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        internal readonly CombatPositionRecoveryController Controller;
        internal Fixture(uint dungeon = 2, byte floor = 61)
        {
            Context.FarmingPlan = new(FarmingMode.FreeExploration, SaveUse.Create, 1, 1, false, false, true, true, false);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty, dungeon);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty, floor);
            Controller = new(Context, null!, null!, null, Context.CombatRecoveryBudget, null, _ => { }, (_, _) => { });
        }
        internal bool Start(DateTime now) => (bool)Call(Controller, "ShouldStartRecovery", now, true, CombatSight.Blocked, false, true, false);
        internal bool Reject(DateTime now) => (bool)Call(Controller, "ShouldRejectBlockedArrival", now, CombatSight.Blocked, false);
        internal bool Expired(DateTime now) => (bool)Call(Controller, "ArrivalVerificationExpired", now);
        public void Dispose() { Controller.Dispose(); Context.Dispose(); }
    }
}
