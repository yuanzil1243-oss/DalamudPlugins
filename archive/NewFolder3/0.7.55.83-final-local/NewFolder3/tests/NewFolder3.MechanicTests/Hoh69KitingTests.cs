using System.Numerics;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Core;

internal static class Hoh69KitingTests
{
    private static readonly DateTime Now = new(2026,11,22,2,0,0,DateTimeKind.Utc);
    private static readonly Hoh90KitingFrame Frame = new(
        new(true,true,true,2,90,DeepDungeonFloorKind.Boss,false,1),FarmingMode.FreeExploration,
        true,true,true,true,false,42,9199,true,new(8,0,0),Vector3.Zero,Vector3.Zero,3,.5f,false,0,0,[]) { MechanicObserverAvailable=true };
    private static void Check(bool b,string why){if(!b)throw new Exception(why);}
    private sealed class Rig : IDisposable
    {
        public bool Accepted=true,Safe=true; public int Acquires,Releases,Moves,Stops;
        public Vector3? Destination; public Func<Vector3,Vector3,bool>? Safety;
        public readonly Hoh90KitingController C;
        public Rig(){C=new(on=>{if(on)Acquires++;else Releases++;return Accepted;},p=>{Destination=p;Moves++;},()=>{Destination=null;Stops++;},(a,b)=>Safety?.Invoke(a,b)??Safe);}
        public void Tick(Hoh90KitingFrame? f=null,double seconds=0)=>C.Update(f??Frame,Now.AddSeconds(seconds));
        public void Dispose()=>C.Dispose();
    }
    internal static IEnumerable<(string,Action)> Cases()
    {
        yield return ("hoh69-normal-movement-increases-close-boss-separation",()=>{
            using var r=new Rig();r.Tick();Check(r.Moves==1&&r.C.OwnsMovement,"owned movement");
            Check(Hoh90KitingController.XZDistance(r.Destination!.Value,Frame.Boss)>8,"retreat instead of face-tanking");});
        yield return ("hoh69-normal-keeps-moving-at-preferred-range",()=>{
            using var r=new Rig();var f=Frame with{Player=new(16,0,0)};r.Tick(f);
            Check(r.Destination is {} d&&Vector3.Distance(d,f.Player)>2,"continuous kiting");});
        yield return ("hoh69-can-retreat-from-overlapping-hitboxes",()=>{
            using var r=new Rig();r.Tick(Frame with{Player=new(.2f,0,0)});
            Check(r.Moves==1&&r.Destination!.Value.Length()>2,"escape even from inside hitbox");});
        yield return ("hoh69-kiting-targets-stay-inside-arena",()=>{
            using var r=new Rig();r.Tick(Frame with{Player=new(22.9f,0,0),Boss=new(18,0,0)});
            Check(r.Destination is {} d&&d.Length()<=23.01f,"edge route bends inside");});
        yield return ("hoh69-readbar-retreats-before-handover",()=>{
            using var r=new Rig();var f=Frame with{Player=new(14,0,0),BossCasting=true,Action=12214,Remaining=5};r.Tick(f);
            Check(r.C.OwnsMovement&&r.Destination is {} d&&d.Length()>=16,"raidwide window opens distance before boss chases");});
        yield return ("hoh69-meteor-uses-larger-distant-retreat",()=>{
            using var r=new Rig();var f=Frame with{BossCasting=true,Action=12211,Remaining=3,Player=new(14,0,0)};r.Tick(f);
            Check(r.C.OwnsMovement&&r.Destination is {} d&&d.Length()>=20.5f,"retreat during meteor cast");});
        yield return ("hoh69-meteor-destination-outside-all-live-spheres",()=>{
            using var r=new Rig();var f=Frame with{Player=new(14,0,0),BossCasting=true,Action=12211,Remaining=3,
                Spheres=[new(new(22,0,0),9)]};r.Tick(f);
            Check(r.Destination is {} d&&Vector3.Distance(d,new(22,0,0))>=10.1f,"sphere radius plus player and margin");});
        yield return ("hoh69-meteor-respects-blocked-route",()=>{
            using var r=new Rig{Safe=false};r.Tick(Frame with{BossCasting=true,Action=12211,Remaining=3});
            Check(r.Moves==0&&!r.C.OwnsMovement,"no blind path through danger");});
        yield return ("hoh81-no-safe-meteor-route-retains-mechanic-and-retries-after-bmr-window",()=>{
            using var r=new Rig{Safe=false};var f=Frame with{BossCasting=true,Action=12211,Remaining=3};r.Tick(f);
            r.Safe=true;r.Tick(f with{Remaining=1},2);Check(r.C.MeteorPending&&r.C.OwnsMovement,"dynamic cleared route can resume fixed-half goal");});
        yield return ("hoh81-meteor-visual-cast-end-does-not-end-mechanic",()=>{
            using var r=new Rig();r.Tick(Frame with{Player=new(14,0,0),BossCasting=true,Action=12211,Remaining=3});
            int moves=r.Moves;r.Tick(Frame,.01);Check(r.C.MeteorPending&&r.C.OwnsMovement&&r.Moves==moves,"retain far-half movement across visual end");});
        yield return ("hoh81-zero-remaining-meteor-retains-native-impact-wait",()=>{
            using var r=new Rig();var f=Frame with{Player=new(14,0,0),BossCasting=true,Action=12211,Remaining=3};r.Tick(f);
            r.Tick(f with{Remaining=0},.01);Check(r.C.MeteorPending&&r.C.OwnsMovement,"zero visual remaining is not actual meteor resolution");});
        yield return ("hoh81-boss-cast-interpolation-does-not-release-meteor-half",()=>{
            using var r=new Rig();var f=Frame with{Player=new(21,0,0),BossCasting=true,Action=12211,Remaining=3};r.Tick(f);
            r.Tick(f with{Boss=new(1,0,0),Remaining=.1f},.01);
            Check(r.C.MeteorPending&&r.C.OwnsMovement,"1m interpolation is not permission to abandon mechanic");});
        yield return ("hoh69-sustained-chase-model-stays-in-arena-and-opens-distance",()=>{
            using var r=new Rig();var f=Frame;float minimum=100;
            for(int i=0;i<400;i++)
            {
                r.Tick(f,i*.15);var p=f.Player;
                if(r.Destination is {} d){var step=d-p;p+=Vector3.Normalize(step)*Math.Min(.9f,step.Length());}
                var b=f.Boss;var chase=p-b;if(chase.Length()>.001f)b+=Vector3.Normalize(chase)*Math.Min(.45f,chase.Length());
                f=f with{Player=p,Boss=b};Check(p.Length()<=23.01f,"bounded path");
                if(i>50)minimum=Math.Min(minimum,Vector3.Distance(p,b));
            }
            Check(minimum>8&&r.Moves>=300,$"chase model min={minimum:F2}, moves={r.Moves}, stage={r.C.Stage}, player={f.Player}, boss={f.Boss}");});
        yield return ("hoh69-distant-safe-meteor-position-does-not-keep-running",()=>{
            using var r=new Rig();r.Tick(Frame with{Player=new(21,0,0),BossCasting=true,Action=12211,Remaining=3});
            Check(r.C.MeteorGoal is {} d&&d.X>=20&&r.C.MeteorPending,"far half edge remains locked");});
        yield return ("hoh81-late-visual-still-starts-far-half-movement-for-delayed-helper",()=>{
            using var r=new Rig();r.Tick(Frame with{BossCasting=true,Action=12211,Remaining=.3f});
            Check(r.Moves==1&&r.C.MeteorPending,"visual remaining must not be mistaken for delayed impact deadline");});
        yield return ("hoh69-rejected-lease-never-writes-movement",()=>{
            using var r=new Rig{Accepted=false};r.Tick();r.Tick(seconds:.2);Check(r.Moves==0,"wait for native BMR quiescence");});
        yield return ("hoh69-lease-timeout-keeps-no-stale-input",()=>{
            using var r=new Rig{Accepted=false};r.Tick();r.Tick(seconds:1.1);Check(r.Destination==null&&!r.C.OwnsMovement&&r.Releases>0,"bounded acquisition");});
        yield return ("hoh69-danger-during-kiting-yields-to-bmr",()=>{
            using var r=new Rig();r.Tick();r.Safe=false;r.Tick(seconds:.2);Check(r.Destination==null&&r.Releases==1,"fresh geometry/hint check");});
        yield return ("hoh69-unexpected-bmr-navigation-releases-local-input",()=>{
            using var r=new Rig();r.Tick();r.Accepted=false;r.Tick(seconds:.2);
            Check(r.Destination==null&&!r.C.OwnsMovement&&r.C.Stage=="bmr-navigation-resumed-release-local-input","never fight a resumed external navigator");});
        yield return ("hoh69-read-failure-is-not-movement-permission",()=>{
            using var r=new Rig{Safety=(_,_)=>throw new Exception("IPC unavailable")};r.Tick();Check(r.Moves==0&&r.C.LastFailure=="IPC unavailable","fail safe");});
        yield return ("hoh69-no-progress-yields-without-stopping-host",()=>{
            using var r=new Rig();r.Tick();r.Tick(seconds:2.1);Check(r.C.Stage=="no-movement-progress-bmr"&&!r.C.OwnsMovement,"bounded movement only");});
        yield return ("hoh69-no-other-dungeon-floor-mode-or-manual-controller",()=>{
            foreach(var f in new[]{Frame with{State=Frame.State with{DungeonId=4}},Frame with{State=Frame.State with{Floor=89}},
                Frame with{State=Frame.State with{Floor=91}},Frame with{Mode=FarmingMode.DeepProgression},
                Frame with{ManualOrbit=true},Frame with{BossBaseId=1}})
            {using var r=new Rig();r.Tick(f);Check(r.Acquires==0&&r.Moves==0,"scope isolated");}});
        yield return ("hoh69-death-reconnect-stop-and-boss-reset-release",()=>{
            foreach(var f in new[]{Frame with{PlayerReady=false},Frame with{RunActive=false},Frame with{InCombat=false},
                Frame with{BossAlive=false},Frame with{BmrConfigured=false},Frame with{State=Frame.State with{IsTransitioning=true}}})
            {using var r=new Rig();r.Tick();r.Tick(f,.01);Check(r.Releases==1&&r.Destination==null,"terminal cleanup");}});
        yield return ("hoh69-bmr-cast-handover-has-settle-window",()=>{
            using var r=new Rig();r.Tick(Frame with{Player=new(14,0,0),BossCasting=true,Action=12214,Remaining=5});r.Tick(seconds:.2);
            int acquisitions=r.Acquires;Check(!r.C.OwnsMovement&&r.Releases==1,"readbar end returns to BMR");r.Tick(seconds:.5);Check(r.Acquires==acquisitions,"do not immediately steal handback");r.Tick(seconds:1.1);Check(r.C.OwnsMovement,"resume normal distance movement");});
    }
}
