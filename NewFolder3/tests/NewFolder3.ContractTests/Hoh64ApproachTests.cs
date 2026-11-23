using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace NewFolder3;

internal static class Hoh64ApproachTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 21, 0, 0, DateTimeKind.Utc);
    private static void Check(bool v, string why) { if (!v) throw new Exception(why); }
    private static object Get(object o, string n) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(o)!;
    private static void Set(object o, string n, object v) => o.GetType().GetField(n, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(o,v);
    private static object Call(object o, string n, params object[] args) => o.GetType().GetMethod(n, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(o,args)!;
    internal static int Run()
    {
        (string,Action)[] tests =
        [
            ("hoh64-near-side-is-at-target-not-at-distant-origin", () => {
                var s = new HohTargetApproachState(); var target = new Vector3(28,0,0); s.Begin(Vector3.Zero,target);
                var points=s.Seeds(Vector3.Zero,target).ToArray();
                Check(points.Length==6 && points.All(p=>p.X>21 && p.X<28 && Vector3.Distance(p,target)<=6.01f), "floor56 target beyond15m must still get nearby seeds"); }),
            ("hoh64-opposite-side-keeps-initial-bearing-after-near-leg", () => {
                var s = new HohTargetApproachState(); var target=new Vector3(28,0,0); s.Begin(Vector3.Zero,target); s.Advance();
                Check(s.Seeds(new(24,0,0),target).All(p=>p.X>28), "arrival cannot redefine the opposite side as the same old approach"); }),
            ("hoh64-target-motion-translates-side-seeds", () => {
                var s=new HohTargetApproachState();s.Begin(Vector3.Zero,new(28,0,0));var t=new Vector3(30,1,2);
                Check(s.Seeds(Vector3.Zero,t).All(p=>p.X<t.X && Math.Abs(p.Z-t.Z)<=6), "use current target location with stable direction"); }),
            ("hoh64-two-stages-then-existing-fallback-no-loop", () => {
                var s=new HohTargetApproachState();s.Begin(Vector3.Zero,new(28,0,0));
                Check(s.Stage==HohTargetApproachStage.NearSide && s.Advance() && s.Stage==HohTargetApproachStage.OppositeSide && s.Advance() && !s.Active && !s.Advance(), "finite transitions"); }),
            ("hoh64-invalid-or-overlapping-positions-do-not-generate-nan", () => {
                var s=new HohTargetApproachState();s.Begin(Vector3.Zero,Vector3.Zero);Check(s.Seeds(Vector3.Zero,Vector3.Zero).All(CombatPositionPolicy.IsFinite),"stable overlap bearing");
                s.Begin(new(float.NaN,0,0),Vector3.Zero);Check(!s.Active && !s.Seeds(Vector3.Zero,Vector3.Zero).Any(),"invalid input"); }),
            ("hoh64-height-seeds-do-not-invent-a-bridge-ban", () => {
                var s=new HohTargetApproachState();s.Begin(Vector3.Zero,new(28,1.2f,0));var p=s.Seeds(Vector3.Zero,new(28,1.2f,0)).ToArray();
                Check(p.Length==12 && p.Select(x=>x.Y).Distinct().Order().SequenceEqual(new[]{0f,1.2f}),"actual heights feed existing ground checks"); }),
            ("hoh64-stage-escalation-keeps-target-deadline-and-budget", () => {
                using var f=new Fixture();f.Approach.Begin(Vector3.Zero,new(28,0,0));Set(f.Controller,"_targetId",7UL);Set(f.Controller,"_deadline",Now);
                Check((bool)Call(f.Controller,"TryAdvanceTargetApproach","path-rejected"),"advance near to far");
                Check((ulong)Get(f.Controller,"_targetId")==7 && (DateTime)Get(f.Controller,"_deadline")==Now && f.Context.CombatRecoveryBudget.TotalFailures==0,"do not suppress target, refresh timer or charge whole attempt"); }),
            ("hoh64-second-stage-failure-returns-to-local-search", () => {
                using var f=new Fixture();f.Approach.Begin(Vector3.Zero,new(28,0,0));Call(f.Controller,"TryAdvanceTargetApproach","near");Call(f.Controller,"TryAdvanceTargetApproach","far");
                Check(!f.Approach.Active && !(bool)Call(f.Controller,"TryAdvanceTargetApproach","again") && f.Events.Count==2,"no automatic direction reset"); }),
            ("hoh64-approach-has-own-bounded-path-query-budget", () => {
                using var f=new Fixture();f.Approach.Begin(Vector3.Zero,new(28,0,0));
                int Limit()=> (int)f.Controller.GetType().GetProperty("PathQueryLimit",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(f.Controller)!;
                Check(Limit()==8,"bounded approach search");f.Approach.Advance();f.Approach.Advance();Check(Limit()==12,"existing local budget retained"); }),
            ("hoh64-directed-movement-not-cancelled-by-target-motion", () => {
                using var f=new Fixture();f.Approach.Begin(Vector3.Zero,new(28,0,0));Set(f.Controller,"_ownsPath",true);var field=f.Controller.GetType().GetField("_phase",BindingFlags.NonPublic|BindingFlags.Instance)!;
                field.SetValue(f.Controller,Enum.Parse(field.FieldType,"Moving"));
                Check(!(bool)Call(f.Controller,"ShouldReplanForTargetMovement",new Vector3(28,0,1)),"finish validated ground leg rather than search again in ranged hold radius");Set(f.Controller,"_ownsPath",false); }),
            ("hoh64-arrival-allows-a-real-output-window", () => {
                using var f=new Fixture();f.Approach.Begin(Vector3.Zero,new(28,0,0));Set(f.Controller,"_verifyAt",Now);
                Check(!(bool)Call(f.Controller,"ShouldRejectBlockedArrival",Now.AddSeconds(2),CombatSight.Blocked,false),"no .8s premature rejection");
                Check(!(bool)Call(f.Controller,"ArrivalVerificationExpired",Now.AddSeconds(5)) && (bool)Call(f.Controller,"ArrivalVerificationExpired",Now.AddSeconds(6)),"one bounded real GCD window, not arrival-as-success"); }),
            ("hoh64-ending-attempt-clears-directed-stage", () => {
                using var f=new Fixture();f.Approach.Begin(Vector3.Zero,new(28,0,0));Call(f.Controller,"EndAttempt");Check(!f.Approach.Active && f.Approach.Stage==HohTargetApproachStage.None,"no leak to next target"); }),
            ("hoh64-other-dungeons-and-bosses-cannot-advance-directed-stage", () => {
                foreach(var (dungeon,floor) in new (uint,byte)[]{(1,61),(4,94),(2,60),(2,90)}) {
                    using var f=new Fixture(dungeon,floor);f.Approach.Begin(Vector3.Zero,new(28,0,0));Check(!(bool)Call(f.Controller,"TryAdvanceTargetApproach","test"),"HoH normal-floor only"); } }),
            ("hoh64-target-near-path-still-rejects-other-idle-mobs", () => {
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(new[]{new Vector3(24,0,0)},Vector3.Zero,new[]{new Vector3(12,0,0)}),"approach cannot bypass incidental aggro safety"); }),
            ("hoh64-target-side-path-still-rejects-excessive-length-and-height", () => {
                Check(!CombatPositionPolicy.ValidatePathEndpoint(Vector3.Zero,new(34,0,0),new[]{new Vector3(0,0,8),new Vector3(34,0,0)},out _,out _),"35m cap unchanged");
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(new[]{new Vector3(24,4,0)},Vector3.Zero,Array.Empty<Vector3>()),"height safety unchanged"); }),
            ("hoh64-open-door-ends-stone-pulls-before-effect-expires", () => {
                var s=new HohPetrificationState();s.BeginFloor(2,59);s.ConfirmUse(Now);s.Observe(true,true,false,Now);Check(s.Clearing,"closed gate clearing");s.Observe(true,true,true,Now.AddSeconds(2));
                Check(!s.Clearing && s.StopAttackingAfterClear && !s.AllowsTarget(true,false) && s.AllowsTarget(true,true) && s.AllowsTarget(false,true),"return to normal work; retain existing hate"); }),
            ("hoh64-late-stone-status-cannot-restart-door-open-clearing", () => {
                var s=new HohPetrificationState();s.BeginFloor(2,59);s.ConfirmUse(Now);s.Observe(false,false,true,Now);s.Observe(true,true,true,Now.AddMilliseconds(100));Check(!s.Clearing && !s.AllowsTarget(true),"native delay cannot override gate"); }),
            ("hoh64-stone-ended-with-closed-door-restores-original-exploration", () => {
                var s=new HohPetrificationState();s.BeginFloor(2,59);s.ConfirmUse(Now);s.Observe(true,true,false,Now);s.Observe(false,false,false,Now.AddSeconds(30));Check(!s.Clearing && !s.StopAttackingAfterClear && s.AllowsTarget(false),"ordinary clearing can still open the door"); }),
            ("hoh64-91-through99-release-strength-frailty-and-hold-steel-for-centaur", () => {
                foreach(int floor in Enumerable.Range(91,9)) foreach(uint slot in new uint[]{2,11}) foreach(int stock in new[]{1,2,3})
                    Check(HohItemPolicy.AllowPomander(floor,slot,stock,false,false,false),"no future HoH boss after90");
                Check(!HohItemPolicy.AllowPomander(91,3,3,false,false,false)&&HohItemPolicy.AllowPomander(94,3,1,false,false,false,true),"steel exception is reserved for centaur combat"); }),
            ("hoh64-before90-existing-buff-and-frailty-reserves-remain", () => {
                Check(!HohItemPolicy.AllowPomander(89,2,1,false,false,false) && !HohItemPolicy.AllowPomander(89,3,2,false,false,false) && !HohItemPolicy.AllowPomander(89,11,2,false,true,false),"keep1 strength/2steel/2frailty");
                foreach(uint slot in new uint[]{2,3,11})Check(HohItemPolicy.AllowPomander(90,slot,1,true,false,false),"boss can use reserved stock"); }),
            ("hoh64-boss-frailty-remains-two-uses-and-thirty-percent-cutoff", () => {
                Check(HohItemPolicy.AllowBossFrailty(30,100,1) && !HohItemPolicy.AllowBossFrailty(29,100,1) && !HohItemPolicy.AllowBossFrailty(80,100,2),"no change to90boss refresh limits"); }),
            ("hoh64-magicite-updated-budget-prioritizes-jinba-window", () => {
                foreach(int floor in Enumerable.Range(91,3))Check(!HohItemPolicy.ShouldUseMagicite(floor,3,false,false,true,false),"hold91through93 before Jinba window");
                foreach(int floor in new[]{97,98,99})Check(HohItemPolicy.ShouldUseMagicite(floor,100-floor,false,false,true,false),"enough stock for this and remaining floors");
                Check(!HohItemPolicy.ShouldUseMagicite(99,1,true,false,true,false) && !HohItemPolicy.ShouldUseMagicite(99,1,false,false,true,true) && !HohItemPolicy.ShouldUseMagicite(99,1,false,false,false,false),"door/effect/enemy gates retained"); }),
            ("hoh64-91-through99-stone-and-flight-keep-validity-gates", () => {
                foreach(int floor in Enumerable.Range(91,3))Check(!HohItemPolicy.AllowPetrification(floor,false,false,false,false,false,1),"reserve stone curse until94");
                foreach(int floor in Enumerable.Range(94,6))Check(HohItemPolicy.AllowPetrification(floor,false,false,false,false,false,1),"94plus may accelerate");
                Check(!HohItemPolicy.AllowPetrification(91,false,false,true,false,false,1) && !HohItemPolicy.AllowPetrification(91,false,true,false,false,false,1),"after magicite or open door no new stone curse");
                Check(ProgressionItemPolicy.CanBenefitNextFloor(2,98,100) && !ProgressionItemPolicy.CanBenefitNextFloor(2,99,100),"flight next mob only"); }),
            ("hoh64-buff-planner-still-rejects-active-status-overlap", () => {
                var s=new GeneralAutoPomanderSnapshot {CanAttemptPomanderUse=true,StrengthUsable=true,SteelUsable=true,HasStrengthStatus=true,HasSteelStatus=true,FlightUsable=true,FlightActive=true,NextFloorIsMob=true};
                foreach(uint slot in new uint[]{2,3,5})Check(!GeneralAutoPomanderPlanner.ShouldUseSlot(s,slot),"release reserve does not waste active effects"); }),
        ];
        foreach(var (name,test) in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context=new(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
        internal readonly CombatPositionRecoveryController Controller;
        internal readonly List<string> Events=new();
        internal HohTargetApproachState Approach=>(HohTargetApproachState)Get(Controller,"_targetApproach");
        internal Fixture(uint dungeon=2,byte floor=58)
        {
            Context.FarmingPlan=new(FarmingMode.FreeExploration,SaveUse.Create,1,1,false,false,true,true,false);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty,dungeon);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty,floor);
            Controller=new(Context,null!,null!,null,Context.CombatRecoveryBudget,null,_=>{},(n,_)=>Events.Add(n));
        }
        public void Dispose(){Controller.Dispose();Context.Dispose();}
    }
}
