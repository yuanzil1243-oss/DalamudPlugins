using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

internal static class Hoh51RegressionTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 6, 7, 54, DateTimeKind.Utc);
    private static readonly Vector3 Center = new(-300, 0, -300);
    private static void Check(bool ok, string why) { if (!ok) throw new Exception(why); }
    private static bool Allow(int f, uint s, int n, bool boss = false, bool cap = false, bool emergency = false) => HohItemPolicy.AllowPomander(f, s, n, boss, emergency, cap);
    private static Hoh90Frame Cannon(Vector3 player, float remaining = 4, Vector3? bmr = null, bool? navigating = null) =>
        new(true, 2, 90, true, true, 42, 9199, true, player, Center,
            [new(42, 9199, 12215, 1, Center, Center, 0, remaining)], [], Center, bmr, navigating);
    private static Hoh90Frame Meteor(Vector3 player, Vector3 origin, float remaining = 5.7f) =>
        new(true, 2, 90, true, true, 42, 9199, true, player, Center,
            [new(42, 9199, 12211, 1, origin, origin, 0, 3), new(71, 8865, 12212, 1, origin, origin, 0, remaining)], [], Center);

    public static IEnumerable<(string, Action)> Cases()
    {
        yield return ("hoh51-low-floor-frailty-only-confirmed-overcap", () => {
            foreach (int f in new[] { 1, 58, 74, 79 })
            { Check(!Allow(f, 11, 3, emergency: true), "emergency cannot bypass reserve"); Check(Allow(f, 11, 3, cap: true), "confirmed capped loot is useful at low floors"); }
        });
        yield return ("hoh64-81-to89-preserve-two-frailties-but91-to99-release-boss-reserve", () => {
            for (int f = 81; f <= 89; f++)
            { Check(Allow(f, 11, 3), "surplus allowed"); Check(!Allow(f, 11, 2) && !Allow(f, 11, 1, emergency: true), "two reserved before90"); }
            for (int f = 91; f <= 99; f++) Check(Allow(f,11,1) && Allow(f,11,2), "no remaining boss after90");
        });
        yield return ("hoh51-boss-frailty-can-use-last-stock-on-every-real-boss", () => {
            for (int f = 10; f <= 90; f += 10) Check(Allow(f, 11, 1, boss: true), "normal reserve must not block boss");
            Check(DeepDungeonFloorClassifier.Classify(2, 99) == DeepDungeonFloorKind.Mob, "99 must remain a normal HoH floor");
        });
        yield return ("hoh51-boss-frailty-hp-exact-thirty-percent-boundary", () => {
            Check(!HohItemPolicy.AllowBossFrailty(299, 1000, 0), "below30");
            Check(HohItemPolicy.AllowBossFrailty(300, 1000, 1), "exact30");
            Check(!HohItemPolicy.AllowBossFrailty(0, 1000, 0) && !HohItemPolicy.AllowBossFrailty(300, 0, 0), "invalid or dead");
        });
        yield return ("hoh51-boss-frailty-counts-confirmed-consumption-only", () => {
            var b = new HohFloorItemBudget(); var channel = new FloorItemUseSession();
            var request = channel.Begin(new(FloorItemUseKind.Pomander, 11), FloorItemUsePurpose.HohFrailty, 3, Now, "boss", false, 0, 0, 0, 0);
            Check(channel.Observe(3, false, Now.AddSeconds(.2)) != FloorItemUseConfirmationDecisionKind.Confirmed && b.BossFrailtyConfirmed == 0, "dispatch not success");
            Check(channel.Observe(2, false, Now.AddSeconds(.3)) == FloorItemUseConfirmationDecisionKind.Confirmed, "count proves consumption");
            b.RecordPomanderConfirmed(11, Now, boss: true); channel.Confirm(request);
            b.RecordPomanderConfirmed(11, Now.AddMinutes(3), boss: true);
            Check(b.BossFrailtyConfirmed == 2 && !HohItemPolicy.AllowBossFrailty(900, 1000, b.BossFrailtyConfirmed), "two maximum");
            Check(new HohFloorItemBudget().BossFrailtyConfirmed == 0, "new boss floor budget resets");
        });
        yield return ("hoh51-second-frailty-still-waits-for-existing-duration", () => {
            var b = new HohFloorItemBudget(); b.RecordPomanderConfirmed(11, Now, true);
            Check(!b.AllowsPomander(11, Now.AddSeconds(179)) && b.AllowsPomander(11, Now.AddMinutes(3)), "no back-to-back double use");
        });
        yield return ("hoh51-steel-two-strength-one-reserve", () => {
            Check(Allow(74, 3, 3) && !Allow(74, 3, 2) && !Allow(84, 3, 1, emergency: true), "two steel before90");
            Check(Allow(74, 2, 3) && Allow(74, 2, 2) && !Allow(84, 2, 1), "one strength before90");
            foreach (uint s in new uint[] { 2, 3 }) Check(Allow(90, s, 1, true), "boss may spend reserve");
        });
        yield return ("hoh51-supply-shortage-affluence-default-is-one-or-less", () => {
            Check(HohItemPolicy.SupplyBuffsLow(1, 3) && HohItemPolicy.SupplyBuffsLow(3, 0), "either scarce");
            Check(!HohItemPolicy.SupplyBuffsLow(2, 2), "adequate stocks");
        });
        yield return ("hoh51-all-next-floor-effects-reject-next-boss-and-shrine", () => {
            foreach (uint s in new uint[] { 4, 5, 6 })
            {
                Check(ProgressionItemPolicy.AffectsNextFloor(s), "all three");
                foreach (int f in new[] { 9, 19, 79, 89, 99 }) Check(!ProgressionItemPolicy.CanBenefitNextFloor(2, f, 100), "never before boss/result");
            }
            Check(ProgressionItemPolicy.CanBenefitNextFloor(2, 88, 100) && !ProgressionItemPolicy.CanBenefitNextFloor(2, 88, 88), "normal next floor and stop boundary");
        });
        yield return ("hoh51-capped-loot-transaction-cannot-consume-twice", () => {
            var c = new HohChestReliefState();
            Check(!c.CanRelieve(false, 3, false) && !c.CanRelieve(true, 2, false) && !c.CanRelieve(true, 3, true), "real event and full inventory and free channel");
            Check(c.CanRelieve(true, 3, false), "one relief"); c.ConfirmConsumption(2);
            Check(c.AwaitingLoot && !c.CanRelieve(true, 3, false), "duplicate event or replenished stock does not spend again");
            Check(!c.ObserveLoot(2, false) && c.ObserveLoot(3, false) && !c.AwaitingLoot, "confirm reacquisition");
            Check(!c.CanRelieve(true, 3, false), "spent flag survives completed loot");
        });
        yield return ("hoh51-original-chest-native-completion-is-authoritative", () => {
            var c = new HohChestReliefState(); c.ConfirmConsumption(2);
            Check(c.ObserveLoot(2, true), "native chest completion can arrive before count refresh");
        });
        yield return ("hoh51-unchanged-magicite-stock-does-not-close-floor-budget", () => {
            var b = new HohFloorItemBudget(); var channel = new FloorItemUseSession();
            channel.Begin(new(FloorItemUseKind.Stone, 1), FloorItemUsePurpose.HohMagicite, 2, Now, "magicite", false, 0, 0, 0, 0);
            Check(!b.MagiciteConfirmed && channel.Observe(2, false, Now.AddSeconds(.2)) != FloorItemUseConfirmationDecisionKind.Confirmed, "request only");
            b.RecordMagiciteConfirmed(); Check(b.MagiciteConfirmed, "consumption locks subsequent petrification");
        });
        yield return ("hoh51-capped-frailty-and-petrification-allowed-in-combat", () => {
            foreach (uint s in new uint[] { 11, 15 }) Check(ProgressionItemPolicy.AllowsCombat(s, false, FloorItemUsePurpose.GoldChestOvercap, 2), "do not wait for combat to end");
            Check(ProgressionItemPolicy.AllowsCombat(15, false, FloorItemUsePurpose.HohPetrification, 2), "active clear in combat");
        });
        yield return ("hoh51-petrification-overcap-requires-door-closed-and-real-full-stock", () => {
            Check(HohItemPolicy.AllowPetrification(58, false, false, false, false, true, 3), "58 capped new loot");
            Check(!HohItemPolicy.AllowPetrification(58, false, true, false, false, true, 3), "door open");
            Check(!HohItemPolicy.AllowPetrification(58, false, false, false, false, true, 2), "not full");
            Check(!HohItemPolicy.AllowPetrification(58, false, false, false, false, false, 3), "full alone is not new loot");
        });
        yield return ("hoh51-petrification-active-94plus-but-not-boss", () => {
            Check(!HohItemPolicy.AllowPetrification(91, false, false, false, false, false, 1), "reserved until94");
            Check(HohItemPolicy.AllowPetrification(94, false, false, false, false, false, 1), "does not require full stock or new loot from94");
            Check(!HohItemPolicy.AllowPetrification(89, false, false, false, false, false, 3), "no unsolicited low-floor use");
            Check(!HohItemPolicy.AllowPetrification(90, true, false, false, false, true, 3), "no boss");
        });
        yield return ("hoh51-magicite-then-petrification-is-forbidden", () => {
            foreach (bool cap in new[] { false, true }) Check(!HohItemPolicy.AllowPetrification(94, false, false, true, false, cap, 3), "both entry points block after confirmed magicite");
        });
        yield return ("hoh51-petrification-then-magicite-stays-allowed", () => {
            var p = new HohPetrificationState(); p.BeginFloor(2, 99); p.ConfirmUse(Now);
            Check(p.Clearing && HohItemPolicy.ShouldUseMagicite(99, 1, false, false, true, false), "one-way exclusion only; current floor budget must still allow the use");
            Check(HohItemPolicy.ShouldUseMagicite(94, 1, false, true, true, false), "emergency use also remains allowed after stone curse");
        });
        yield return ("hoh51-petrification-clears-only-real-status-targets", () => {
            var p = new HohPetrificationState(); p.BeginFloor(2, 58); p.ConfirmUse(Now); p.Observe(true, true, false, Now.AddSeconds(1));
            Check(p.Clearing && p.AllowsTarget(true) && !p.AllowsTarget(false), "observed Stone Curse targets only");
            Check(!HohItemPolicy.AllowPetrification(94, false, false, false, true, false, 3), "do not overlap active stone clear");
        });
        yield return ("hoh51-petrification-ends-with-door-open-blocks-new-pulls-and-resets-next-floor", () => {
            var p = new HohPetrificationState(); p.BeginFloor(2, 58); p.ConfirmUse(Now); p.Observe(true, true, true, Now.AddSeconds(1));
            p.Observe(false, false, true, Now.AddSeconds(31));
            Check(!p.Clearing && p.StopAttackingAfterClear && !p.AllowsTarget(true), "stop new pulls when no engagement evidence was supplied");
            p.BeginFloor(2, 59); Check(!p.Clearing && !p.StopAttackingAfterClear && p.AllowsTarget(false), "normal next floor");
        });
        yield return ("hoh51-petrification-ends-with-door-closed-restores-normal-clearing", () => {
            var p = new HohPetrificationState(); p.BeginFloor(2, 94); p.ConfirmUse(Now); p.Observe(true, true, false, Now.AddSeconds(1));
            p.Observe(false, false, false, Now.AddSeconds(31)); Check(!p.StopAttackingAfterClear && p.AllowsTarget(false), "normal passage activation remains");
        });
        yield return ("hoh51-petrification-native-state-delay-has-bounded-grace", () => {
            var p = new HohPetrificationState(); p.BeginFloor(2, 94); p.ConfirmUse(Now);
            p.Observe(false, false, false, Now.AddSeconds(1)); Check(p.Clearing, "server status can settle");
            p.Observe(false, false, false, Now.AddSeconds(6)); Check(!p.Clearing, "no endless wait without native evidence");
        });
        yield return ("hoh51-bridge-vertical-only-019m-is-not-recovery-motion", () => {
            Check(!CombatPositionPolicy.HasRealHorizontalMove(Vector3.Zero, new(0, .19f, 0)), "74/81 false recovery");
            Check(CombatPositionPolicy.HasRealHorizontalMove(Vector3.Zero, new(1, .19f, 0)), "real bridge exit");
        });
        yield return ("hoh51-bridge-ground-path-may-rise-continuously", () => {
            Vector3[] path = [new(2, .2f, 0), new(4, .4f, 0), new(6, 0, 0)];
            Check(CombatPositionPolicy.HasContinuousGround(path, Vector3.Zero, p => p, (_, _) => true, out var rise) && rise < .4f, "gradual bridge profile accepted");
        });
        yield return ("hoh51-bridge-rejects-under-bridge-layer-and-midpath-hole", () => {
            Vector3[] path = [new(4, 0, 0)];
            Check(!CombatPositionPolicy.HasContinuousGround(path, Vector3.Zero, p => p - new Vector3(0, 7, 0), (_, _) => true, out _), "wrong storey");
            Check(!CombatPositionPolicy.HasContinuousGround(path, Vector3.Zero, p => p.X is > 1 and < 3 ? null : p, (_, _) => true, out _), "gap along path");
        });
        yield return ("hoh51-bridge-rejects-collision-and-new-enemy-crossing", () => {
            Vector3[] path = [new(6, 0, 0)];
            Check(!CombatPositionPolicy.HasContinuousGround(path, Vector3.Zero, p => p, (_, _) => false, out _), "collision");
            Check(!PassageCombatPolicy.SafeLocalPath(path, Vector3.Zero, [new(4, 0, 0)]), "cannot add aggro while leaving bridge");
        });
        yield return ("hoh66-cannon-without-meteor-remains-exclusively-with-bmr", () => {
            Vector3? desired = null;
            using var c = new Hoh90MeteorController(_ => true, p => desired = p, () => desired = null, p => p, (_, _) => true, _ => { });
            var f = Cannon(Center + new Vector3(0, 0, 6)); c.Update(f, Now);
            Check(c.MeteorOrigin == null && !c.OwnsMovement && !c.SuppressOutput && desired == null, "ordinary local fallback removed after live regression");
        });
        yield return ("hoh51-native-effective-bmr-route-keeps-control", () => {
            using var c = new Hoh90MeteorController(_ => true, _ => { }, () => { }, p => p, (_, _) => true, _ => { });
            var f = Cannon(Center + new Vector3(0, 0, 6), 4, Center + new Vector3(12, 0, 6), true); c.Update(f, Now);
            c.Update(f with { Player = f.Player + new Vector3(.5f, 0, 0) }, Now.AddSeconds(.1));
            Check(!c.OwnsMovement && c.HandoffEvidence.Contains("ordinary-phase"), "ordinary mechanics retain BMR control");
        });
        yield return ("hoh66-ordinary-bmr-goal-without-motion-does-not-authorize-takeover", () => {
            using var c = new Hoh90MeteorController(_ => true, _ => { }, () => { }, p => p, (_, _) => true, _ => { });
            var f = Cannon(Center + new Vector3(0, 0, 6), 4, Center + new Vector3(12, 0, 6), true); c.Update(f, Now);
            Check(!c.OwnsMovement, "short initial observation");
            c.Update(f with { Casts = [f.Casts[0] with { Remaining = 3.6f }] }, Now.AddSeconds(.4));
            Check(!c.OwnsMovement, "ordinary phase cannot be taken over on a short no-motion timer");
        });
        yield return ("hoh66-ordinary-local-rejection-of-bmr-goal-does-not-take-control", () => {
            using var c = new Hoh90MeteorController(_ => true, _ => { }, () => { }, p => p, (_, _) => true, _ => { });
            c.Update(Cannon(Center + new Vector3(0, 0, 6), 4, Center + new Vector3(0, 0, 9), true), Now);
            Check(!c.OwnsMovement, "local danger-model rejection is not independent proof of BMR failure");
        });
        yield return ("hoh51-preposition-and-safe-hold-do-not-disable-all-output", () => {
            using var c = new Hoh90MeteorController(_ => true, _ => { }, () => { }, p => p, (_, _) => true, _ => { });
            var f = Meteor(Center, Center) with { Casts = [new(42, 9199, 12211, 1, Center, Center, 0, 3)] };
            c.Update(f, Now); Check(c.OwnsMovement && !c.SuppressOutput, "provisional positioning is not an 8-second blanket output stop");
            using var safe = new Hoh90MeteorController(_ => true, _ => { }, () => { }, p => p, (_, _) => true, _ => { });
            safe.Update(Meteor(Center, Center), Now);
            safe.Update(Meteor(safe.EscapeEdge!.Value, Center), Now.AddSeconds(1));
            Check(safe.OwnsMovement && !safe.SuppressOutput && !safe.UrgentMovement, "safe final hold restores output");
        });
        yield return ("hoh51-refused-lease-is-cleaned-up-on-reset", () => {
            int released = 0, moved = 0;
            using var c = new Hoh90MeteorController(active => { if (!active) released++; return !active; }, _ => moved++, () => { }, p => p, (_, _) => true, _ => { });
            c.Update(Meteor(Center, Center), Now); c.Reset();
            Check(!c.OwnsMovement && moved == 0 && released == 1, "pending disable does not leak override");
        });
        yield return ("hoh66-committed-far-edge-does-not-introduce-short-waypoint-holds", MiddleWaypoint);
        yield return ("hoh51-actual-no-motion-triggers-stall-instead-of-fake-holding-progress", () => {
            var notes = new List<string>();
            using var c = new Hoh90MeteorController(_ => true, _ => { }, () => { }, p => p, (_, _) => true, notes.Add);
            var f = Meteor(Center, Center); c.Update(f, Now); c.Update(f, Now.AddSeconds(2.1));
            Check(!c.OwnsMovement && notes.Any(n => n.Contains("movement-stalled")), "actual no progress returns control instead of hanging");
        });
        yield return ("hoh66-late-helper-and-cannon-without-visual-remain-with-bmr", CannonDuringMeteor);
        yield return ("hoh51-original-chest-recovery-outranks-passage-and-combat", () => {
            var s = new ObjectiveArbiterSnapshot(false, false, false, false, true, true, false, true, true, true, PassageFirst: true, RecoverActiveChest: true);
            var d = ObjectiveArbiter.Decide(s);
            Check(d.PrimaryObjective == FloorObjectiveKind.OpenPlannedChest && d.Channels.Interaction != CommandChannelPermission.Blocked &&
                d.Channels.Transition == CommandChannelPermission.Blocked, "reacquire original loot before any transition");
            Check(ObjectiveArbiter.Decide(s with { RecoverActiveChest = false }).PrimaryObjective == FloorObjectiveKind.FinishCombatBeforePassage, "ordinary behavior preserved");
        });
        yield return ("hoh51-completed-petrification-idle-explores-pending-route", () => {
            var s = new ObjectiveArbiterSnapshot(false, false, false, false, true, true, false, false, false, true, RequiredChestWork: true, SuppressCombat: true);
            var d = ObjectiveArbiter.Decide(s);
            Check(d.PrimaryObjective == FloorObjectiveKind.OpenPlannedChest && d.Channels.Combat == CommandChannelPermission.Blocked &&
                d.Channels.Transition == CommandChannelPermission.Blocked, "explore instead of reentering attack loop or leaving immediately");
        });
        yield return ("hoh51-capped-petrification-can-free-a-slot-even-if-prior-effect-is-active", () => {
            Check(HohItemPolicy.AllowPetrification(58, false, false, false, true, true, 3), "real new capped loot is distinct from automatic repeated use");
            Check(!HohItemPolicy.AllowPetrification(94, false, false, false, true, false, 3), "normal refresh does not overwrite active clear");
        });
        yield return ("hoh51-duplicate-confirmation-does-not-reset-loot-baseline", () => {
            var c = new HohChestReliefState(); c.ConfirmConsumption(2); c.ConfirmConsumption(3);
            Check(c.StockAfterUse == 2 && c.ObserveLoot(3, false), "one transaction keeps original post-consumption count");
        });
        yield return ("hoh51-failed-bmr-command-retries-without-frame-spam", () => {
            var clock = Now; int attempts = 0;
            var c = new FsdBossMechanicsController(command => ++attempts >= 2, () => clock);
            var s = new DeepDungeonStateSnapshot(true, true, true, 2, 90, DeepDungeonFloorKind.Boss, false, 1);
            c.Update(true, s, new(), FarmingMode.FreeExploration);
            Check(!c.IsEnabled, "failure is not enabled proof");
            for (int i = 0; i < 20; i++) c.Update(true, s, new(), FarmingMode.FreeExploration);
            Check(attempts == 1, "no frame spam"); clock = clock.AddSeconds(1);
            c.Update(true, s, new(), FarmingMode.FreeExploration);
            Check(c.IsEnabled && attempts == 2, "failed on dispatch must not remain stuck forever");
        });
        yield return ("hoh66-meteor-effect-returns-overlapping-cannon-to-bmr", () => {
            int releases = 0;
            using var c = new Hoh90MeteorController(active => { if (!active) releases++; return true; }, _ => { }, () => { }, p => p, (_, _) => true, _ => { });
            var player = Center + new Vector3(23, 0, 0);
            var f = Meteor(player, Center) with { Casts = [new(42, 9199, 12211, 1, Center, Center, 0, 3), new(71, 8865, 12212, 1, Center, Center, 0, 1.7f), new(42, 9199, 12215, 1, Center, Center, 0, 2)] };
            c.Update(f, Now); Check(c.OwnsMovement, "safe meteor hold"); c.ObserveAction(71, 12212, 2);
            Check(!c.OwnsMovement && releases == 1, "no local post-meteor takeover of cannon");
        });
    }

    private static void MiddleWaypoint()
    {
        Vector3? desired=null;
        using var c=new Hoh90MeteorController(_=>true,p=>desired=p,()=>desired=null,p=>p,(_,_)=>true,_=>{});
        var f=Meteor(Center+new Vector3(15,0,0),Center+new Vector3(15,0,0));
        c.Update(f,Now);var edge=c.EscapeEdge;
        c.Update(f with {Player=Center+new Vector3(5,0,0)},Now.AddSeconds(1));
        Check(c.EscapeEdge==edge && desired==edge && edge is {} e && e.X<Center.X-23,"one committed far-edge destination, no short waypoint holds");
    }
    private static void CannonDuringMeteor()
    {
        using var c=new Hoh90MeteorController(_=>true,_=>{},()=>{},p=>p,(_,_)=>true,_=>{});
        var f=Meteor(Center,Center) with {Casts=[new(71,8865,12212,1,Center,Center,0,3.2f),new(42,9199,12215,1,Center,Center,0,1.7f)]};
        c.Update(f,Now);
        Check(!c.OwnsMovement,"late helper+cannon without the initiating visual belongs to BMR");
    }
}
