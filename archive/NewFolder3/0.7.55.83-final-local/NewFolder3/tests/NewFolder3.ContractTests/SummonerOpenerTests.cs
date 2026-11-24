using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class SummonerOpenerTests
{
    private static readonly DateTime Now = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("summoner-opener-action-policy-covers-level-and-replacement-actions", () => {
                Func<uint, bool, bool, uint, bool, uint> p = SummonerOpenerActionPolicy.Select;
                Check(p(57, true, true, 36992, true) == 0, "no summon before level58");
                Check(p(58, true, false, 0, false) == 3581 && p(69, true, false, 0, false) == 3581,
                    "levels58-69 use Dreadwyrm Trance");
                Check(p(70, false, true, 7427, false) == 7427 && p(70, false, true, 36992, false) == 36992,
                    "level70+ follows the native Bahamut replacement, including Solar Bahamut");
                Check(p(80, false, false, 0, true) == 25831 && p(100, false, false, 0, true) == 25831,
                    "Phoenix is available from level80 when its gauge says ready");
                Check(p(100, false, true, 36992, true) == 36992, "ready Solar Bahamut takes priority over Phoenix"); }),
            ("summoner-opener-requires-an-eligible-target-and-ready-action", () => {
                var f = new SummonerOpenerController();
                Check(!f.TryBegin(0, 7427, true, Now) && !f.TryBegin(12, 0, true, Now) &&
                    !f.TryBegin(12, 7427, false, Now) && f.Stage == SummonerOpenerStage.Idle,
                    "missing target, missing ready action and blocked safety gate cannot acquire the hold"); }),
            ("summoner-opener-waits-for-ae-off-and-effect-confirmation", () => {
                var f = new SummonerOpenerController();
                Check(f.TryBegin(12, 7427, true, Now) && f.HoldsOutput, "valid summon acquires temporary hold");
                Check(f.Tick(Now.AddMilliseconds(200), false, false) == 0, "never cast before AE is confirmed off");
                Check(f.Tick(Now.AddMilliseconds(300), true, false) == 7427, "dispatch only after AE off");
                f.RecordCast(true, Now.AddMilliseconds(300));
                Check(f.HoldsOutput && f.Tick(Now.AddMilliseconds(500), true, false) == 0, "accepted call is not effect confirmation");
                f.Tick(Now.AddMilliseconds(600), true, true);
                Check(f.Stage == SummonerOpenerStage.Complete && !f.HoldsOutput, "effect confirmation hands back output"); }),
            ("summoner-opener-times-out-if-ae-never-stops", () => {
                var f = new SummonerOpenerController(); f.TryBegin(12, 36992, true, Now);
                Check(f.Tick(Now.AddMilliseconds(1500), false, false) == 0 && !f.HoldsOutput && f.Stage == SummonerOpenerStage.Failed,
                    "stop request has a 1.5 second bound"); }),
            ("summoner-opener-rejected-cast-and-missing-effect-release-ae", () => {
                foreach (uint action in new uint[] { 3581, 7427, 25831, 36992 })
                {
                    var f = new SummonerOpenerController(); f.TryBegin(12, action, true, Now);
                    Check(f.Tick(Now, true, false) == action, $"action {action} can enter cast phase");
                    f.RecordCast(false, Now); Check(!f.HoldsOutput, $"action {action} rejection releases AE");
                }
                var pending = new SummonerOpenerController(); pending.TryBegin(12, 25831, true, Now);
                pending.Tick(Now, true, false); pending.RecordCast(true, Now);
                pending.Tick(Now.AddSeconds(2), true, false);
                Check(!pending.HoldsOutput && pending.Stage == SummonerOpenerStage.Failed, "effect wait is bounded"); }),
            ("summoner-opener-cancels-on-an-output-safety-gate", () => {
                var f = new SummonerOpenerController(); f.TryBegin(12, 7427, true, Now); f.Abort();
                Check(!f.HoldsOutput && f.Stage == SummonerOpenerStage.Failed, "other output gates always regain ownership"); }),
            ("summoner-opener-resets-for-next-combat", () => {
                var f = new SummonerOpenerController(); f.TryBegin(12, 7427, true, Now); f.Abort(); f.ResetCombat();
                Check(f.TryBegin(13, 25831, true, Now.AddSeconds(3)) && f.TargetId == 13, "new combat permits a fresh bounded attempt"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
}
