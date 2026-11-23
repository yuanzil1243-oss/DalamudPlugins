using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace NewFolder3;
internal static class Hoh63ContinuityBoundaryTests
{
    private static void Check(bool b,string why) { if(!b) throw new Exception(why); }
    internal static int Run()
    {
        var now = new DateTime(2026,11,22,4,0,0,DateTimeKind.Utc);
        var f = new HohContinuityFrame(true,true,true,true,2,90,819,false,false);
        HohRunContinuity Started() { var s=new HohRunContinuity();s.Observe(f,now);return s; }
        (string,Action)[] tests = [
            ("hoh63-terminal-event-during-disconnect-releases-cleanup",()=>{
                var s=Started();s.Observe(f with {LoggedIn=false},now.AddSeconds(1));s.Observe(f with {Terminal=true},now.AddSeconds(2));
                Check(!s.HoldsHost && s.HoldsControls && s.ResumeSequence==0,"must neither deadlock exit nor enable combat");}),
            ("hoh63-terminal-event-during-revival-grace-releases-cleanup",()=>{
                var s=Started();s.ObserveDeath(now);s.Observe(f with {Terminal=true},now.AddSeconds(1));
                Check(!s.HoldsHost && s.Phase==HohContinuityPhase.Exited,"real confirmed terminal evidence wins");}),
            ("hoh63-late-wipe-during-rebuff-does-not-fail-revived-player",()=>{
                var s=Started();s.ObserveDeath(now);s.Observe(f,now.AddSeconds(2));
                Check(s.DeferWipe(false,now.AddSeconds(5)) && s.BossRebuffPending && !s.HoldsHost,"do not restart death timer for a live player");}),
            ("hoh63-late-wipe-after-rebuff-has-bounded-grace",()=>{
                var s=Started();s.ObserveDeath(now);s.Observe(f,now.AddSeconds(2));s.CompleteBossRebuff();
                Check(s.DeferWipe(false,now.AddSeconds(3)),"late event");
                Check(!s.DeferWipe(false,now.AddSeconds(5)),"not an indefinite wipe exemption");}),
            ("hoh63-new-death-after-revival-is-not-ignored",()=>{
                var s=Started();s.ObserveDeath(now);s.Observe(f,now.AddSeconds(2));s.CompleteBossRebuff();
                s.Observe(f with {Dead=true},now.AddSeconds(5));s.Observe(f with {Dead=true},now.AddSeconds(17));
                Check(s.Phase==HohContinuityPhase.ConfirmedDeath && !s.DeferWipe(true,now.AddSeconds(17)),"second death has its own bounded wait");}),
            ("hoh63-disconnected-wipe-does-not-turn-into-revival-wait",()=>{
                var s=Started();s.Observe(f with {LoggedIn=false,PlayerAvailable=false},now.AddSeconds(1));
                Check(s.DeferWipe(false,now.AddSeconds(2)) && s.Phase==HohContinuityPhase.Disconnected,"retain original disconnect identity");}),
            ("hoh63-healthy-attempt-and-confirmed-terminal-do-not-ignore-wipe",()=>{
                var s=Started();Check(!s.DeferWipe(false,now),"ordinary genuine wipe remains fatal");
                s.Observe(f with {Terminal=true},now);Check(!s.DeferWipe(true,now),"terminal cannot be undone");}),
            ("hoh63-reset-attempt-clears-continuity-and-rebuff-state",()=>{
                using var c=new RunContext(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
                c.HohContinuity.Observe(f,now);c.HohContinuity.ObserveDeath(now);c.HohContinuity.Observe(f,now.AddSeconds(2));
                c.ResetAttemptState();Check(!c.HohContinuity.HasIdentity && !c.HohContinuity.HoldsControls && c.HohContinuity.ResumeSequence==0,"per attempt state");})
        ];
        foreach(var (name,test) in tests) {test();Console.WriteLine("  ok  "+name);}
        return tests.Length;
    }
}
