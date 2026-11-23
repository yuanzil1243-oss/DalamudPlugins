using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class Hoh70FloorFlowTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 2, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh70-centaur-identified-by-native-ids-not-localized-name", () => {
                Check(Hoh70FloorPolicy.IsCentaur(8995,0) && Hoh70FloorPolicy.IsCentaur(0,7584), "verified source IDs");
                Check(!Hoh70FloorPolicy.IsCentaur(8996,7585), "adjacent monster IDs are not ranged exclusions"); }),
            ("hoh70-centaur-not-a-voluntary-pull-on-any-normal-hoh-floor", () => {
                foreach (int f in Enumerable.Range(1,99).Where(f=>f%10!=0))
                    Check(!Hoh70FloorPolicy.AllowsVoluntaryTarget(FarmingMode.FreeExploration,2,f,8995,7584,false,2,2), "no initial or supplemental centaur pull"); }),
            ("hoh70-centaur-existing-hate-is-never-discarded-by-room-filter", () => {
                Check(Hoh70FloorPolicy.AllowsVoluntaryTarget(FarmingMode.FreeExploration,2,96,8995,7584,true,2,4), "already pulled is an obligation");
                Check(Hoh70FloorPolicy.Priority(FarmingMode.FreeExploration,2,96,8995,7584,0)>2,"above ordinary HP and stoneskin selection"); }),
            ("hoh70-pt-and-other-modes-retain-target-policies", () => {
                foreach (var (m,d,f) in new (FarmingMode?,uint,int)[]{(FarmingMode.FreeExploration,4,96),(FarmingMode.DeepProgression,2,96),(null,2,96),(FarmingMode.FreeExploration,2,90)}) {
                    Check(Hoh70FloorPolicy.AllowsVoluntaryTarget(m,d,f,8995,7584,false,-1,-1),"outside HoH normal scope");
                    Check(Hoh70FloorPolicy.Priority(m,d,f,8995,7584,2)==2,"priority unchanged"); } }),
            ("hoh70-91plus-acquisition-is-current-room-only", () => {
                Check(Hoh70FloorPolicy.AllowsVoluntaryTarget(FarmingMode.FreeExploration,2,91,8996,7585,false,3,3),"local mob allowed");
                Check(!Hoh70FloorPolicy.AllowsVoluntaryTarget(FarmingMode.FreeExploration,2,91,8996,7585,false,3,5),"do not select across two rooms");
                Check(!Hoh70FloorPolicy.AllowsVoluntaryTarget(FarmingMode.FreeExploration,2,91,8996,7585,false,-1,-1),"unknown room cannot create a new pull"); }),
            ("hoh70-81through89-keeps-existing-wide-acquisition", () => {
                Check(Hoh70FloorPolicy.AllowsVoluntaryTarget(FarmingMode.FreeExploration,2,86,8996,7585,false,3,5),"no accidental 91plus adoption"); }),
            ("hoh70-ordinary-engaged-mobs-survive-room-and-door-transition", () => {
                Check(Hoh70FloorPolicy.AllowsVoluntaryTarget(FarmingMode.FreeExploration,2,99,8996,7585,true,-1,8),"existing hate remains eligible"); }),
            ("hoh70-centaur-preempts-sticky-hp-target-in-shared-hoh-arbitration", () => {
                var s=new HohCombatTargetState();s.BeginFloor(FarmingMode.FreeExploration,2,96);
                HohCombatCandidate C(ulong id,uint b,uint n,uint hp)=>new(id,true,true,true,true,false,Hoh70FloorPolicy.Priority(FarmingMode.FreeExploration,2,96,b,n,0),hp,4);
                Check(s.Select(new[]{C(1,8996,7585,99999)},1,false)==1,"establish old sticky");
                Check(s.Select(new[]{C(1,8996,7585,99999),C(2,8995,7584,70000)},1,false)==2,"centaur wins even against sticky preferred"); }),
            ("hoh70-room-walk-checks-new-room-before-next-move", () => {
                var s=new HohRoomAdvanceState();var hops=Line(4);
                Check(s.Next(0,new[]{0,1,2,3},hops,Now)==-1,"initial scan window");
                Check(s.Next(0,new[]{0,1,2,3},hops,Now.AddSeconds(1))==1,"adjacent room, never remote3");
                Check(s.Next(1,new[]{0,1,2,3},hops,Now.AddSeconds(2))==-1,"enter1 triggers rescan");
                Check(s.Next(1,new[]{0,1,2,3},hops,Now.AddSeconds(3))==2,"only then move to2"); }),
            ("hoh70-room-walk-backs-through-adjacent-room-at-branch", () => {
                var s=new HohRoomAdvanceState();var hops=Line(4);s.Enter(1,Now);s.Enter(0,Now.AddSeconds(1));
                Check(s.Next(0,new[]{0,1,2,3},hops,Now.AddSeconds(2))==1,"toward unvisited2 via1, not directly2"); }),
            ("hoh70-room-path-failure-defers-edge-without-infinite-reissue", () => {
                var s=new HohRoomAdvanceState();var hops=Line(3);s.Enter(1,Now);s.Block(1,0,Now);
                Check(s.Next(1,new[]{0,1,2},hops,Now.AddSeconds(1))==2,"other adjacent branch survives"); }),
            ("hoh70-room-walk-does-not-invent-edge-when-disconnected", () => {
                var s=new HohRoomAdvanceState();var hops=new int[,]{{0,9999},{9999,0}};s.Enter(0,Now);
                Check(s.Next(0,new[]{0,1},hops,Now.AddSeconds(1))==-1,"no nonexistent neighbor"); }),
            ("hoh70-room-walk-revisits-after-full-scan-instead-of-permanent-empty-cache", () => {
                var s=new HohRoomAdvanceState();var hops=Line(2);s.Enter(0,Now);s.Enter(1,Now.AddSeconds(1));
                Check(s.Next(1,new[]{0,1},hops,Now.AddSeconds(2))==0,"a later patrol can discover new visible targets"); }),
            ("hoh70-petrification-frailty-lock-survives-effect-and-door-end", () => {
                var s=new HohPetrificationState();s.BeginFloor(2,93);s.ConfirmUse(Now);s.Observe(true,true,false,Now.AddSeconds(1));
                Check(s.Clearing&&!Hoh70FloorPolicy.AllowsFrailty(s.UsedThisFloor,false),"lock while clearing");
                s.Observe(false,false,true,Now.AddSeconds(40));
                Check(!s.Clearing&&s.UsedThisFloor&&!Hoh70FloorPolicy.AllowsFrailty(s.UsedThisFloor,false),"lock whole floor, not aura only"); }),
            ("hoh70-petrification-lock-resets-before-next-boss", () => {
                var s=new HohPetrificationState();s.BeginFloor(2,89);s.ConfirmUse(Now);s.BeginFloor(2,90);
                Check(!s.UsedThisFloor&&Hoh70FloorPolicy.AllowsFrailty(s.UsedThisFloor,false),"boss frailty remains available");
                Check(HohItemPolicy.AllowBossFrailty(50,100,1)&&!HohItemPolicy.AllowBossFrailty(29,100,1),"old max2 and30percent retained"); }),
            ("hoh70-pending-stone-cannot-interleave-frailty", () => {
                Check(!Hoh70FloorPolicy.AllowsFrailty(false,true),"dispatch is not success but occupies mutual exclusion gate");
                Check(Hoh70FloorPolicy.AllowsFrailty(false,false),"failed unconfirmed stone does not burn whole-floor lock"); }),
            ("hoh70-serenity-reserve-cannot-be-spent-by-emergency-or-overcap", () => {
                foreach(int floor in Enumerable.Range(1,90))foreach(int count in new[]{1,2})foreach(bool overflow in new[]{false,true})
                    Check(!HohItemPolicy.AllowPomander(floor,10,count,floor%10==0,true,overflow),"reserve2 survives all old paths");
                Check(HohItemPolicy.AllowPomander(89,10,3,false,false,true),"surplus may be used, leaving2"); }),
            ("hoh70-deep-serenity-is-for-item-ban-not-other-floor-effects", () => {
                foreach(int floor in Enumerable.Range(91,9))foreach(int count in new[]{1,2,3}) {
                    Check(Hoh70FloorPolicy.AllowsSerenity(floor,count,true),"release reserve for actual ban");
                    Check(!Hoh70FloorPolicy.AllowsSerenity(floor,count,false),"do not waste it on another effect"); }
                Check(!Hoh70FloorPolicy.AllowsSerenity(100,3,true)&&!Hoh70FloorPolicy.AllowsSerenity(95,0,true),"no nonexistent stock/floor"); }),
            ("hoh70-serenity-confirmation-waits-for-floor-ban-sync", () => {
                var budget=new HohFloorItemBudget();budget.RecordPomanderConfirmed(10,Now);
                Check(!budget.AllowsPomander(10,Now.AddSeconds(1)),"do not use a second stock while native ban settles");
                Check(budget.AllowsPomander(10,Now.AddSeconds(5)),"bounded confirmation gate"); }),
            ("hoh70-mimic-93-first-logged-box-correlates-with-new-spawn", () => {
                var s=new HohMimicChestState();var pos=new Vector3(-350.37698f,0,425.7448f);
                s.Begin(1073758362,pos,Now,Array.Empty<ulong>());
                Check(s.Observe(Now.AddMilliseconds(653),true,new[]{new HohMimicCandidate(1073759421,pos,true)}),"actual93 timing and position");
                Check(s.MimicId==1073759421,"retain original/mimic identity across combat");
                Check(!s.Observe(Now.AddSeconds(50),true,Array.Empty<HohMimicCandidate>())&&s.MimicId!=0,"correlation persists after defeat"); }),
            ("hoh70-mimic-93-second-logged-box-is-a-separate-attempt", () => {
                var s=new HohMimicChestState();var pos=new Vector3(-353.35175f,0,414.07477f);
                s.Begin(1073759491,pos,Now,new ulong[]{1073759421});
                Check(s.Observe(Now.AddMilliseconds(522),true,new[]{new HohMimicCandidate(1073759525,pos,true)}),"second native timeline"); }),
            ("hoh70-missing-box-alone-never-confirms-mimic-or-loot", () => {
                var s=new HohMimicChestState();s.Begin(1,Vector3.Zero,Now,Array.Empty<ulong>());
                Check(!s.Observe(Now.AddSeconds(1),true,Array.Empty<HohMimicCandidate>())&&s.MimicId==0,"missing is insufficient"); }),
            ("hoh70-preexisting-mimic-cannot-resolve-an-unrelated-box", () => {
                var s=new HohMimicChestState();s.Begin(1,Vector3.Zero,Now,new ulong[]{2});
                Check(!s.Observe(Now.AddSeconds(1),true,new[]{new HohMimicCandidate(2,Vector3.Zero,true)}),"baseline exclusion"); }),
            ("hoh70-nearby-mimic-needs-original-box-disappearance", () => {
                var s=new HohMimicChestState();s.Begin(1,Vector3.Zero,Now,Array.Empty<ulong>());
                Check(!s.Observe(Now.AddSeconds(1),false,new[]{new HohMimicCandidate(2,Vector3.Zero,true)}),"old box still present"); }),
            ("hoh70-far-late-dead-or-ambiguous-mimics-do-not-fake-completion", () => {
                foreach(var (time,candidates) in new (DateTime,HohMimicCandidate[])[]{
                    (Now.AddSeconds(6),[new(2,Vector3.Zero,true)]),
                    (Now.AddSeconds(1),[new(2,new Vector3(4,0,0),true)]),
                    (Now.AddSeconds(1),[new(2,Vector3.Zero,false)]),
                    (Now.AddSeconds(1),[new(2,Vector3.Zero,true),new(3,Vector3.Zero,true)])}) {
                    var s=new HohMimicChestState();s.Begin(1,Vector3.Zero,Now,Array.Empty<ulong>());
                    Check(!s.Observe(time,true,candidates),"positive proof must be unambiguous and timely"); } }),
        ];
        foreach(var(name,test) in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
    private static int[,] Line(int rooms)
    { var result=new int[rooms,rooms];for(int i=0;i<rooms;i++)for(int j=0;j<rooms;j++)result[i,j]=Math.Abs(i-j);return result; }
}
