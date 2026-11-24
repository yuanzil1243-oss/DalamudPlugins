using System.Numerics;
using System.Text.Json;
using DeepDungeon.Fsd.Runtime;

internal static class Hoh90MeteorTests
{
    private static readonly DateTime Now = new(2026,11,20,8,25,35,DateTimeKind.Utc);
    private static readonly Vector3 Center = new(-300,0,-300);
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static Hoh90Frame Frame(Vector3 player,Vector3? meteor=null) => new(true,2,90,true,true,42,9199,true,
        player,Center,meteor is {} p ? [new(42,9199,12211,1,p,p,0,3),new(71,8865,12212,1,p,p,0,5.7f)] : [],[]);
    public static IEnumerable<(string,Action)> Cases()
    {
        yield return ("hoh90-plans-early-22m-plus-with-deadline-margin", () => {
            var p=new Hoh90MeteorPlanner(v=>v,(_,_)=>true);
            var route=p.Plan(Center,Center,Center,Now.AddSeconds(6),[],Now);
            Check(route!=null && route.DistanceFromMeteor>=22 && route.Eta<5.25,"early escape");
            Check(route!.Steps.All(s=>Vector2.Distance(Hoh90MeteorPlanner.XZ(s.Position),Hoh90MeteorPlanner.XZ(Center))<=23.5),"inside arena"); });
        yield return ("hoh90-checks-the-whole-route-not-just-final-point", () => {
            var p=new Hoh90MeteorPlanner(v=>v,(_,_)=>true);
            Hoh90Danger[] danger=[new(Center,Now.AddSeconds(1),9.6f)];
            Check(!p.SegmentSafe(Center-new Vector3(12,0,0),Center+new Vector3(12,0,0),Now,Now.AddSeconds(2),
                danger,Center+new Vector3(40,0,0),Now.AddSeconds(10)),"unsafe crossing"); });
        yield return ("hoh90-time-window-allows-crossing-before-sphere-resolves", () => {
            var p=new Hoh90MeteorPlanner(v=>v,(_,_)=>true);
            Check(p.SegmentSafe(Center,Center+new Vector3(12,0,0),Now,Now.AddSeconds(2),
                [new(Center,Now.AddSeconds(4),9.6f)],Center-new Vector3(40,0,0),Now.AddSeconds(10)),"leave before hit"); });
        yield return ("hoh90-final-hold-cannot-enter-later-sand-hit", () => {
            var p=new Hoh90MeteorPlanner(v=>v,(_,_)=>true);
            var hold=Center+new Vector3(22,0,0);
            Check(!p.SegmentSafe(hold,hold,Now,Now.AddSeconds(6),[new(hold,Now.AddSeconds(5),9.6f)],Center,Now.AddSeconds(6)),"hold checked"); });
        yield return ("hoh90-other-circle-and-rectangle-aoes-reject-crossings", () => {
            var p=new Hoh90MeteorPlanner(v=>v,(_,_)=>true);
            var meteor=Center-new Vector3(40,0,0);
            Check(!p.SegmentSafe(Center,Center,Now,Now.AddSeconds(2),[new(Center,Now.AddSeconds(1),8.6f)],meteor,Now.AddSeconds(9)),"burning rave");
            Check(!p.SegmentSafe(Center+new Vector3(0,0,12),Center+new Vector3(0,0,12),Now,Now.AddSeconds(2),
                [new(Center,Now.AddSeconds(1),5.6f,0,true)],meteor,Now.AddSeconds(9)),"aura cannon"); });
        yield return ("hoh90-refuses-ungrounded-or-blocked-full-path", () => {
            Check(new Hoh90MeteorPlanner(_=>null,(_,_)=>true).Plan(Center,Center,Center,Now.AddSeconds(6),[],Now)==null,"no ground");
            Check(new Hoh90MeteorPlanner(v=>v,(_,_)=>false).Plan(Center,Center,Center,Now.AddSeconds(6),[],Now)==null,"blocked"); });
        yield return ("hoh90-never-moves-before-bmr-handoff-acceptance", () => {
            int moves=0;
            using var c=new Hoh90MeteorController(_=>false,_=>moves++,()=>{},v=>v,(_,_)=>true,_=>{});
            c.Update(Frame(Center,Center),Now);
            Check(moves==0 && !c.OwnsMovement,"handoff rejected"); });
        yield return ("hoh90-locks-helper-origin-not-later-boss-position", () => {
            using var c=new Hoh90MeteorController(_=>true,_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
            c.Update(Frame(Center,Center),Now);
            var f=Frame(Center,Center+new Vector3(10,0,0));
            c.Update(f,Now.AddMilliseconds(100));
            Check(c.MeteorOrigin==Center,"same helper/sequence cannot chase boss"); });
        yield return ("hoh90-releases-only-matching-meteor-effect", () => {
            int released=0,stops=0;
            using var c=new Hoh90MeteorController(on=>{if(!on)released++;return true;},_=>{},()=>stops++,v=>v,(_,_)=>true,_=>{});
            c.Update(Frame(Center,Center),Now); Check(c.OwnsMovement,"acquire");
            c.ObserveAction(72,12212,9); Check(c.OwnsMovement,"wrong helper ignored");
            c.ObserveAction(71,12210,10); Check(c.OwnsMovement,"sand effect ignored");
            c.ObserveAction(71,12212,11);
            Check(!c.OwnsMovement && c.MeteorOrigin==null && released==1 && stops>0,"release on effect"); });
        yield return ("hoh90-resolved-cast-does-not-reacquire-on-stale-object-frame", () => {
            using var c=new Hoh90MeteorController(_=>true,_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
            var f=Frame(Center,Center); c.Update(f,Now); c.ObserveAction(71,12212,11);
            c.Update(f,Now.AddMilliseconds(100));
            Check(!c.OwnsMovement && c.MeteorOrigin==null,"stale cast ignored"); });
        yield return ("hoh90-scope-death-stop-and-dispose-release-input", () => {
            foreach(var change in new Func<Hoh90Frame,Hoh90Frame>[] {
                f=>f with {Enabled=false},f=>f with {DungeonId=4},f=>f with {Floor=70},
                f=>f with {PlayerAlive=false},f=>f with {InCombat=false},f=>f with {BossAlive=false}}) {
                int released=0;
                using var c=new Hoh90MeteorController(on=>{if(!on)released++;return true;},_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
                var f=Frame(Center,Center);c.Update(f,Now);c.Update(change(f),Now.AddMilliseconds(100));
                Check(!c.OwnsMovement && released==1,"scoped release"); }
            var d=new Hoh90MeteorController(_=>true,_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
            d.Update(Frame(Center,Center),Now);d.Dispose();Check(!d.OwnsMovement,"dispose"); });
        yield return ("hoh90-missing-effect-timeout-is-not-reported-as-success", () => {
            using var c=new Hoh90MeteorController(_=>true,_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
            c.Update(Frame(Center,Center),Now);c.Update(Frame(Center),Now.AddSeconds(9));
            Check(!c.OwnsMovement && c.Stage.Contains("missing-timeout"),"distinct missing-event reason"); });
        yield return ("hoh90-visual-prepares-without-claiming-confirmed-meteor-origin", () => {
            int moves=0;
            using var c=new Hoh90MeteorController(_=>true,_=>moves++,()=>{},v=>v,(_,_)=>true,_=>{});
            c.Update(Frame(Center) with {Casts=[new(42,9199,12211,1,Center,Center,0,3)]},Now);
            Check(c.MeteorOrigin==null && c.PlanningOrigin==Center && c.OwnsMovement && moves>0,"provisional preparation"); });
        yield return ("hoh66-sand-birth-without-visual-does-not-acquire-control", () => {
            using var c=new Hoh90MeteorController(_=>true,_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
            c.Update(Frame(Center) with {Spheres=[new(8,Center+new Vector3(10,0,0))],BossPosition=Center},Now);
            Check(c.MeteorOrigin==null && c.PlanningOrigin==null && !c.OwnsMovement,"only actual boss visual initiates escape"); });
        yield return ("hoh90-actual-helper-overrides-the-provisional-origin", () => {
            using var c=new Hoh90MeteorController(_=>true,_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
            c.Update(Frame(Center) with {Casts=[new(42,9199,12211,1,Center,Center,0,3)]},Now);
            var actual=Center+new Vector3(3,0,0);
            c.Update(Frame(Center,actual),Now.AddSeconds(1));
            Check(c.MeteorOrigin==actual && c.PlanningOrigin==actual,"real helper replaces prediction"); });
        yield return ("hoh90-late-activation-without-reachable-deadline-keeps-bmr", () => {
            int moves=0;
            using var c=new Hoh90MeteorController(_=>true,_=>moves++,()=>{},v=>v,(_,_)=>true,_=>{});
            var f=Frame(Center,Center);
            c.Update(f with {Casts=[f.Casts[1] with {Remaining=.5f}]},Now);
            Check(!c.OwnsMovement && moves==0,"helper alone cannot start a late local takeover"); });
        yield return ("hoh66-last20-replay-starts-at-visual-and-leaves-bmr-time", Replay);
    }
    private static Vector3 V(JsonElement value) => new(value[0].GetSingle(),value[1].GetSingle(),value[2].GetSingle());
    private static void Replay()
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/hoh90-last20.json")));
        Check(doc.RootElement.GetProperty("parsedBytes").GetInt32()==29478263 && doc.RootElement.GetProperty("parsedRecords").GetInt32()==866551,"complete original replay");
        Hoh66Boss90Tests.VerifyReplay(doc.RootElement.GetProperty("frames"),1073760973,".49");
    }
}
