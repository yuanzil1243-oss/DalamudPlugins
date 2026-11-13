using DeepDungeon.Fsd.Core;

internal static class Run30RegressionTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 1, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    public static IEnumerable<(string, Action)> Cases()
    {
        yield return ("floor43-no-interaction-or-reselect-cannot-reset-box-budget", () => {
            var b = new OptionalChestBudget(); b.Select(43, 8, Now);
            for (int i=1; i<=30; i++) { b.Select(43, 8, Now.AddSeconds(i)); b.Tick(43, Now.AddSeconds(i), false, 8); }
            Check(b.Reason(43) == "no-real-progress", "reissuing selection is not progress");
        });
        yield return ("box-real-approach-progress-resets-idle-not-total-budget", () => {
            var b = new OptionalChestBudget(); b.Select(1, 100, Now);
            for (int i=1; i<=60; i++) b.Tick(1, Now.AddSeconds(i), false, 100-i);
            Check(b.Reason(1) == "active-time-limit", "continuous progress must still be bounded");
        });
        yield return ("box-jitter-does-not-reset-progress-clock", () => {
            var b = new OptionalChestBudget(); b.Select(1, 10, Now);
            for (int i=1; i<=30; i++) b.Tick(1, Now.AddSeconds(i), false, 10-(i%2)*.1f);
            Check(b.Reason(1) != null, "jitter cannot keep a box forever");
        });
        yield return ("box-combat-avoidance-and-cutscene-pause-timeouts", () => {
            var b = new OptionalChestBudget(); b.Select(1, 10, Now);
            b.Tick(1, Now.AddSeconds(20), false, 10);
            b.Tick(1, Now.AddSeconds(21), true);
            b.Tick(1, Now.AddMinutes(10), true);
            b.Tick(1, Now.AddMinutes(11), false, 10);
            Check(b.Reason(1) == null, "suspension is not 10 minutes of box failure");
            b.Tick(1, Now.AddMinutes(11).AddSeconds(10), false, 10);
            Check(b.Reason(1) == "no-real-progress", "unpaused timer resumes");
        });
        yield return ("box-confirmed-inventory-change-is-bounded-progress", () => {
            var b = new OptionalChestBudget(); b.Select(1, 1, Now);
            b.Tick(1, Now.AddSeconds(25), false, 1, true);
            b.Tick(1, Now.AddSeconds(50), false, 1);
            Check(b.Reason(1) == null, "inventory relief permits retry");
            b.Tick(1, Now.AddSeconds(60), false, 1, true);
            Check(b.Reason(1) == "active-time-limit", "count changes cannot extend total forever");
        });
        yield return ("box-three-completed-recovery-failures-abandon-only-that-entity", () => {
            var b = new OptionalChestBudget(); b.Select(1, 5, Now); b.Select(2, 5, Now);
            b.FailedRecovery(1); b.FailedRecovery(1); Check(b.Reason(1) == null, "two rounds");
            b.FailedRecovery(1); b.Select(1, 0, Now.AddSeconds(1));
            Check(b.Reason(1) == "recovery-limit" && b.Reason(2) == null, "persistent per-entity blacklist");
            Check(!new OptionalChestBudget().Contains(1), "new floor generation starts clean");
        });
        yield return ("box-objective-flapping-without-reselection-still-expires", () => {
            var b = new OptionalChestBudget(); b.Select(1, 5, Now);
            b.Tick(1, Now.AddSeconds(30), false);
            Check(b.Reason(1) == "no-real-progress", "switching to passage must not hide the pending box");
        });
        yield return ("solo-hazards-61-77-and-high-floor-remain-eligible-when-alone", () => {
            foreach (var (floor, name) in new (int,uint)[] { (68,14179), (77,14194), (77,14197), (95,14219), (95,14221), (95,14224) }) {
                Check(MobPullPolicy.RequiresSolo(FarmingMode.DeepProgression,4,floor,name), "recognized hazard");
                Check(MobPullPolicy.CanInitiateControlled(FarmingMode.DeepProgression,4,floor,name,0,false,false), "must not patrol forever when only hazards remain");
                Check(!MobPullPolicy.CanInitiateControlled(FarmingMode.DeepProgression,4,floor,name,1,false,false), "no hazardous add");
                Check(MobPullPolicy.CanInitiateControlled(FarmingMode.DeepProgression,4,floor,name,2,true,true), "existing hate remains obligation");
            }
        });
        yield return ("solo-engagement-blocks-new-ordinary-enemy-but-keeps-existing-aggro", () => {
            Check(!MobPullPolicy.CanInitiateControlled(FarmingMode.DeepProgression,4,68,123,1,true,false), "no ordinary add");
            Check(MobPullPolicy.CanInitiateControlled(FarmingMode.DeepProgression,4,68,123,2,true,true), "already engaged");
            Check(MobPullPolicy.CanInitiateControlled(FarmingMode.DeepProgression,4,68,123,1,false,false), "ordinary rules unchanged");
        });
        yield return ("solo-hazard-scope-keeps-other-floors-and-dungeons-unchanged", () => {
            Check(MobPullPolicy.RequiresSolo(FarmingMode.FreeExploration,4,68,14179), "free exploration shares progression hazard rules");
            Check(!MobPullPolicy.RequiresSolo(FarmingMode.DeepProgression,4,80,14194), "floor boundary");
            Check(!MobPullPolicy.RequiresSolo(FarmingMode.DeepProgression,1,77,14194), "dungeon boundary");
            Check(!MobPullPolicy.RequiresSolo(null,4,77,14194), "mode boundary");
        });
        yield return ("mechanics-preempt-every-combination-of-combat-movement", () => {
            foreach(bool priority in new[] {false,true}) foreach(bool solo in new[] {false,true}) foreach(bool group in new[] {false,true})
                Check(MobMovementPolicy.Choose(true,priority,solo,group) == MobMovementOwner.Mechanics, "avoidance must win");
        });
        yield return ("priority-recovers-position-and-resumes-without-unlatching-group", () => {
            var group = new CombatConsolidationState(); group.Update(true,3,3);
            Check(MobMovementPolicy.Choose(false,true,false,group.Active) == MobMovementOwner.Fight, "mimic recovery before group route");
            Check(MobMovementPolicy.Choose(true,true,false,group.Active) == MobMovementOwner.Mechanics, "mechanic yields");
            Check(MobMovementPolicy.Choose(false,true,false,group.Active) == MobMovementOwner.Fight, "resume same fight");
            Check(MobMovementPolicy.Choose(false,false,true,group.Active) == MobMovementOwner.Consolidation, "accidental group keeps existing consolidation policy");
            group.Update(true,0,3);
            Check(MobMovementPolicy.Choose(false,false,false,group.Active) == MobMovementOwner.Routine, "resume exploration once cleared");
        });
    }
}
