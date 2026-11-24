using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using Dalamud.Game.ClientState.Objects.Types;

namespace NewFolder3;

internal static class Run41CombatRepositionTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 7, 29, 26, DateTimeKind.Utc);
    private static readonly Vector3 Output = new(301.72437f, 1.05746f, -248.00322f);
    private static readonly Vector3 Stuck = new(299.82202f, .541349f, -229.73482f);
    private static readonly Vector3 Target = new(296.65063f, 1, -252.50105f);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static CombatRepositionState Dodged()
    {
        var state = new CombatRepositionState(); state.ObserveOutput(Output, Now.AddSeconds(-10));
        state.YieldToMechanics(); Check(state.ResumeAfterMechanics(Now), "one handoff"); return state;
    }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("post-dodge-94-replay-remembers-the-working-position", () => {
                var state = Dodged(); Check(state.ReturnPosition(Stuck, Now) == Output,
                    "recorded dodge is over 15m but remains inside the bounded 22m return route"); }),
            ("post-dodge-handoff-is-not-renewed-every-frame", () => {
                var state = Dodged(); Check(!state.ResumeAfterMechanics(Now.AddSeconds(5)), "resume only once");
                Check(!state.InDodgeGrace(Now.AddSeconds(12)), "fixed grace deadline"); }),
            ("post-dodge-repeated-casts-cannot-extend-grace-forever", () => {
                var state = Dodged();
                foreach (int second in new[] { 10, 20, 29, 31, 40 })
                { state.YieldToMechanics(); state.ResumeAfterMechanics(Now.AddSeconds(second)); }
                Check(!state.InDodgeGrace(Now.AddSeconds(40)), "no verified output means grace remains capped");
                state.ObserveOutput(Output, Now.AddSeconds(41)); state.YieldToMechanics(); state.ResumeAfterMechanics(Now.AddSeconds(42));
                Check(state.InDodgeGrace(Now.AddSeconds(43)), "real output earns a fresh avoidance handoff"); }),
            ("post-dodge-identical-failures-wait-through-the-recorded-stop", () => {
                var state = Dodged(); state.WaitForChange(Stuck, Target, 7, Now.AddSeconds(1));
                for (int ms = 1100; ms < 12000; ms += 100)
                    Check(state.Retry(Stuck, Target, 7, Now.AddMilliseconds(ms)) == CombatRepositionRetry.Waiting,
                        "the old three-second stop must not repeat rejected routes");
                Check(state.Retry(Stuck, Target, 7, Now.AddSeconds(12)) == CombatRepositionRetry.TimedOut, "bounded wait"); }),
            ("post-dodge-engaged-target-approach-permits-a-new-plan", () => {
                var state = Dodged(); state.WaitForChange(Stuck, Target, 7, Now);
                var approaching = new Vector3(297.13977f, 1.05694f, -240.80518f);
                Check(state.Retry(Stuck, approaching, 7, Now.AddSeconds(1)) == CombatRepositionRetry.Waiting, "no rapid query burst");
                Check(state.Retry(Stuck, approaching, 7, Now.AddSeconds(2)) == CombatRepositionRetry.Changed, "real approach changes geometry"); }),
            ("post-dodge-player-reposition-and-idle-enemy-change-replan", () => {
                foreach (bool moved in new[] { false, true })
                {
                    var state = Dodged(); state.WaitForChange(Stuck, Target, 7, Now);
                    Check(state.Retry(moved ? Stuck + Vector3.UnitZ * 2 : Stuck, Target, moved ? 7UL : 8UL,
                        Now.AddSeconds(2)) == CombatRepositionRetry.Changed, "changed route conditions permit re-evaluation");
                } }),
            ("post-dodge-position-jitter-does-not-spend-new-attempts", () => {
                var state = Dodged(); state.WaitForChange(Stuck, Target, 7, Now);
                Check(state.Retry(Stuck + Vector3.UnitX * .1f, Target + Vector3.UnitZ * .2f, 7, Now.AddSeconds(3))
                    == CombatRepositionRetry.Waiting, "small jitter is not progress"); }),
            ("post-dodge-confirmed-output-releases-wait-and-updates-position", () => {
                var state = Dodged(); state.WaitForChange(Stuck, Target, 7, Now);
                var restored = new Vector3(299.5254f, 1.05746f, -241.99629f);
                state.ObserveOutput(restored, Now.AddSeconds(4));
                Check(!state.InDodgeGrace(Now.AddSeconds(4)) && state.Retry(Stuck, Target, 7, Now.AddSeconds(4)) == CombatRepositionRetry.Ready, "output restores normal combat");
                state.YieldToMechanics(); state.ResumeAfterMechanics(Now.AddSeconds(5));
                Check(state.ReturnPosition(Stuck, Now.AddSeconds(5)) == restored, "next dodge uses latest output position"); }),
            ("post-dodge-never-returns-to-stale-distant-or-reset-position", () => {
                var state = new CombatRepositionState(); state.ObserveOutput(Output, Now.AddSeconds(-61));
                state.YieldToMechanics(); state.ResumeAfterMechanics(Now);
                Check(state.ReturnPosition(Stuck, Now) == null, "stale output rejected");
                state = Dodged(); Check(state.ReturnPosition(Stuck + Vector3.UnitZ * 30, Now) == null, "distant position rejected");
                state.Reset(); state.YieldToMechanics(); state.ResumeAfterMechanics(Now);
                Check(state.ReturnPosition(Stuck, Now) == null, "target reset clears cached position"); }),
            ("post-dodge-return-route-still-rejects-new-enemies", () => {
                Check(!PassageCombatPolicy.SafeLocalPath([Output], Stuck, [(Stuck + Output) / 2]), "cached position grants no pull exemption");
                Check(PassageCombatPolicy.SafeLocalPath([Output], Stuck, []), "bounded unobstructed return can pass route policy"); }),
            ("combat-unchanged-layout-wait-is-bounded-outside-dodge", () => {
                var state = new CombatRepositionState(); state.WaitForChange(Stuck, Target, 0, Now);
                Check(state.Retry(Stuck, Target, 0, Now.AddSeconds(7)) == CombatRepositionRetry.Waiting, "no identical immediate retry");
                Check(state.Retry(Stuck, Target, 0, Now.AddSeconds(8)) == CombatRepositionRetry.TimedOut, "not an endless wait"); }),
            ("recovery-idle-controller-still-remembers-a-bmr-handoff", () => {
                using var f = new Fixture(94); f.Controller.YieldMovement();
                Check(f.State.ResumeAfterMechanics(Now), "idle combat recovery must observe the dodge"); }),
            ("recovery-post-dodge-rejected-route-does-not-charge-failure", () => {
                using var f = new Fixture(94); var now = DateTime.UtcNow;
                f.State.ObserveOutput(Output, now.AddSeconds(-10)); f.State.YieldToMechanics(); f.State.ResumeAfterMechanics(now);
                f.Fail(); Check(f.Budget.TotalFailures == 0 && f.Events.Contains("post-dodge-recovery-waiting"), "defer failures during post-dodge recovery");
                Check(f.State.Retry(Stuck, Target, 0, now.AddSeconds(1)) == CombatRepositionRetry.Waiting, "wait is wired to the controller failure path"); }),
            ("recovery-high-floor-normal-failure-keeps-budget-and-waits", () => {
                using var f = new Fixture(94); f.Fail();
                Check(f.Budget.TotalFailures == 1 && f.Budget.ConsecutiveFailures == 1, "real failures remain counted");
                Check(f.State.Retry(Stuck, Target, 0, DateTime.UtcNow) == CombatRepositionRetry.Waiting, "no immediate duplicate attempt"); }),
            ("hoh56-recovery-remembers-bmr-handoff", () => {
                using var f = new Fixture(56, 2); f.Controller.YieldMovement();
                Check(f.State.ResumeAfterMechanics(Now), "HoH normal floors retain avoidance handoff"); }),
            ("hoh56-rejected-post-dodge-route-waits-without-failure", () => {
                using var f = new Fixture(56, 2); var now = DateTime.UtcNow;
                f.Controller.YieldMovement(); f.State.ResumeAfterMechanics(now); f.Fail();
                Check(f.Budget.TotalFailures == 0 && f.Events.Contains("post-dodge-recovery-waiting"), "no two immediate failures");
                Check(f.State.Retry(Stuck, Target, 0, now.AddSeconds(13)) == CombatRepositionRetry.TimedOut, "HoH wait remains bounded"); }),
            ("hoh56-normal-failure-retains-budget-and-waits", () => {
                using var f = new Fixture(56, 2); f.Fail();
                Check(f.Budget.TotalFailures == 1 && f.State.Retry(Stuck, Target, 0, DateTime.UtcNow) == CombatRepositionRetry.Waiting, "real failures count"); }),
            ("hoh59-approaching-target-does-not-cancel-validated-ground-leg", () => {
                using var f = new Fixture(59, 2);
                Check(!f.ReplansMovingTarget(new(-286.59372f, .0051316665f, 296.71915f),
                    new(-272.32782f, 1.3097237f, 305.2262f)),
                    "recorded closing golem must not prevent the validated 6.7m relocation from starting"); }),
            ("hoh-ground-leg-retention-requires-current-path-ownership", () => {
                using var f = new Fixture(59, 2);
                Check(f.ReplansMovingTarget(Target, Target + Vector3.UnitX * 4, false),
                    "a lost path is not retained");
                Check(!f.ReplansMovingTarget(Target, Target + Vector3.UnitX * .2f),
                    "stationary target does not request a replan"); }),
            ("hoh-ground-leg-retention-does-not-change-pt-or-boss-replanning", () => {
                foreach (var (floor, dungeon) in new (byte, uint)[] { (59, 4), (94, 4), (70, 2) })
                {
                    using var f = new Fixture(floor, dungeon);
                    Check(f.ReplansMovingTarget(Target, Target + Vector3.UnitX * 4),
                        "other dungeons and boss floors retain existing target-movement replanning");
                } }),
            ("hoh-boss-floor-keeps-existing-recovery-policy", () => {
                using var f = new Fixture(70, 2); f.Controller.YieldMovement();
                Check(!f.State.ResumeAfterMechanics(Now), "boss mechanics not changed"); }),
            ("recovery-other-floors-do-not-enable-new-handoff-policy", () => {
                using var f = new Fixture(90); f.Controller.YieldMovement();
                Check(!f.State.ResumeAfterMechanics(Now), "scope is 91-98 only");
                f.Fail(); Check(f.Budget.TotalFailures == 1 && f.State.Retry(Stuck, Target, 0, Now) == CombatRepositionRetry.Ready, "existing failure policy unchanged"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        internal readonly CombatRecoveryBudget Budget = new();
        internal readonly List<string> Events = new();
        internal readonly CombatPositionRecoveryController Controller;
        internal CombatRepositionState State => (CombatRepositionState)Field("_reposition")!;
        private object? Field(string name) => typeof(CombatPositionRecoveryController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Controller);
        internal Fixture(byte floor, uint dungeonId = 4)
        {
            Context.FarmingPlan = new(FarmingMode.FreeExploration, SaveUse.Create, 91, 1, false, false, true, true, false);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty, floor);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty, dungeonId);
            Controller = new(Context, null!, null!, null, Budget, null, _ => { }, (name, _) => Events.Add(name));
            Set("_targetId", 42UL); Set("_targetEngaged", true); Set("_lastPlayerPosition", Stuck);
        }
        private void Set(string name, object value) => typeof(CombatPositionRecoveryController).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Controller, value);
        internal bool ReplansMovingTarget(Vector3 plannedTarget, Vector3 currentTarget, bool ownsPath = true)
        {
            Set("_phase", Enum.Parse(typeof(CombatPositionRecoveryController).GetNestedType("Phase", BindingFlags.NonPublic)!, "Moving"));
            Set("_ownsPath", ownsPath); Set("_targetAtPlan", plannedTarget);
            try
            {
                return (bool)typeof(CombatPositionRecoveryController).GetMethod("ShouldReplanForTargetMovement",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Controller, [currentTarget])!;
            }
            finally { Set("_ownsPath", false); }
        }
        internal void Fail()
        {
            var target = DispatchProxy.Create<IBattleChara, TargetProxy>();
            typeof(CombatPositionRecoveryController).GetMethod("Fail", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Controller, ["no-safe-local-path-without-new-enemies", target]);
        }
        public void Dispose() { Controller.Dispose(); Context.Dispose(); }
    }
    public class TargetProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.Name switch
        {
            "get_Position" => Target,
            "get_CurrentHp" => 260018U,
            _ => method!.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null
        };
    }
}
