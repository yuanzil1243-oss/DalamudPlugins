using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class Hoh82ShieldAndPassageTests
{
    private static readonly DateTime Now = new(2026,11,23,6,0,0,DateTimeKind.Utc);
    private static readonly HohShieldBreakFrame Frame = new(7,true,true,true,true,true,false,true,true,false);
    private static void Check(bool value,string message) { if(!value) throw new Exception(message); }
    private static HohShieldBreakState State()
    { var s=new HohShieldBreakState(); s.BeginFloor(FarmingMode.FreeExploration,2,91,FsdRotationProvider.AeAssist); return s; }
    internal static int Run()
    {
        (string,Action)[] tests =
        [
            ("hoh82-magicite-ban-id-five-is-independent-of-other-floor-bans",()=>{
                foreach(byte ban in new byte[]{0,1,2,3,4,5,6,7})
                    Check(DeepDungeonFloorItemUsePolicy.CanUseHohMagicite(ban)==(ban!=5),"local client DeepDungeonBan5 is magicite prohibition");
                Check(DeepDungeonFloorItemUsePolicy.CanUsePtIncense(5)&&!DeepDungeonFloorItemUsePolicy.CanUsePtIncense(7),"PT incense remains a separate ban");
                Check(DeepDungeonFloorItemUsePolicy.CanUsePomanders(5),"magicite prohibition must not block normal pomanders");}),
            ("hoh82-shield-waits-for-quiet-window",()=>{
                var s=State();Check(!s.Update(Frame,Now).Cast,"first sight cannot cast");
                Check(!s.Update(Frame,Now.AddSeconds(2.499)).HoldPosition,"allow AE its initial opportunity");
                Check(s.Update(Frame,Now.AddSeconds(2.5)).Cast,"quiet2.5s permits finite help");}),
            ("hoh82-normal-skill-output-delays-assistance-even-absorbed",()=>{
                var s=State();s.Update(Frame,Now);Check(!s.ObserveHit(7,Now.AddSeconds(2),163),"external hit");
                Check(!s.Update(Frame,Now.AddSeconds(3)).Cast,"native skill progress cannot be mistaken for AE stop");
                Check(s.Update(Frame,Now.AddSeconds(4.5)).Cast,"quiet since last native skill");}),
            ("hoh82-autoattack-alone-does-not-prove-ae-resumed",()=>{
                var s=State();s.Update(Frame,Now);s.ObserveHit(7,Now.AddSeconds(2),8);
                Check(s.Update(Frame,Now.AddSeconds(2.5)).Cast,"AA cannot suppress damage-skill fallback");}),
            ("hoh82-request-acceptance-is-not-hit-or-shield-resolution",()=>{
                var s=State();s.Update(Frame,Now);s.Update(Frame,Now.AddSeconds(3));s.RecordAttempt(7,2866,true,Now.AddSeconds(3));
                Check(!s.Update(Frame,Now.AddSeconds(4)).Cast,"wait for GCD, not success");
                Check(s.ObserveHit(7,Now.AddSeconds(3.2),2866),"record only native hit after request");
                Check(s.Update(Frame,Now.AddSeconds(5.5)).Cast,"shield still present may require another hit");}),
            ("hoh82-rejected-request-backs-off-one-second",()=>{
                var s=State();s.Update(Frame,Now);s.Update(Frame,Now.AddSeconds(3));s.RecordAttempt(7,2866,false,Now.AddSeconds(3));
                Check(!s.Update(Frame,Now.AddSeconds(3.999)).Cast,"no per-frame reject loop");
                Check(s.Update(Frame,Now.AddSeconds(4)).Cast,"retry after one second");}),
            ("hoh82-shield-disappears-returns-to-ae-and-reapplication-gets-new-episode",()=>{
                var s=State();s.Update(Frame,Now);s.Update(Frame,Now.AddSeconds(3));s.RecordAttempt(7,2866,true,Now.AddSeconds(3));
                Check(s.Update(Frame with {HasShield=false,AeEnabled=false},Now.AddSeconds(4)).Reason=="shield-gone-return-to-ae","clear even if AE now disabled");
                Check(!s.HoldsOutput(7,Now.AddSeconds(4)),"release ownership");
                Check(s.Update(Frame,Now.AddSeconds(5)).Attempts==0 && !s.Update(Frame,Now.AddSeconds(6)).Cast,"reapplication gets fresh quiet window");}),
            ("hoh82-eight-requests-exhaust-budget-across-selection-switches-and-time",()=>{
                var s=State();s.Update(Frame,Now);
                for(int i=0;i<8;i++) {var at=Now.AddSeconds(3+i*2.5);Check(s.Update(Frame,at).Cast,"allowed request");s.RecordAttempt(7,2866,true,at);}
                Check(!s.Update(Frame,Now.AddSeconds(23)).HoldPosition,"eight attempts release hold");
                s.Update(Frame with {Target=8},Now.AddSeconds(24));
                Check(s.Update(Frame,Now.AddMinutes(3)).Reason=="shield-break-budget-exhausted","no age eviction renews old target budget");}),
            ("hoh82-twenty-four-second-window-bounds-native-unready-hold",()=>{
                var s=State();s.Update(Frame,Now);Check(s.Update(Frame with {ActionReady=false},Now.AddSeconds(3)).HoldPosition,"wait for real readiness");
                Check(!s.Update(Frame,Now.AddSeconds(27)).HoldPosition,"hold ends at window even without requests");}),
            ("hoh82-hold-lease-expires-without-ticks",()=>{
                var s=State();s.Update(Frame,Now);s.Update(Frame,Now.AddSeconds(3));
                Check(s.HoldsOutput(7,Now.AddSeconds(3.5)) && !s.HoldsOutput(7,Now.AddSeconds(3.501)),"stale adapter cannot freeze navigation");}),
            ("hoh82-ineligible-target-and-safety-gates-block-cast-and-hold",()=>{
                HohShieldBreakFrame[] blocked=[Frame with {Eligible=false},Frame with {Target=0},Frame with {Engaged=false},
                    Frame with {AeEnabled=false},Frame with {NativeObserver=false},Frame with {OtherProtection=true},Frame with {OutputPosition=false}];
                foreach(var f in blocked){var s=State();s.Update(Frame,Now);var d=s.Update(f,Now.AddSeconds(3));Check(!d.Cast&&!d.HoldPosition,"all hard safety gates fail closed");}}),
            ("hoh82-casting-and-movement-cannot-directly-fire",()=>{
                var s=State();s.Update(Frame,Now);var d=s.Update(Frame with {PlayerBusy=true},Now.AddSeconds(3));
                Check(!d.Cast&&d.HoldPosition,"request hold to stop owned movement before later cast");}),
            ("hoh82-floor-mode-provider-isolation",()=>{
                foreach(var(mode,dungeon,floor,rotation)in new(FarmingMode?,uint,int,FsdRotationProvider)[]{
                    (FarmingMode.FreeExploration,2,90,FsdRotationProvider.AeAssist),
                    (FarmingMode.FreeExploration,2,100,FsdRotationProvider.AeAssist),
                    (FarmingMode.FreeExploration,4,91,FsdRotationProvider.AeAssist),
                    (null,2,91,FsdRotationProvider.AeAssist),
                    (FarmingMode.FreeExploration,2,91,FsdRotationProvider.Custom)}){
                    var s=State();s.BeginFloor(mode,dungeon,floor,rotation);s.Update(Frame,Now);Check(!s.Update(Frame,Now.AddSeconds(3)).Cast,"ordinary HoH AE only");}}),
            ("hoh82-new-floor-resets-budget",()=>{
                var s=State();s.Update(Frame,Now);s.Update(Frame,Now.AddSeconds(3));s.RecordAttempt(7,2866,true,Now.AddSeconds(3));
                s.BeginFloor(FarmingMode.FreeExploration,2,92,FsdRotationProvider.AeAssist);
                Check(s.Update(Frame,Now.AddSeconds(4)).Attempts==0,"new floor clears per-target episode");}),
            ("hoh82-action-map-does-not-use-other-job-attract-config",()=>{
                Check(HohShieldBreakActions.ForJob(31)==2866&&HohShieldBreakActions.ForJob(27)==163&&
                    HohShieldBreakActions.ForJob(28)==17869&&HohShieldBreakActions.ForJob(42)==34650&&
                    HohShieldBreakActions.ForJob(0)==0,"job bases are explicit, unknown fails closed");}),
            ("hoh82-passage-budget-all-boundaries-and-unknown",()=>{
                foreach(var(p,limit)in new(int?,int)[]{(null,1),(-1,1),(0,6),(5,6),(6,3),(7,3),(8,1),(10,1),(11,0),(12,0)})
                    Check(HohPassagePullPolicy.MaximumEngaged(p)==limit,$"native progress{p} must cap at{limit}");}),
            ("hoh82-passage-progress-rise-cancels-pending-without-inventing-hate",()=>{
                var s=new HohRotationPullState();OutputTargetCandidate[] candidates=[new(1,100,2,0,true,true),new(2,100,28,0,false,false)];
                Check(s.Select(candidates,1<HohPassagePullPolicy.MaximumEngaged(5),true,1,Now)==2,"initial safe pull");
                Check(s.Select(candidates,1<HohPassagePullPolicy.MaximumEngaged(8),true,2,Now.AddSeconds(1))==0&&s.Pending==0,"cancel new target when near-open");}),
        ];
        foreach(var(name,test)in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
}
