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

internal static class Hoh77ApproachPathTests
{
    private static void Check(bool b, string why) { if (!b) throw new Exception(why); }
    private static bool Validate(Vector3[] path, HohApproachObstacle[] enemies, int capacity, out string reason) =>
        HohApproachPathPolicy.TryValidate(path, Vector3.Zero, enemies, capacity, out _, out _, out _, out _, out _, out reason);
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh77-recorded29-ordinary-blocker-no-longer-vetoes-legal-pull", () => {
                var start = new Vector3(303.52612f, 0, -349.79132f);
                Vector3[] path = [new(262.9832f, .089184165f, -336.7832f)];
                var enemy = new Vector3(260.99106f, 0, -345.38245f);
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(path, start, [enemy], 160), "reproduce .76 veto");
                Check(HohApproachPathPolicy.TryValidate(path,start,[new(13,enemy,true)],int.MaxValue,out _,out _,out _,out _,out var count,out _)
                    && count==1, "ordinary allowed incidental pull can approach"); }),
            ("hoh77-forbidden-unit-on-same-recorded-route-still-blocks", () => {
                Check(!Validate([new(24,0,0)],[new(1,new(12,0,0),false)],5,out var why) && why=="unengaged-enemy-on-local-path", "hard protection"); }),
            ("hoh77-high-floor-capacity-reserves-selected-unengaged-target", () => {
                Check(HohApproachPathPolicy.IncidentalCapacity(true,0,false)==5 && HohApproachPathPolicy.IncidentalCapacity(true,4,false)==1, "selected target occupies one slot"); }),
            ("hoh77-existing-target-not-double-counted", () => {
                Check(HohApproachPathPolicy.IncidentalCapacity(true,5,true)==1 && HohApproachPathPolicy.IncidentalCapacity(true,6,true)==0, "existing hate already occupies slot"); }),
            ("hoh77-early-floor-has-no-new-user-pull-quota", () => {
                Check(HohApproachPathPolicy.IncidentalCapacity(false,9,false)==int.MaxValue, "reuse eligibility without inventing a low floor cap"); }),
            ("hoh77-complete-route-counts-all-incidental-pulls", () => {
                HohApproachObstacle[] e=[new(1,new(8,0,0),true),new(2,new(17,0,0),true)];
                Check(!Validate([new(24,0,0)],e,1,out var why)&&why=="hoh-approach-pull-capacity", "cannot ignore combined quota");
                Check(Validate([new(24,0,0)],e,2,out _), "exactly enough room"); }),
            ("hoh77-duplicate-observation-counts-one-enemy", () => {
                Check(Validate([new(24,0,0)],[new(1,new(10,0,0),true),new(1,new(10,0,0),true)],1,out _), "deduplicate actor"); }),
            ("hoh77-hard-protection-not-bypassed-by-duplicate-id", () => {
                Check(!Validate([new(24,0,0)],[new(1,new(10,0,0),true),new(1,new(10,0,0),false)],2,out _), "prohibition wins"); }),
            ("hoh77-retreat-away-from-close-idle-monster-is-not-new-pull", () => {
                Check(Validate([new(-6,0,0)],[new(1,new(.5f,0,0),false)],0,out _), "preserve bounded escape"); }),
            ("hoh77-unknown-enemy-position-fails-closed", () => {
                Check(!Validate([new(24,0,0)],[new(1,new(float.NaN,0,0),true)],5,out _), "eligibility cannot excuse invalid geometry"); }),
            ("hoh77-approach-length-height-and-empty-path-bounds-remain", () => {
                Check(!Validate([new(161,0,0)],[],5,out _) && !Validate([new(10,4,0)],[],5,out _) && !Validate([],[],5,out _), "bounded complete path"); }),
            ("hoh77-output-prefix-stops-at-room-edge-not-mob-feet", () => {
                var p=HohApproachPathPolicy.UntilFirstOutput(Vector3.Zero,[new(40,0,0)],v=>v.X>=18);
                Check(p.Count==1&&p[^1].X==18, "first real firing position, not target center"); }),
            ("hoh77-output-prefix-retains-wall-detour", () => {
                var p=HohApproachPathPolicy.UntilFirstOutput(Vector3.Zero,[new(0,0,20),new(30,0,20),new(30,0,0)],v=>v.X>=22&&v.Z==20);
                Check(p.Count==2&&p[0]==new Vector3(0,0,20)&&p[^1]==new Vector3(22,0,20), "never shortcut through wall"); }),
            ("hoh77-unreachable-output-keeps-bounded-approach-route", () => {
                Vector3[] p=[new(0,0,20),new(30,0,20)];
                Check(HohApproachPathPolicy.UntilFirstOutput(Vector3.Zero,p,_=>false).SequenceEqual(p), "continue inward when no firing point yet"); }),
            ("hoh77-prefix-does-not-check-enemies-on-unused-tail", () => {
                var p=HohApproachPathPolicy.UntilFirstOutput(Vector3.Zero,[new(40,0,0)],v=>v.X>=15);
                Check(Validate(p.ToArray(),[new(1,new(35,0,0),false)],0,out _), "do not veto an area movement will never enter"); }),
            ("hoh77-no-new-target-through-another-room", () => {
                Check(!HohApproachPathPolicy.AllowsTargetRoom(true,false,4,5)&&!HohApproachPathPolicy.AllowsTargetRoom(true,false,4,6), "enter doorway before pulling adjacent or distant room"); }),
            ("hoh77-current-room-occluded-target-can-be-approached", () => {
                Check(HohApproachPathPolicy.AllowsTargetRoom(true,false,4,4), "room restriction cannot disable local line of sight recovery"); }),
            ("hoh77-old-engagement-and-legacy-room-selection-retained", () => {
                Check(HohApproachPathPolicy.AllowsTargetRoom(true,true,4,6)&&HohApproachPathPolicy.AllowsTargetRoom(false,false,-1,-1), "do not discard existing hate or alter PT"); }),
            ("hoh77-unknown-room-does-not-randomly-select-global-monster", () => {
                Check(!HohApproachPathPolicy.AllowsTargetRoom(true,false,-1,-1)&&!HohApproachPathPolicy.AllowsTargetRoom(true,false,3,-1), "navigation reacquires room first"); }),
            ("hoh77-twelve-unstarted-target-plans-do-not-exhaust-floor", () => {
                using var f=new Fixture();
                for(ulong id=1;id<=12;id++){f.Set("_targetId",id);f.Fail();}
                Check(f.Context.CombatRecoveryBudget.TotalFailures==0&&!f.Context.CombatRecoveryBudget.Exhausted&&f.Events.Count(e=>e=="hoh-approach-plan-deferred")==12,
                    "reproduce .76 rapid planning sweep without charging combat movement failures"); }),
            ("hoh77-movement-started-failure-still-charges-budget", () => {
                using var f=new Fixture();f.Set("_targetId",7UL);f.Set("_attemptNavigationStarted",true);f.Fail();
                Check(f.Context.CombatRecoveryBudget.TotalFailures==1&&!f.Events.Contains("hoh-approach-plan-deferred"), "do not hide actual failed relocation"); }),
            ("hoh77-engaged-and-petrified-failures-not-deferred", () => {
                using var f=new Fixture();f.Set("_targetEngaged",true);Check(!f.Defers,"existing hate obligation");
                f.Set("_targetEngaged",false);f.Set("_staticClearTarget",true);Check(!f.Defers,"stone curse deadline retained"); }),
            ("hoh77-planning-deferral-does-not-leak-to-pt-boss-or-hoard", () => {
                foreach(var(d,n,m)in new(uint,byte,FarmingMode)[]{(4,94,FarmingMode.FreeExploration),(2,90,FarmingMode.FreeExploration),(2,32,FarmingMode.Hoard)})
                {using var f=new Fixture(d,n,m);Check(!f.Defers,"scoped behavior only");} }),
            ("hoh77-new-target-clears-prior-navigation-accounting", () => {
                using var f=new Fixture();f.Set("_attemptNavigationStarted",true);f.Controller.ResetEngagedTargetProgress();
                Check(f.Defers,"unstarted new plan must not inherit old movement"); }),
        ];
        foreach(var(name,test)in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context=new(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
        internal readonly CombatPositionRecoveryController Controller;
        internal readonly List<string> Events=[];
        internal Fixture(uint dungeon=2,byte floor=32,FarmingMode mode=FarmingMode.FreeExploration)
        {
            Context.FarmingPlan=new(mode,SaveUse.Create,1,1,false,false,true,true,false);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty,dungeon);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty,floor);
            var nav=new NavigationHelper(new NullNavigator(),()=>false,()=>DateTime.UtcNow,_=>{},_=>{},()=>false);
            Controller=new(Context,nav,new EnemyChaseHelper(),null,Context.CombatRecoveryBudget,null,_=>{},(n,_)=>Events.Add(n));
        }
        internal void Set(string n,object v)=>typeof(CombatPositionRecoveryController).GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(Controller,v);
        internal bool Defers=>(bool)typeof(CombatPositionRecoveryController).GetProperty("DefersUnstartedApproach",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(Controller)!;
        internal void Fail()=>typeof(CombatPositionRecoveryController).GetMethod("Fail",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(Controller,["directed-approach-unreachable: no-safe-local-path-without-new-enemies",DispatchProxy.Create<IBattleChara,EnemyProxy>()]);
        public void Dispose(){Controller.Dispose();Context.Dispose();}
    }
    public class EnemyProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method,object?[]? args)=>method!.Name=="get_CurrentHp"?100U:method.ReturnType.IsValueType?Activator.CreateInstance(method.ReturnType):null;
    }
    private sealed class NullNavigator : INavigator
    {
        public bool PathfindAndMoveTo(Vector3 d,bool a)=>true;
        public bool RepathTo(Vector3 d,bool a)=>true;
        public void CancelAll(){}
    }
}
