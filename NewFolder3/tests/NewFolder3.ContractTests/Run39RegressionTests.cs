using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class Run39RegressionTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static readonly ExplorationRecoverySnapshot Ready = new(4, 94, true, true, false, true, false, false);
    public static int Run()
    {
        var tests = new (string, Action)[]
        {
            ("recovery-94-replay-success-breaks-the-failure-streak", () => {
                var b = new CombatRecoveryBudget();
                b.RecordFailure(1036); b.RecordFailure(1037); b.ObserveVerifiedOutput(1037, false);
                foreach (ulong id in new ulong[] { 1036, 1033, 1046, 1043 }) b.RecordFailure(id);
                b.ObserveVerifiedOutput(1046, false); b.RecordFailure(1044); b.RecordFailure(1045);
                Check(b.TotalFailures == 8 && b.ConsecutiveFailures == 2 && !b.Exhausted, "94 floor must not stop after two later failures"); }),
            ("recovery-eight-unbroken-failures-remain-bounded", () => {
                var b = new CombatRecoveryBudget(); for (ulong id = 1; id <= 8; id++) b.RecordFailure(id);
                Check(b.Exhausted && b.ConsecutiveFailures == 8 && b.TotalFailures == 8, "no infinite retry"); }),
            ("recovery-98-unengaged-history-cannot-spend-engaged-budget", () => {
                var b = new CombatRecoveryBudget(); b.RecordFailure(1); b.RecordFailure(1);
                Check(b.IsTargetExhausted(1) && !b.IsTargetExhausted(1, true), "fresh engaged obligation");
                b.RecordFailure(1, true); Check(!b.IsTargetExhausted(1, true), "one engaged failure permits one more attempt");
                b.RecordFailure(1, true); Check(b.IsTargetExhausted(1, true), "engaged attempt remains bounded"); }),
            ("hoh60-four-attempt-override-does-not-change-default-two-attempt-budget", () => {
                var b = new CombatRecoveryBudget(); b.RecordFailure(7, true); b.RecordFailure(7, true);
                Check(b.IsTargetExhausted(7, true) && !b.IsTargetExhausted(7, true, 4), "PT/default threshold remains two while HoH may opt into four");
                b.RecordFailure(7, true); Check(!b.IsTargetExhausted(7, true, 4), "third HoH attempt remains available");
                b.RecordFailure(7, true); Check(b.IsTargetExhausted(7, true, 4), "four attempts are still bounded"); }),
            ("recovery-target-switches-cannot-reset-phase-budgets", () => {
                var b = new CombatRecoveryBudget(); b.RecordFailure(1, true); b.RecordFailure(2, true); b.RecordFailure(1, true);
                Check(b.IsTargetExhausted(1, true) && !b.IsTargetExhausted(2, true) && b.ConsecutiveFailures == 3, "retain target identity"); }),
            ("recovery-verified-output-only-releases-the-successful-target", () => {
                var b = new CombatRecoveryBudget(); b.RecordFailure(1); b.RecordFailure(1); b.RecordFailure(2); b.RecordFailure(2);
                b.ObserveVerifiedOutput(1, false);
                Check(!b.IsTargetExhausted(1) && b.IsTargetExhausted(2) && b.TotalFailures == 4 && b.ConsecutiveFailures == 0, "keep other failed targets"); }),
            ("recovery-manual-continue-retains-history-but-renews-attempts", () => {
                var b = new CombatRecoveryBudget(); b.RecordFailure(1); b.RecordFailure(1); b.RecordFailure(1, true); b.RecordFailure(1, true);
                b.ResumeManually(); Check(b.TotalFailures == 4 && b.ConsecutiveFailures == 0 && !b.IsTargetExhausted(1) && !b.IsTargetExhausted(1, true), "explicit bounded retry"); }),
            ("recovery-new-floor-does-not-inherit-budgets", () => {
                var a = new CombatRecoveryBudget(); a.RecordFailure(1);
                var b = new CombatRecoveryBudget(); Check(b.TotalFailures == 0 && b.ConsecutiveFailures == 0, "new floor clean"); }),
            ("continue-not-available-without-a-recorded-error-stop", () => Check(!new ExplorationRecoveryPause().CanResume(Ready), "normal/manual stop cannot resume")),
            ("continue-allows-only-the-original-ready-floor", () => { var p = Paused(); Check(p.CanResume(Ready), "original live floor"); }),
            ("continue-rejects-outside-duty", () => Check(!Paused().CanResume(Ready with { InDuty = false }), "outside")),
            ("continue-rejects-state-read-failure", () => Check(!Paused().CanResume(Ready with { StateReady = false }), "unknown state")),
            ("continue-rejects-loading", () => Check(!Paused().CanResume(Ready with { Transitioning = true }), "loading")),
            ("continue-rejects-death-or-missing-player", () => Check(!Paused().CanResume(Ready with { PlayerAlive = false }), "dead")),
            ("continue-rejects-wipe-or-timeout", () => Check(!Paused().CanResume(Ready with { DutyFailed = true }), "failed")),
            ("continue-rejects-completed-duty", () => Check(!Paused().CanResume(Ready with { DutyCompleted = true }), "complete")),
            ("continue-rejects-zero-timer-before-ejection-event", () => Check(!Paused().CanResume(Ready with { TimerExpired = true }), "expired timer")),
            ("continue-rejects-floor-or-dungeon-change", () => {
                Check(!Paused().CanResume(Ready with { Floor = 95 }) && !Paused().CanResume(Ready with { DungeonId = 3 }), "scope mismatch"); }),
            ("continue-token-is-consumed-and-cleared", () => { var p = Paused(); p.Clear(); Check(!p.CanResume(Ready) && !p.IsPaused && p.Reason.Length == 0, "double-click cannot resume twice"); }),
            ("recoverable-error-retains-the-scenario-session-and-settings", () => {
                using var ctx = Context();
                var plan = new FarmingPlan(FarmingMode.FreeExploration, SaveUse.Create, 91, 2, false, true, true, true, false) { StopAfterFloor = 100 };
                var session = new FarmingSession(plan, 0, true);
                using var scenario = new FarmingScenario(session); scenario.Initialize(ctx);
                Set(ctx.Duty, nameof(DutyState.IsInDuty), true); Set(ctx.Duty, nameof(DutyState.DungeonId), (uint)4); Set(ctx.Duty, nameof(DutyState.Floor), (byte)94);
                ctx.StopExplorationRecoverably("position recovery exhausted"); scenario.Update(null!);
                Check(ctx.StatusIsError && ctx.ExplorationRecovery.IsPaused && !scenario.IsComplete && scenario.EntryAcceptedForRecovery,
                    "host must retain the live attempt");
                Check(session.OwnedSlot == 0 && session.NextFloor == 91 && ctx.RunOptions.Current.BandedEnabled && session.Plan.StopAfterFloor == 100,
                    "do not recreate the task or lose ownership"); }),
            ("nonrecoverable-error-still-completes-the-scenario", () => {
                using var ctx = Context(); var session = new FarmingSession(new(FarmingMode.FreeExploration, SaveUse.Create, 91, 1, false, false, true, true, false), 0, true);
                using var scenario = new FarmingScenario(session); scenario.Initialize(ctx); ctx.StatusIsError = true; scenario.Update(null!);
                Check(scenario.IsComplete && !ctx.ExplorationRecovery.IsPaused, "unknown fatal errors cannot enable continue"); }),
            ("outside-error-cannot-create-a-continuation-token", () => {
                using var ctx = Context(); ctx.StopExplorationRecoverably("not in duty");
                Check(ctx.StatusIsError && !ctx.ExplorationRecovery.IsPaused, "outside errors remain nonrecoverable"); }),
        };
        foreach (var (name, action) in tests) { action(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private static ExplorationRecoveryPause Paused() { var p = new ExplorationRecoveryPause(); p.Pause(4, 94, "recovery exhausted"); return p; }
    private static RunContext Context() => new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
}
