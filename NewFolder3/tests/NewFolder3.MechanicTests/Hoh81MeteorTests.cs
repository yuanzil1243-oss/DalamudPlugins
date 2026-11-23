using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

internal static class Hoh81MeteorTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 4, 54, 15, DateTimeKind.Utc);
    private static readonly Hoh90KitingFrame Frame = new(
        new(true,true,true,2,90,DeepDungeonFloorKind.Boss,false,1), FarmingMode.FreeExploration,
        true,true,true,true,false,42,9199,true,new(-289.29617f,0,-319.6998f),
        new(-295.48996f,0,-310.5223f),new(-300,0,-300),3,.5f,true,12211,3,[])
        { MechanicObserverAvailable=true };
    private static void Check(bool b, string why) { if (!b) throw new Exception(why); }
    private sealed class Rig : IDisposable
    {
        public bool Safe=true; public int Moves, Releases; public Vector3? Destination;
        public readonly Hoh90KitingController C;
        public Rig() { C=new(on=>{if(!on)Releases++;return true;},p=>{Moves++;Destination=p;},
            ()=>Destination=null,(_,_)=>Safe); }
        public void Tick(Hoh90KitingFrame? f=null,double seconds=0)=>C.Update(f??Frame,Now.AddSeconds(seconds));
        public void Dispose()=>C.Dispose();
    }
    private static Hoh90MechanicResolution Impact(double seconds, ulong caster=900, Vector3? origin=null) =>
        new(Now.AddSeconds(seconds),caster,8865,12212,777,origin??Frame.Boss);
    internal static IEnumerable<(string,Action)> Cases()
    {
        yield return ("hoh81-route-full-arena-crossing-is-not-rejected-at24m",()=>{
            int probes=0;Check(Hoh90RouteSafety.Check(new(-23,0,0),new(23,0,0),p=>p,(_,_)=>true,ref probes),"46m safe crossing");
            Check(probes>=62&&probes<=65,"continuous .75m ground and hint samples");});
        yield return ("hoh81-route-safe-endpoint-cannot-hide-middle-hazard",()=>{
            int probes=0;Check(!Hoh90RouteSafety.Check(new(-20,0,0),new(20,0,0),p=>p,
                (_,b)=>Math.Abs(b.X)>3,ref probes),"endpoint-only BMR predicate must be sampled across walk");});
        yield return ("hoh81-route-ground-gap-never-becomes-movement-permission",()=>{
            int probes=0;Check(!Hoh90RouteSafety.Check(new(-20,0,0),new(20,0,0),p=>Math.Abs(p.X)<2?null:p,
                (_,_)=>true,ref probes),"missing ground along crossing");});
        yield return ("hoh81-route-probe-budget-bounds-all-candidate-searches",()=>{
            int probes=Hoh90RouteSafety.ProbeBudget-2,calls=0;
            Check(!Hoh90RouteSafety.Check(new(-23,0,0),new(23,0,0),p=>{calls++;return p;},(_,_)=>true,ref probes),"budget exhaustion rejects");
            Check(calls==2&&probes==Hoh90RouteSafety.ProbeBudget,"strict shared frame budget");});
        yield return ("hoh81-route-invalid-or-outsize-crossing-fails-without-native-probes",()=>{
            int probes=0;Check(!Hoh90RouteSafety.Check(Vector3.Zero,new(float.NaN,0,0),p=>p,(_,_)=>true,ref probes),"invalid");
            Check(!Hoh90RouteSafety.Check(Vector3.Zero,new(50,0,0),p=>p,(_,_)=>true,ref probes)&&probes==0,"outside arena diameter budget");});
        yield return ("hoh81-meteor-real-death-coordinates-require-opposite-half-edge",()=>{
            using var r=new Rig();r.Tick();var d=r.C.MeteorGoal!.Value;
            var away=Vector3.Normalize(Frame.Arena-Frame.Boss);
            Check(Vector3.Dot(d-Frame.Arena,away)>=8,"opposite half is mandatory");
            Check(Vector3.Distance(d,Frame.Boss)>=20.5f&&Vector3.Distance(d,Frame.Arena)>=19.5f,"distant edge");
            Check(Vector3.Distance(d,Frame.Player)>24,"full crossing must exceed old route-length cap");});
        yield return ("hoh81-meteor-native-cast-location-controls-half-before-position-interpolation",()=>{
            using var r=new Rig();var f=Frame with{CastLocation=new(-315,0,-300),Boss=new(-298,0,-300)};r.Tick(f);
            Check(r.C.MeteorOrigin==f.CastLocation&&r.C.MeteorGoal!.Value.X>-292,"ground target freezes correct half");});
        yield return ("hoh81-meteor-moving-boss-never-flips-frozen-half",()=>{
            using var r=new Rig();r.Tick();var goal=r.C.MeteorGoal;r.Tick(Frame with{Boss=new(-310,0,-280)},.2);
            Check(r.C.MeteorGoal==goal&&r.C.OwnsMovement,"moving/interpolating boss cannot hand back or flip half");});
        yield return ("hoh81-meteor-persists-across-next-boss-spell-until-delayed-impact",()=>{
            using var r=new Rig();r.Tick();var f=Frame with{BossCasting=false,Action=0,Remaining=0};r.Tick(f,3.1);
            r.Tick(f with{BossCasting=true,Action=12215,Remaining=3},8);
            Check(r.C.MeteorPending,"visual end and another cast are not meteor resolution");
            r.Tick(f with{Meteors=[new(900,Frame.Boss,5.7f)]},9);
            Check(r.C.MeteorHelper==900&&r.C.MeteorPending,"native helper attached after long variable delay");
            r.Tick(f with{Resolutions=[Impact(15)]},15);
            Check(!r.C.MeteorPending&&!r.C.OwnsMovement&&r.C.Stage=="meteor-impact-confirmed-bmr","native resolution hands back");});
        yield return ("hoh81-meteor-stale-helper-cast-after-effect-cannot-restart-mechanic",()=>{
            using var r=new Rig();r.Tick();var f=Frame with{BossCasting=false,Action=0,Remaining=0,
                Meteors=[new(900,Frame.Boss,0)],Resolutions=[Impact(15)]};r.Tick(f,15);r.Tick(f,15.2);
            Check(!r.C.MeteorPending&&!r.C.OwnsMovement,"do not reacquire during cast-wrapper lag");});
        yield return ("hoh81-meteor-old-native-effect-cannot-release-new-cycle",()=>{
            using var r=new Rig();r.Tick(Frame with{Resolutions=[Impact(-10)]});
            Check(r.C.MeteorPending&&r.C.OwnsMovement,"timestamp belongs to previous mechanic");});
        yield return ("hoh81-meteor-other-helper-and-other-origin-cannot-release",()=>{
            using var r=new Rig();r.Tick(Frame with{Meteors=[new(900,Frame.Boss,5)]});
            r.Tick(Frame with{Resolutions=[Impact(1,901)]},1);Check(r.C.MeteorPending,"helper identity matches");
            using var q=new Rig();q.Tick();q.Tick(Frame with{Resolutions=[Impact(1,901,new(-320,0,-300))]},1);
            Check(q.C.MeteorPending,"unknown helper requires matching ground origin");});
        yield return ("hoh81-meteor-native-effect-without-player-damage-releases",()=>{
            using var r=new Rig();r.Tick();r.Tick(Frame with{Resolutions=[Impact(12)]},12);
            Check(!r.C.MeteorPending,"resolution evidence does not depend on positive player damage");});
        yield return ("hoh81-meteor-unseen-visual-can-start-from-native-helper",()=>{
            using var r=new Rig();r.Tick(Frame with{BossCasting=false,Action=0,Remaining=0,
                Meteors=[new(900,Frame.Boss,5.7f)]});
            Check(r.C.MeteorPending&&r.C.MeteorHelper==900&&r.C.MeteorGoal.HasValue,"late observer still uses actual origin");});
        yield return ("hoh81-meteor-no-route-never-substitutes-near-half-edge",()=>{
            using var r=new Rig{Safe=false};r.Tick();r.Tick(Frame with{BossCasting=false,Action=0,Remaining=0},4);
            Check(r.Moves==0&&r.C.MeteorPending&&!r.C.OwnsMovement,"keep BMR and pending lifecycle without arbitrary fallback");});
        yield return ("hoh81-meteor-no-observer-delegates-and-can-recover-observation",()=>{
            using var r=new Rig();r.Tick(Frame with{MechanicObserverAvailable=false});
            Check(r.Moves==0&&r.C.MeteorPending,"no movement ownership without actual-resolution observer");
            r.Tick(seconds:1);Check(r.C.OwnsMovement,"observer initialization lag is not permanent failure");});
        yield return ("hoh81-meteor-watchdog-is-not-a-synthetic-impact",()=>{
            using var r=new Rig();r.Tick();r.Tick(Frame with{BossCasting=false,Action=0,Remaining=0},31);
            Check(r.C.MeteorPending&&!r.C.OwnsMovement&&r.C.Stage=="meteor-unconfirmed-impact-bmr","timeout only delegates");
            r.Tick(Frame with{BossCasting=false,Action=0,Resolutions=[Impact(32)]},32);
            Check(!r.C.MeteorPending,"late real event still resolves watchdog state");});
        yield return ("hoh81-meteor-arrival-holds-far-edge-until-real-impact",()=>{
            using var r=new Rig();r.Tick();var goal=r.C.MeteorGoal!.Value;
            var f=Frame with{Player=goal,BossCasting=false,Action=0,Remaining=0};r.Tick(f,1);r.Tick(f,10);
            Check(r.C.OwnsMovement&&r.C.MeteorPending&&r.C.Stage=="meteor-holding-far-half-until-impact","do not orbit toward boss while waiting");});
        yield return ("hoh81-meteor-death-and-floor-change-clear-pending",()=>{
            foreach(var f in new[]{Frame with{PlayerReady=false},Frame with{State=Frame.State with{Floor=91}},Frame with{BossAlive=false}})
            {using var r=new Rig();r.Tick();r.Tick(f,.2);Check(!r.C.MeteorPending&&!r.C.OwnsMovement,"scoped terminal release");}});
        yield return ("hoh81-sphere-third-distinct-native-pulse-retires-lingering-object",()=>{
            var a=new Hoh90MechanicResolution(Now,7,9235,12210,1,Vector3.Zero);
            Check(!Hoh90KitingController.SphereResolved(7,[a,a,a]),"duplicate packets are one pulse");
            Check(!Hoh90KitingController.SphereResolved(7,[a,a with{Sequence=2}]),"two pulses remain live");
            Check(Hoh90KitingController.SphereResolved(7,[a,a with{Sequence=2},a with{Sequence=3}]),"three pulses completed");
            Check(!Hoh90KitingController.SphereResolved(8,[a,a with{Sequence=2},a with{Sequence=3}]),"other sphere isolated");});
    }
}
