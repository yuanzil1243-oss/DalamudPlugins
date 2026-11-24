using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Combat;

namespace NewFolder3;

internal static class HohStoneskinPriorityTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static HohStoneskinPriority State(int floor = 64)
    { var s = new HohStoneskinPriority(); s.BeginFloor(FarmingMode.FreeExploration, 2, floor); return s; }
    internal static int Run()
    {
        var now = new DateTime(2026,11,20,10,0,0,DateTimeKind.Utc);
        (string, Action)[] tests =
        [
            ("hoh-stoneskin-scoped-to-normal-hoh-free-exploration", () => {
                Check(HohStoneskinPriority.Enabled(FarmingMode.FreeExploration,2,64),"normal HoH");
                foreach(int f in new[]{0,10,50,70,90,100,101})
                    Check(!HohStoneskinPriority.Enabled(FarmingMode.FreeExploration,2,f),"boss/outside");
                Check(!HohStoneskinPriority.Enabled(FarmingMode.FreeExploration,4,64) &&
                    !HohStoneskinPriority.Enabled(null,2,64),"PT/mode isolation"); }),
            ("hoh-known-jubokko-priority-before-first-cast", () => {
                Check(State().Observe(8961,7349,false,0)==1,"type-based before first cast/shield"); }),
            ("hoh-stoneskin-current-cast-is-urgent", () => {
                Check(State().Observe(8961,7349,true,12274)==2,"actual caster"); }),
            ("hoh-stoneskin-recipient-never-becomes-caster", () => {
                Check(State().Observe(8962,7348,false,0)==0,"shield recipient excluded"); }),
            ("hoh-stoneskin-unknown-cast-does-not-expand-species-table", () => {
                Check(State().Observe(42,43,true,12274)==0,"unknown type never guessed"); }),
            ("hoh-stoneskin-exact-base-and-name-pair", () => {
                Check(State().Observe(8961,1,false,0)==0 && State().Observe(1,7349,false,0)==0,"both rows"); }),
            ("hoh-stoneskin-priority-survives-cast-and-floor-change", () => {
                var s=State(); s.BeginFloor(FarmingMode.FreeExploration,2,65);
                Check(s.Observe(8961,7349,false,12274)==1,"species, not observation"); }),
            ("hoh-stoneskin-reset-isolated-from-pt", () => {
                var s=State(); s.BeginFloor(FarmingMode.FreeExploration,4,64);
                Check(s.Observe(8961,7349,true,12274)==0,"PT disabled"); }),
            ("hoh-stoneskin-uncast-caster-beats-high-hp-non-caster", () => {
                Check(HohStoneskinPriority.Select([new(1,829,25,1,true),new(2,40000,1,0,true)],25)==1,"priority over HP"); }),
            ("hoh-stoneskin-never-pulls-idle-jubokko", () => {
                Check(HohStoneskinPriority.Select([new(1,40000,1,2,false)],25)==0,"no extra pull"); }),
            ("hoh-stoneskin-eligibility-and-range-gates", () => {
                HohStoneskinCandidate[] invalid=[new(1,829,626,2,true),new(2,829,4,2,true,false),
                    new(3,829,4,2,true,Dead:true),new(4,829,4,2,true,Allowed:false),
                    new(5,0,4,2,true),new(6,829,float.NaN,2,true)];
                Check(HohStoneskinPriority.Select(invalid,25)==0,"invalid excluded"); }),
            ("hoh-stoneskin-distant-engaged-caster-remains-chase-priority", () => {
                Check(HohStoneskinPriority.Select([new(1,829,900,1,true)],float.MaxValue)==1,"chase caster"); }),
            ("hoh-stoneskin-stable-tie-order", () => {
                Check(HohStoneskinPriority.Select([new(3,100,1,1,true),new(2,200,4,1,true),new(1,200,4,1,true)],25)==1,"stable order"); }),
            ("hoh-stoneskin-interrupt-role-and-level", () => {
                foreach(uint job in new uint[]{1,3,19,21,32,37}) {
                    Check(HohStoneskinInterrupt.ActionFor(job,18)==7538,"tank");
                    Check(HohStoneskinInterrupt.ActionFor(job,17)==0,"locked"); }
                foreach(uint job in new uint[]{5,23,31,38}) {
                    Check(HohStoneskinInterrupt.ActionFor(job,24)==7551,"ranged");
                    Check(HohStoneskinInterrupt.ActionFor(job,23)==0,"locked"); }
                foreach(uint job in new uint[]{6,20,24,25,28,30,33,34,35,39,40,41,42})
                    Check(HohStoneskinInterrupt.ActionFor(job,100)==0,"other jobs"); }),
            ("hoh-stoneskin-interrupt-needs-interruptible-time-window", () => {
                var s=new HohStoneskinInterrupt();
                Check(s.CanAttempt(1,1,true,2,now),"valid");
                Check(!s.CanAttempt(1,1,false,2,now) && !s.CanAttempt(1,1,true,.1f,now) &&
                    !s.CanAttempt(1,1,true,float.NaN,now),"invalid cast"); }),
            ("hoh-stoneskin-interrupt-accepted-once-per-cast", () => {
                var s=new HohStoneskinInterrupt(); s.ObserveCast(1,true,.1f); s.Record(1,0,true,now);
                s.ObserveCast(1,true,1.5f);
                Check(!s.CanAttempt(1,0,true,1,now.AddSeconds(1)),"no repeat with zero sequence"); }),
            ("hoh-stoneskin-interrupt-rejected-retries-throttled", () => {
                var s=new HohStoneskinInterrupt(); s.Record(1,1,false,now);
                Check(!s.CanAttempt(1,1,true,2,now.AddMilliseconds(100)),"throttle");
                Check(s.CanAttempt(1,1,true,2,now.AddMilliseconds(300)),"retry"); }),
            ("hoh-stoneskin-interrupt-new-cast-reused-zero-sequence", () => {
                var s=new HohStoneskinInterrupt(); s.ObserveCast(1,true,2); s.Record(1,0,true,now);
                s.ObserveCast(1,true,.1f);
                Check(s.CanAttempt(1,0,true,2,now.AddSeconds(30)),"new cast epoch"); }),
            ("hoh-stoneskin-interrupt-reset-clears-old-run", () => {
                var s=new HohStoneskinInterrupt();s.Record(1,1,true,now);s.Reset();
                Check(s.CanAttempt(1,1,true,2,now),"new run"); }),
        ];
        foreach(var(name,test) in tests){test();Console.WriteLine("  ok  "+name);}
        return tests.Length;
    }
}
