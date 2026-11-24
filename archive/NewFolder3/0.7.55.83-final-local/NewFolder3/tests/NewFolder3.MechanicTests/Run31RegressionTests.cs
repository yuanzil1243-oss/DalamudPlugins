using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

internal static class Run31RegressionTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static ObjectiveArbiterSnapshot Floor(bool open, bool combat, bool boxes, bool first = true) =>
        new(false, false, false, false, true, open, !open, combat, !open || combat, boxes,
            RequiredChestWork: boxes, PassageFirst: first);
    private static int[,] Graph()
    {
        // Start 0 -> 1 -> passage 2; room 3 is a branch from 1.
        return new[,] { {0,1,2,2}, {1,0,1,1}, {2,1,0,2}, {2,1,2,0} };
    }
    public static IEnumerable<(string, Action)> Cases()
    {
        yield return ("91-before-open-pauses-chests-and-hoard-without-allowing-interaction", () => {
            var s = Floor(false, false, true) with { VisibleBandedChest = true, RequiredHoardDiscovery = true };
            var d = ObjectiveArbiter.Decide(s);
            Check(d.PrimaryObjective == FloorObjectiveKind.ActivatePassage, "open first");
            Check(d.Channels.Transition == CommandChannelPermission.Blocked, "no premature transition");
            Check(d.Channels.Interaction == CommandChannelPermission.Blocked, "no opening boxes while killing for gate");
        });
        yield return ("91-after-open-pending-onway-work-blocks-passage", () => {
            var d = ObjectiveArbiter.Decide(Floor(true, false, true));
            Check(d.PrimaryObjective == FloorObjectiveKind.OpenPlannedChest, "resume boxes");
            Check(d.Channels.Transition == CommandChannelPermission.Blocked, "must finish them first");
        });
        yield return ("91-open-with-hate-pauses-exploration-for-clearing", () => {
            var d = ObjectiveArbiter.Decide(Floor(true, true, true));
            Check(d.PrimaryObjective == FloorObjectiveKind.FinishCombatBeforePassage, "mimic or incidental hate interrupts boxes");
            Check(d.Channels.Interaction == CommandChannelPermission.Blocked, "pause interaction");
        });
        yield return ("91-work-complete-or-policy-abandoned-finally-allows-passage", () => {
            var d = ObjectiveArbiter.Decide(Floor(true, false, false));
            Check(d.PrimaryObjective == FloorObjectiveKind.EnterPassage &&
                d.Channels.Transition == CommandChannelPermission.PrimaryObjective, "no permanently stuck gate");
        });
        yield return ("other-floors-retain-chest-before-gate-default", () => {
            Check(ObjectiveArbiter.Decide(Floor(false, false, true, false)).PrimaryObjective == FloorObjectiveKind.OpenPlannedChest,
                "do not change unrelated modes");
        });
        yield return ("91-route-excludes-branches-and-orders-work-towards-gate", () => {
            var s = new PassageRouteScope(); Check(s.Initialize(0,2,Graph()), "route available");
            var result = s.Filter([new(3,false,true),new(2,false,true),new(0,false,true),new(1,false,true)]);
            Check(result.Select(e => e.RoomIndex).SequenceEqual(new[] {0,1,2}), "no branch detour or backward ordering");
        });
        yield return ("91-replanning-and-late-drops-cannot-add-passed-rooms", () => {
            var s = new PassageRouteScope(); s.Initialize(0,2,Graph()); s.ObserveRoom(1);
            Check(!s.Allows(0) && !s.Allows(3) && s.Allows(1) && s.Allows(2), "forward scope");
            s.ObserveRoom(0); Check(!s.Allows(0), "mechanics displacement must not expand route");
            Check(s.Filter([new(3,false,true),new(0,false,true),new(2,false,true)]).Single().RoomIndex == 2,
                "filter regenerated route");
        });
        yield return ("91-unknown-or-disconnected-gate-does-not-fabricate-route", () => {
            var s = new PassageRouteScope(); Check(!s.Initialize(0,-1,Graph()) && !s.Ready, "unknown passage");
            var graph = Graph(); graph[0,2] = -1;
            Check(!s.Initialize(0,2,graph) && !s.Ready, "disconnected");
            Check(s.Initialize(2,2,Graph()) && s.Rooms.SequenceEqual(new[] {2}), "already in passage room still searches that room");
        });
        yield return ("wisdom-success-blocks-idle-targets-at-69-88-95-98", () => {
            foreach (int floor in new[] {69,88,95,98}) {
                var s = new WisdomCombatState(); s.Sync(4,floor); s.Confirm(Now);
                Check(s.SuppressOutput(2) && !s.AllowsTarget(true), "stop even old output during short retreat");
                s.FinishRetreat();
                Check(s.AllowsTarget(true) && !s.AllowsTarget(false), "old hate only after retreat");
                Check(!PassageCombatPolicy.AllowsNewTarget(true,floor>=91,false,0,false,true), "native passage flag lag cannot reenable pulling");
            }
        });
        yield return ("wisdom-old-hate-cleared-disables-output-and-clears-floor-state", () => {
            var s = new WisdomCombatState(); s.Sync(4,95); s.Confirm(Now); s.FinishRetreat();
            Check(!s.SuppressOutput(1) && s.SuppressOutput(0), "old output resumes, then stops when hate is gone");
            s.Sync(4,96); Check(!s.Confirmed && !s.SuppressOutput(0), "next floor can fight normally");
            s.Confirm(Now); s.Reset(); Check(!s.Confirmed, "new attempt resets");
        });
        yield return ("wisdom-idle-output-suppression-must-not-disable-chest-interaction", () => {
            var wisdom = new WisdomCombatState(); wisdom.Sync(4,95); wisdom.Confirm(Now);
            Check(!wisdom.AllowsExploration(0), "pause exploration during retreat");
            wisdom.FinishRetreat(); Check(!wisdom.AllowsExploration(1), "finish hate first");
            Check(wisdom.SuppressOutput(0) && wisdom.AllowsExploration(0), "stop fighting idle enemies while allowing boxes");
            var d = ObjectiveArbiter.Decide(Floor(true,false,true) with {SuppressCombat=wisdom.SuppressOutput(0)});
            Check(d.PrimaryObjective == FloorObjectiveKind.OpenPlannedChest &&
                d.Channels.Interaction != CommandChannelPermission.Blocked &&
                d.Channels.Combat == CommandChannelPermission.Blocked &&
                d.Channels.Transition == CommandChannelPermission.Blocked, "separate output, interaction and transition permissions");
        });
        yield return ("wisdom-duplicate-confirmation-cannot-extend-wait-for-fixed-enemies", () => {
            var s = new WisdomCombatState(); s.Sync(4,98); s.Confirm(Now); s.Confirm(Now.AddSeconds(7));
            Check(s.RetreatExpired(Now.AddSeconds(8)), "bounded eight-second total");
            s.FinishRetreat(); Check(!s.SuppressOutput(1), "resume local sight recovery even if ranged mob did not follow");
        });
        yield return ("wisdom-switches-real-output-controller-off-on-off-without-repeated-commands", () => {
            var calls = new List<bool>();
            var output = new FsdRotationController(_ => FsdRotationState.Unknown, (_,enabled,_) => {calls.Add(enabled);return true;});
            var snapshot = new DeepDungeonStateSnapshot(true,true,true,4,95,DeepDungeonFloorKind.Mob,false,1);
            var settings = new FsdRotationSettings {Provider = FsdRotationProvider.RotationSolverReborn};
            var wisdom = new WisdomCombatState(); wisdom.Sync(4,95);
            void Tick(int hate) => output.Update(true,FarmingMode.DeepProgression,snapshot,true,settings,wisdom.SuppressOutput(hate));
            Tick(1); wisdom.Confirm(Now); Tick(1); Tick(1); wisdom.FinishRetreat(); Tick(1); Tick(1); Tick(0); Tick(0);
            Check(calls.SequenceEqual(new[]{true,false,true,false}), "pause and restore at real policy boundaries");
            wisdom.Sync(4,96); snapshot = snapshot with {Floor=96}; Tick(0);
            Check(calls.Last(), "next floor reenables ordinary combat");
        });
        yield return ("wisdom-and-mechanics-never-authorize-new-target", () => {
            Check(!PassageCombatPolicy.AllowsNewTarget(true,true,true,1,false,true), "wisdom wins over supplement");
            Check(PassageCombatPolicy.AllowsNewTarget(true,true,true,1,true), "existing hate remains obligation");
            Check(MobMovementPolicy.Choose(true,true,true,true) == MobMovementOwner.Mechanics, "mechanics owns movement");
        });
        yield return ("91-one-enemy-is-cleared-without-waiting-for-two", () => {
            var group = new CombatConsolidationState();
            Check(group.Update(true,1,1), "runtime holds at first hate, not configured cap");
            Check(!PassageCombatPolicy.CanSupplement(1,2,false,5,25,false,false,false), "no cross-room supplement");
            Check(!PassageCombatPolicy.CanSupplement(1,2,true,13,25,false,false,false), "no distant supplement");
        });
        yield return ("91-local-supplement-honors-user-cap-and-solo-targets", () => {
            Check(PassageCombatPolicy.CanSupplement(1,2,true,8,25,false,false,false), "nearby second allowed");
            Check(!PassageCombatPolicy.CanSupplement(2,2,true,8,25,false,false,false), "actual cap two");
            Check(!PassageCombatPolicy.CanSupplement(1,1,true,8,25,false,false,false), "cap one");
            Check(!PassageCombatPolicy.CanSupplement(1,2,true,8,25,true,false,false), "existing dangerous target");
            Check(!PassageCombatPolicy.CanSupplement(1,2,true,8,25,false,false,true), "new dangerous target");
            Check(!PassageCombatPolicy.CanSupplement(1,2,true,8,25,false,true,false), "priority fight");
            Check(!PassageCombatPolicy.AllowsNewTarget(false,true,true,1,false,true), "gate open stops supplement");
        });
        yield return ("local-retreat-rejects-path-through-monsters-even-with-safe-endpoint", () => {
            Check(!PassageCombatPolicy.SafeLocalPath([new(0,0,0),new(18,0,0)], new(0,0,0), [new(9,0,0)]), "monster in middle");
            Check(!PassageCombatPolicy.SafeLocalPath([new(0,0,0),new(0,0,14),new(0,0,0)],new(0,0,0),[]), "bounded actual path length");
        });
        yield return ("local-retreat-allows-moving-away-from-nearby-idle-enemy", () => {
            Check(PassageCombatPolicy.SafeLocalPath([new(0,0,0),new(-8,0,0)],new(0,0,0),[new(6,0,0)]), "safe direction");
            Check(!PassageCombatPolicy.SafeLocalPath([new(0,0,0),new(3,0,0)],new(0,0,0),[new(6,0,0)]), "cannot approach new enemy");
            Check(!PassageCombatPolicy.SafeLocalPath([new(float.NaN,0,0)],Vector3.Zero,[]), "invalid coordinates");
        });
        yield return ("pt10-keeps-attackable-flower-until-dead-despite-center-change", () => {
            BossAddCandidate wounded = new(1935,63161,15697,100,true), fresh = new(1934,63161,63161,1,true);
            Check(BossAddTargetPolicy.Select([wounded,fresh],1935) == 1935, "replay regression");
            Check(BossAddTargetPolicy.Select([wounded with {Hp=30000},fresh with {Hp=1000}],1935) == 1935, "keep safe current add to death");
        });
        yield return ("pt10-reselects-low-hp-then-center-and-recovers-unhittable-target", () => {
            BossAddCandidate a = new(1,100,10,100,true), b = new(2,100,20,1,true), boss = new(3,1000,500,0,true);
            Check(BossAddTargetPolicy.Select([a,b,boss],0) == 1, "wounded add before central fresh one");
            Check(BossAddTargetPolicy.Select([a with {CanHit=false},b,boss],1) == 2, "do not stick to unreachable add");
            Check(BossAddTargetPolicy.Select([a with {Hp=0},b],1) == 2, "dead current releases");
            Check(BossAddTargetPolicy.Select([a with {Hp=20},b],0) == 2, "equal HP uses center");
        });
    }
}
