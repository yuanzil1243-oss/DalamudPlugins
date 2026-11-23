using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class Hoh81RotationPullTests
{
    private static readonly DateTime Now = new(2026, 11, 23, 5, 0, 0, DateTimeKind.Utc);
    private static OutputTargetCandidate Old => new(1, 100, 3, 0, true, true);
    private static OutputTargetCandidate New => new(2, 100, 40, 0, false, false);
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh81-multipull-selects-distant-target-while-old-is-hittable", () => {
                var s = new HohRotationPullState();
                Check(s.Select([Old, New], true, true, 1, Now) == 2 && s.Reason == "navigate-to-output", "must approach, not remain stationary"); }),
            ("hoh81-pending-target-does-not-count-as-engaged", () => {
                var s = new HohRotationPullState();
                for (int i=0;i<50;i++) Check(s.Select([Old,New],true,true,2,Now.AddMilliseconds(i*100)) == 2, "selection alone cannot confirm pull"); }),
            ("hoh81-pull-confirms-only-on-real-engagement", () => {
                var s=new HohRotationPullState();s.Select([Old,New],true,true,1,Now);
                Check(s.Select([Old,New with { Engaged=true }],true,true,2,Now.AddSeconds(1))==0 && s.Pending==0 && s.Reason=="engagement-confirmed","release to existing fight"); }),
            ("hoh81-output-window-starts-after-selection-in-range", () => {
                var s=new HohRotationPullState();s.Select([Old,New],true,true,1,Now);
                Check(s.Select([Old,New],true,true,2,Now.AddSeconds(10))==2,"navigation gets its own bounded time");
                Check(s.Select([Old,New with { CanHit=true }],true,true,1,Now.AddSeconds(11))==2,"wrong actual selection does not prove opportunity");
                Check(s.Select([Old,New with { CanHit=true }],true,true,2,Now.AddSeconds(12))==2,"begin output grace");
                Check(s.Select([Old,New with { CanHit=true }],true,true,2,Now.AddSeconds(18))==0 && s.DeferredTarget==2,"six seconds without engagement defers"); }),
            ("hoh81-unreachable-pull-has-absolute-deadline", () => {
                var s=new HohRotationPullState();s.Select([Old,New],true,true,1,Now);
                Check(s.Select([Old,New],true,true,1,Now.AddSeconds(24))==0 && s.DeferredTarget==2,"no endless path or selection wait");
                Check(s.Select([Old,New],true,true,1,Now.AddSeconds(26))==0,"do not immediately reselect a failed target"); }),
            ("hoh81-safety-gate-cancels-pending-and-casting-blocks-new-start", () => {
                var s=new HohRotationPullState();Check(s.Select([Old,New],true,false,1,Now)==0,"protect cast before starting");
                s.Select([Old,New],true,true,1,Now);
                Check(s.Select([Old,New],true,false,2,Now.AddSeconds(1))==2,"do not clear target during its rotation cast");
                Check(s.Select([Old,New],false,true,2,Now.AddSeconds(2))==0 && s.Pending==0,"door/cap/priority/mechanics gates preempt pull"); }),
            ("hoh81-no-candidate-never-waits-for-quota", () => {
                var s=new HohRotationPullState();Check(s.Select([Old],true,true,1,Now)==0,"return directly to ordinary combat"); }),
            ("hoh81-authoritative-rotation-target-retains-old-hate", () => {
                var s=new HohCombatTargetState();s.BeginFloor(FarmingMode.FreeExploration,2,36);
                HohCombatCandidate old=new(1,true,true,true,true,true,0,100,1200), next=new(2,true,true,false,true,true,0,100,25);
                s.Observe([old,next],true,Now);
                Check(s.Select([old,next],1,true,2)==2 && s.IsEngaged(1) && !s.IsEngaged(2),"output and chase agree without inventing hate");
                Check(s.Select([old,next],2,false,2)==1,"open door refuses new target");
                Check(s.Select([old with { StoneskinPriority=4 },next],2,true,2)==1,"engaged priority enemy preempts"); }),
            ("hoh81-strength-thirty-percent-boundary-without-frailty-count-limit", () => {
                Check(!HohItemPolicy.AllowBossStrengthRefresh(2999,10000),"29.99 stops refresh");
                Check(HohItemPolicy.AllowBossStrengthRefresh(3000,10000) && HohItemPolicy.AllowBossStrengthRefresh(3001,10000),"30 and30.01 allowed");
                Check(!HohItemPolicy.AllowBossStrengthRefresh(0,100) && !HohItemPolicy.AllowBossStrengthRefresh(100,0),"invalid boss fails closed");
                Check(!HohItemPolicy.AllowBossFrailty(50,100,2) && HohItemPolicy.AllowBossStrengthRefresh(50,100),"do not copy frailty two-use maximum"); }),
        ];
        foreach(var(name,test) in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
}
