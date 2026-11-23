using System.Numerics;
using System.Text.Json;
using DeepDungeon.Fsd.Runtime;

internal static class Hoh66Boss90Tests
{
    private static readonly DateTime Now = new(2026,11,22,0,0,0,DateTimeKind.Utc);
    private static readonly Vector3 Center = new(-300,0,-300);
    private static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    private static Vector3 V(JsonElement e)=>new(e[0].GetSingle(),e[1].GetSingle(),e[2].GetSingle());
    private static Hoh90Frame Frame(Vector3 player,params Hoh90Cast[] casts)=>
        new(true,2,90,true,true,42,9199,true,player,Center,casts,[],Center);
    private static Hoh90Cast Visual(Vector3 origin,uint seq=1)=>new(42,9199,12211,seq,origin,origin,0,3);
    private static Hoh90Cast Helper(Vector3 origin,float remaining=5.7f)=>new(71,8865,12212,1,origin,origin,0,remaining);
    public static IEnumerable<(string,Action)> Cases()
    {
        yield return ("hoh66-native-action-identities-distinguish-visual-helper-and-aoes",()=>{
            Check(Hoh90MeteorController.VisualActionId==12211 && Hoh90MeteorController.MeteorActionId==12212,"visual and real helper differ");
            Check(Hoh90MeteorController.SubductionActionId==12210 && Hoh90MeteorController.AuraCannonActionId==12215,"sphere versus rectangle");
            Check(Hoh90MeteorController.BurningRaveActionId==12213 && Hoh90MeteorController.AncientQuagaActionId==12214 && Hoh90MeteorController.KnucklePressActionId==12216,"all native identities");
        });
        yield return ("hoh66-recorded-first-cannon-is-unsafe-at-real-angle-but-safe-at-legacy-zero",()=>{
            var p=new Hoh90MeteorPlanner(v=>v,(_,_)=>true);
            Vector3 player=new(-282.25348f,0,-314.42548f),boss=new(-288.83807f,0,-315.1751f);
            bool Safe(float rotation)=>p.SegmentSafe(player,player,Now,Now.AddSeconds(3),[new(boss,Now.AddSeconds(2),5.6f,rotation,true)],Center,Now.AddHours(1));
            Check(Safe(0) && !Safe(1.2895701f),"real direction reproduces false-safe classification");
        });
        yield return ("hoh66-recorded-bmr-opening-goal-is-rejected-only-by-wrong-angle",()=>{
            var p=new Hoh90MeteorPlanner(v=>v,(_,_)=>true);
            Vector3 goal=new(-285.86115f,0,-305.9847f),boss=new(-288.83807f,0,-315.1751f);
            bool Safe(float rotation)=>p.SegmentSafe(goal,goal,Now,Now.AddSeconds(3),[new(boss,Now.AddSeconds(2),5.6f,rotation,true)],Center,Now.AddHours(1));
            Check(!Safe(0) && Safe(1.2895701f),"wrong local model is not evidence of BMR failure");
        });
        yield return ("hoh66-opening-replay-never-takes-an-ordinary-movement-lease",OpeningReplay);
        yield return ("hoh66-meteor-on-wrong-actor-is-not-a-real-helper",()=>{
            using var c=Controller();
            c.Update(Frame(Center,new Hoh90Cast(42,9199,12212,0,Center,Center,0,5.7f)),Now);
            Check(c.MeteorOrigin==null && !c.OwnsMovement,"source baseId is part of identity");
        });
        yield return ("hoh66-visual-starts-immediately-toward-opposite-edge",()=>{
            Vector3? desired=null;int leases=0;
            using var c=new Hoh90MeteorController(on=>{if(on)leases++;return true;},p=>desired=p,()=>desired=null,v=>v,(_,_)=>true,_=>{});
            var origin=Center+new Vector3(15,0,0);c.Update(Frame(origin,Visual(origin)),Now);
            Check(leases==1 && c.OwnsMovement && desired is {} to && Math.Abs(to.X-(Center.X-23.5f))<.01f,"first visual frame commits to opposite edge, not merely22m from boss");
            Check(!c.SuppressOutput,"movement does not blanket-disable rotation");
        });
        yield return ("hoh66-direct-escape-reaches-edge-and-briefly-holds",()=>{
            Vector3? desired=null;var player=Center+new Vector3(15,0,0);var origin=player;
            using var c=new Hoh90MeteorController(_=>true,p=>desired=p,()=>desired=null,v=>v,(_,_)=>true,_=>{});
            for(int i=0;i<85;i++){
                if(desired is {} d){var delta=d-player;if(delta.LengthSquared()>.0001)player+=Vector3.Normalize(delta)*Math.Min(delta.Length(),.55f);}
                c.Update(Frame(player,i<30?[Visual(origin) with {Remaining=3-i*.1f}]:[Helper(origin,Math.Max(.1f,12-i*.1f))]),Now.AddSeconds(i*.1));
            }
            Check(c.ReachedEdge && c.OwnsMovement && desired==null && c.Stage.Contains("brief-hold"),"arrival stops only local input and holds before late cast handoff");
        });
        yield return ("hoh66-actual-sand-threat-leaves-one-point-five-seconds-before-readbar-end",()=>{
            var player=Center+new Vector3(15,0,0);var origin=player;
            using var c=Controller();
            var sand=new Hoh90Cast(8,9235,12210,1,Center+new Vector3(5,0,0),Center+new Vector3(5,0,0),0,3);
            c.Update(Frame(player,Visual(origin),sand),Now);Check(c.OwnsMovement,"early travel allowed");
            c.Update(Frame(Center+new Vector3(7,0,0),Visual(origin) with {Remaining=1.5f},sand with {Remaining=1.5f}),Now.AddSeconds(1.5));
            Check(!c.OwnsMovement && c.Stage.Contains("actual-threat-margin") && !c.ReachedEdge,"give BMR its full lead even before reaching edge");
            Check(c.HandoffDeadline is {} at && Math.Abs((at-Now).TotalSeconds-1.5)<.01,"deadline includes cast-end margin and effect delay");
        });
        yield return ("hoh66-irrelevant-sand-does-not-cancel-far-edge-escape",()=>{
            using var c=Controller();var origin=Center+new Vector3(15,0,0);var side=Center+new Vector3(0,0,21);
            c.Update(Frame(origin,Visual(origin),new(8,9235,12210,1,side,side,0,.8f)),Now);
            Check(c.OwnsMovement && c.HandoffDeadline==null,"do not yield to an unrelated explosion on the other side");
        });
        yield return ("hoh66-sphere-birth-alone-never-invents-an-explosion-deadline",()=>{
            using var c=Controller();var origin=Center+new Vector3(15,0,0);
            var f=Frame(origin,Visual(origin)) with {Spheres=[new(8,Center)]};
            c.Update(f,Now);c.Update(f with {Player=Center+new Vector3(5,0,0)},Now.AddSeconds(1.8));
            Check(c.OwnsMovement && c.HandoffDeadline==null,"creation has no pulse timer");
        });
        yield return ("hoh66-early-handoff-is-latched-through-stale-visual-and-helper",()=>{
            int leases=0;using var c=new Hoh90MeteorController(on=>{if(on)leases++;return true;},_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
            var origin=Center+new Vector3(15,0,0);var sand=new Hoh90Cast(8,9235,12210,1,origin,origin,0,1);
            var f=Frame(origin,Visual(origin),sand);c.Update(f,Now);c.Update(f,Now.AddSeconds(.1));
            c.Update(Frame(origin,Helper(origin)),Now.AddSeconds(4));
            Check(!c.OwnsMovement && leases==0,"handoff before initial acquisition does not retry on lingering readbars");
            c.Update(Frame(origin,Visual(origin,2)),Now.AddSeconds(20));
            Check(c.OwnsMovement && leases==1,"a genuinely new visual starts the next cycle");
        });
        yield return ("hoh66-late-handoff-preserves-distance-even-if-bmr-runs-toward-boss",()=>{
            using var c=Controller();var origin=Center+new Vector3(15,0,0);var edge=Center-new Vector3(23.5f,0,0);
            c.Update(Frame(origin,Visual(origin)),Now);
            c.Update(Frame(edge,Helper(origin,5.7f)),Now.AddSeconds(7));
            Check(c.OwnsMovement && c.ReachedEdge,"brief local hold at far edge");
            c.Update(Frame(edge,Helper(origin,.8f)),Now.AddSeconds(11.9));
            Check(!c.OwnsMovement && c.Stage.Contains("late-meteor-cast"),"hand over near damage readbar end");
        });
        yield return ("hoh66-near-center-meteor-cannot-release-with-insufficient-runback-buffer",()=>{
            using var c=Controller();var edge=Center+new Vector3(23.5f,0,0);
            c.Update(Frame(Center,Visual(Center)),Now);
            edge=c.EscapeEdge!.Value;
            c.Update(Frame(edge,Helper(Center,.8f)),Now.AddSeconds(5));
            Check(c.OwnsMovement,"remaining runback could enter attenuation range");
        });
        yield return ("hoh66-refused-release-retries-without-new-escape",()=>{
            int releases=0;using var c=new Hoh90MeteorController(on=>on || ++releases>1,_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
            var f=Frame(Center,Visual(Center));c.Update(f,Now);
            c.Update(f with {Casts=[]},Now.AddSeconds(2.1));Check(!c.OwnsMovement && releases==1,"no movement despite failed restore");
            c.Update(f with {Casts=[]},Now.AddSeconds(2.2));Check(releases==2 && !c.OwnsMovement,"retry BMR restore");
        });
        yield return ("hoh66-actual-combination-replay-hands-over-before-threatening-mud",()=>{
            using var d=Fixture();var clip=d.RootElement.GetProperty("clips")[1];
            VerifyReplay(clip.GetProperty("frames"),clip.GetProperty("bossId").GetUInt64(),".65");
        });
    }
    private static Hoh90MeteorController Controller()=>new(_=>true,_=>{},()=>{},v=>v,(_,_)=>true,_=>{});
    private static JsonDocument Fixture()=>JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures/hoh65-boss90.json")));
    private static Hoh90Cast[] Casts(JsonElement f)=>f.GetProperty("casts").EnumerateArray().Select(c=>new Hoh90Cast(
        c.GetProperty("actor").GetUInt64(),c.GetProperty("baseId").GetUInt32(),c.GetProperty("action").GetUInt32(),0,
        V(c.GetProperty("location")),V(c.GetProperty("position")),c.GetProperty("rotation").GetSingle(),c.GetProperty("remaining").GetSingle())).ToArray();
    private static void OpeningReplay(){
        using var d=Fixture();var root=d.RootElement;int acquired=0,moves=0;
        Check(root.GetProperty("parsedBytes").GetInt32()==14498609 && root.GetProperty("parsedRecords").GetInt32()==404150,"whole actual .65 replay parsed");
        using var c=new Hoh90MeteorController(on=>{if(on)acquired++;return true;},_=>moves++,()=>{},v=>v,(_,_)=>true,_=>{});
        foreach(var f in root.GetProperty("clips")[0].GetProperty("frames").EnumerateArray())
            c.Update(new(true,2,90,true,true,1073757004,9199,true,V(f.GetProperty("player")),Center,Casts(f),[],V(f.GetProperty("bossPosition"))),Now.AddSeconds(f.GetProperty("seconds").GetDouble()));
        Check(acquired==0 && moves==0 && !c.SuppressOutput,"all recorded ordinary frames stay with BMR");
    }
    internal static void VerifyReplay(JsonElement frames,ulong bossId,string label)
    {
        Vector3 player=default;Vector3? desired=null;bool attempted=false,acquired=false,released=false;double started=0,finished=0;
        float distanceAtRelease=0;double lead=0;var notes=new List<string>();double seconds=0;var trace=new List<object>();
        using var c=new Hoh90MeteorController(_=>true,p=>desired=p,()=>desired=null,v=>new(v.X,0,v.Z),(_,_)=>true,n=>notes.Add($"{seconds:F1}: {n}"));
        foreach(var f in frames.EnumerateArray()){
            seconds=f.GetProperty("seconds").GetDouble();var now=Now.AddSeconds(seconds);
            if(!acquired)player=V(f.GetProperty("player"));
            else if(desired is {} to){var delta=to-player;if(delta.LengthSquared()>.0001)player+=Vector3.Normalize(delta)*Math.Min(delta.Length(),.55f);}
            var casts=Casts(f);
            foreach(var e in f.GetProperty("effects").EnumerateArray()){
                if(acquired && !released && e.GetProperty("action").GetUInt32()==12210)
                    Check(Vector2.Distance(Hoh90MeteorPlanner.XZ(player),Hoh90MeteorPlanner.XZ(V(e.GetProperty("position"))))>=9,"local escape must not hold through recorded sand damage");
                c.ObserveAction(e.GetProperty("actor").GetUInt64(),e.GetProperty("action").GetUInt32(),(uint)(seconds*10));
            }
            c.Update(new(true,2,90,true,true,bossId,9199,true,player,Center,casts,[],V(f.GetProperty("bossPosition"))),now);
            if(!attempted && casts.Any(x=>x.BaseId==9199 && x.ActionId==12211)){attempted=true;started=seconds;}
            if(c.OwnsMovement && !acquired){acquired=true;Check(casts.Any(x=>x.BaseId==9199 && x.ActionId==12211),"start exactly on actual boss visual");}
            if(attempted && !c.OwnsMovement && !released && c.Stage.Contains("returned-to-bmr")){
                released=true;finished=seconds;
                distanceAtRelease=c.PlanningOrigin is {} origin?Vector2.Distance(Hoh90MeteorPlanner.XZ(player),Hoh90MeteorPlanner.XZ(origin)):0;
                if(c.HandoffDeadline is {} deadline){lead=(deadline.AddSeconds(1.8)-now).TotalSeconds;Check(lead>=1.64,"BMR retains at least1.5s before readbar end plus effect delay");}
                Check(c.Stage.Contains("returned-to-bmr"),"explicit handoff reason");
            } else if(released)Check(!c.OwnsMovement,"no reacquisition later in same recorded meteor");
            trace.Add(new{seconds,player,c.Stage,c.OwnsMovement,c.ReachedEdge,c.HandoffDeadline});
        }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,$"hoh66-{label.Trim('.')}-escape-trace.json"),JsonSerializer.Serialize(new{started,finished,distanceAtRelease,lead,trace,notes}));
        Check(attempted && released && finished>=started,"replay includes visual and safe release (or declined initial lease); "+string.Join(';',notes));
        Console.WriteLine($"  {label} replay: visual={started:F1}s, BMR handoff={finished:F1}s, localAcquired={acquired}, distance={distanceAtRelease:F2}m, actual-threat effect lead={lead:F2}s; post-handoff BMR NOT simulated");
    }
}

