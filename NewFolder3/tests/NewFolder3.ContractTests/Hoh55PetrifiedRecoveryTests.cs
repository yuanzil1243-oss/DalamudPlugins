using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace NewFolder3;

internal static class Hoh55PetrifiedRecoveryTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 8, 0, 0, DateTimeKind.Utc);
    private static void Check(bool condition, string why) { if (!condition) throw new Exception(why); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh55-wait-timeout-does-not-charge-another-failure", () => {
                using var f = new RecoveryFixture(); f.Fail(); Check(f.Budget.TotalFailures == 1, "one real failed attempt");
                for (int i = 0; i < 5; i++) Check(!(bool)Call(f.Controller, "HandleWaitTimeout", Enemy()), "timeout opens search");
                Check(f.Budget.TotalFailures == 1 && !f.Budget.IsTargetExhausted(7, true), "waiting does not fabricate second failure"); }),
            ("hoh60-hoh-engaged-target-gets-four-real-recovery-attempts", () => {
                using var f = new RecoveryFixture(); f.Fail(); Call(f.Controller, "HandleWaitTimeout", Enemy());
                f.Fail(); f.Fail(); f.Fail();
                Check(f.Budget.TotalFailures == 4 && f.Budget.IsTargetExhausted(7, true, 4) && f.Context.StatusIsError,
                    "only four genuine failed attempts trigger the bounded HoH pause"); }),
            ("hoh55-legacy-wait-timeout-still-charges-under-original-policy", () => {
                using var f = new RecoveryFixture(4, 94); Check((bool)Call(f.Controller, "HandleWaitTimeout", Enemy()) && f.Budget.TotalFailures == 1, "PT untouched"); }),
            ("hoh55-static-target-failure-never-waits-for-monster-to-approach", () => {
                using var f = new RecoveryFixture(); Set(f.Controller, "_staticClearTarget", true); f.Fail();
                var state = (CombatRepositionState)Get(f.Controller, "_reposition");
                Check(state.Retry(Vector3.Zero, Vector3.Zero, 0, DateTime.UtcNow) == CombatRepositionRetry.Ready && f.Budget.TotalFailures == 1, "fixed target receives real replan, not movement wait"); }),
            ("hoh55-optional-petrified-approach-failure-defers-target", () => {
                using var f = new RecoveryFixture(); Set(f.Controller, "_staticClearTarget", true); Set(f.Controller, "_targetEngaged", false); f.Controller.DeferPetrifiedApproach(Enemy());
                Check(f.Context.IsCombatTargetSuppressed(7) && !f.Context.StatusIsError && (ulong)Get(f.Controller, "_targetId") == 0, "skip optional fixed enemy instead of blocking entire clear"); }),
            ("hoh55-fixed-target-at-safe-room-landing-triggers-reposition", () => {
                using var f = new RecoveryFixture(); Set(f.Controller, "_staticClearTarget", true);
                ((EnemyChaseAttackWindow)Get(f.Controller, "_attackWindow")).Observe(false, true, Now);
                Check(!(bool)Call(f.Controller, "ShouldStartRecovery", Now.AddSeconds(3), false, CombatSight.Unknown, false, false, false) &&
                    (bool)Call(f.Controller, "ShouldStartRecovery", Now.AddSeconds(4), false, CombatSight.Unknown, false, false, false), "arrival cannot repeat forever outside attack range"); }),
            ("hoh55-petrified-target-reason-keeps-existing-enum-values", () => Check((int)EnemyChaseTargetReason.Aggro == 0 && (int)EnemyChaseTargetReason.CloserAfterRoomChange == 3 && (int)EnemyChaseTargetReason.Petrified == 4, "not fabricated hate, no renumbering")),
            ("hoh55-selector-skips-suppressed-optional-even-when-preferred", () => {
                var a = new Candidate(false, true, true, 1); var b = new Candidate(false, false, false, 20);
                Check(Choose(a, b) == b, "assist must not reselect deferred target"); }),
            ("hoh55-selector-never-drops-real-aggro-for-suppression", () => {
                var a = new Candidate(true, true, false, 100); var b = new Candidate(false, false, true, 1);
                Check(Choose(a, b) == a, "real hate outranks optional clearing"); }),
            ("hoh55-selector-keeps-floor-preferred-safe-target", () => {
                var a = new Candidate(false, false, true, 100); var b = new Candidate(false, false, false, 1);
                Check(Choose(a, b) == a && Choose(b) == b && Choose(new Candidate(false, true, true, 1)) == null, "movement and assist share stable choice"); }),
            ("hoh56-bridge-controller-and-policy-are-absent", () => {
                Check(typeof(HohCombatRecoveryState).Assembly.GetType("DeepDungeon.Fsd.Core.HohBridgeCombatPolicy") == null &&
                    typeof(RunContext).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Runtime.Floor.HohBridgeCombatController") == null, "deleted, not a disabled feature flag"); }),
            ("hoh56-no-bridge-output-or-target-gates-remain", () => {
                var members = typeof(RunContext).GetMembers(BindingFlags.Instance | BindingFlags.NonPublic);
                Check(!members.Any(m => m.Name.Contains("HohBridge", StringComparison.OrdinalIgnoreCase)), "no stale suppression, reset or target gate"); }),
            ("hoh56-floor-has-no-forced-bridge-exit", () => {
                var members = typeof(FloorExplorationController).GetMembers(BindingFlags.Instance | BindingFlags.NonPublic);
                Check(!members.Any(m => m.Name.Contains("HohBridge", StringComparison.OrdinalIgnoreCase)), "no forced exit, cast cancellation or bridge pause"); }),
            ("hoh56-elevation-alone-does-not-exclude-recovery-position", () => {
                using var f = new RecoveryFixture();
                foreach (float y in new[] { 0f, .45f, 1.31f, 2f })
                    Check((bool)Call(f.Controller, "AllowsRecoveryCandidate", Vector3.Zero, new Vector3(8, y, 0), false), "ordinary safety checks remain separate; elevation is not a bridge ban"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private sealed record Candidate(bool Engaged, bool Suppressed, bool Preferred, float Distance);
    private static Candidate? Choose(params Candidate[] c) => HohPetrifiedTargetPolicy.Select(c, x => x.Engaged, x => x.Suppressed, x => x.Distance, x => x.Preferred);
    private static void SetDuty(RunContext c, uint dungeon = 2, byte floor = 61)
    {
        typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(c.Duty, dungeon);
        typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(c.Duty, floor);
    }
    private static RunContext Context(uint dungeon = 2, byte floor = 61)
    {
        var c = new RunContext(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        c.FarmingPlan = new(FarmingMode.FreeExploration, SaveUse.Create, 1, 1, false, false, true, true, false); SetDuty(c, dungeon, floor); return c;
    }
    private static object Get(object o, string n) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    private static void Set(object o, string n, object v) => o.GetType().GetField(n, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, v);
    private static object Call(object o, string n, params object[] args) => o.GetType().GetMethod(n, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args)!;
    private sealed class RecoveryFixture : IDisposable
    {
        internal readonly RunContext Context;
        internal readonly CombatPositionRecoveryController Controller;
        internal CombatRecoveryBudget Budget => Context.CombatRecoveryBudget;
        internal RecoveryFixture(uint dungeon = 2, byte floor = 61)
        {
            Context = Hoh55PetrifiedRecoveryTests.Context(dungeon, floor);
            var nav = new NavigationHelper(new NullNavigator(), () => false, () => Now, _ => { }, _ => { }, () => false);
            Controller = new(Context, nav, null!, null, Budget, null, _ => { }, (_, _) => { });
            Set(Controller, "_targetId", 7UL); Set(Controller, "_targetEngaged", true);
        }
        internal void Fail() => Call(Controller, "Fail", "test-real-search-failure", Enemy());
        public void Dispose() { Controller.Dispose(); Context.Dispose(); }
    }
    private static IBattleChara Enemy() => DispatchProxy.Create<IBattleChara, EnemyProxy>();
    public class EnemyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name == "get_CurrentHp" ? 100U : method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    }
    private sealed class NullNavigator : INavigator
    {
        public bool PathfindAndMoveTo(Vector3 d, bool a) => true;
        public bool RepathTo(Vector3 d, bool a) => true;
        public void CancelAll() { }
    }
}
