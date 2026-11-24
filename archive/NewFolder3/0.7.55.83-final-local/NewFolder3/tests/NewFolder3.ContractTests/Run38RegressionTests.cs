using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class Run38RegressionTests
{
    private static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }
    public static int Run()
    {
        var tests = new (string, Action)[]
        {
            ("delete-menu-unlock-dependent-order-is-not-a-fixed-index", () => {
                string[][] menus = [["进入", "重置攻略进度", "解散固定队伍"],
                    ["进入", "挑战歼灭战", "重置攻略进度", "解散固定队伍"],
                    ["进入", "挑战歼灭战", "挑战深想战", "重置攻略进度", "解散固定队伍"]];
                for (int i = 0; i < menus.Length; i++) Check(DeepDungeonDeleteMenuPolicy.FindResetProgress(menus[i]) == i + 1, "select actual reset row"); }),
            ("delete-menu-reordered-options-keep-the-reset-intent", () =>
                Check(DeepDungeonDeleteMenuPolicy.FindResetProgress(["解散固定队伍", "返回", "重置进度", "进入"]) == 2, "no ordinal assumptions")),
            ("delete-menu-disband-and-conversion-never-match", () =>
                Check(DeepDungeonDeleteMenuPolicy.FindResetProgress(["解散固定队伍", "转换为随机组队的存档", "进入"]) == -1, "do not select conversion")),
            ("delete-menu-duplicate-reset-options-are-rejected", () =>
                Check(DeepDungeonDeleteMenuPolicy.FindResetProgress(["重置攻略进度", "重置进度"]) == -1, "ambiguity")),
            ("delete-menu-empty-and-unreadable-options-are-rejected", () => {
                Check(DeepDungeonDeleteMenuPolicy.FindResetProgress([]) == -1, "empty");
                Check(DeepDungeonDeleteMenuPolicy.FindResetProgress(["", "未知选项"]) == -1, "unreadable"); }),
            ("delete-menu-localized-label-and-punctuation-are-supported", () => {
                foreach (string label in new[] { "重置攻略進度", "重置進度", "Reset progress.", "Reset your progress", " 攻略状況をリセットする ", "　重置 攻略进度。" })
                    Check(DeepDungeonDeleteMenuPolicy.FindResetProgress([label]) == 0, label); }),
            ("delete-menu-confirmations-and-descriptions-are-not-actions", () =>
                Check(DeepDungeonDeleteMenuPolicy.FindResetProgress(["同意上述内容并删除存档", "通过重置攻略进度来删除存档", "不要重置攻略进度"]) == -1, "exact action semantics")),
            ("hoard-option-can-be-set-before-the-run-starts", () => {
                var session = Session(false); Check(session.TrySetTargets(new(true, true, true, false)) && session.BandedEnabled, "pre-start choice"); }),
            ("hoard-option-is-locked-on-and-off-after-initialize", () => {
                foreach (bool enabled in new[] { false, true }) {
                    var session = Session(enabled); using var context = Context(); using var scenario = new FarmingScenario(session);
                    scenario.Initialize(context);
                    Check(!session.TrySetTargets(new(!enabled, true, true, false)), "reject mid-run toggle");
                    Check(session.BandedEnabled == enabled && context.RunOptions.Current.BandedEnabled == enabled, "keep captured intent");
                } }),
            ("hoard-rejected-change-does-not-partially-update-chest-targets", () => {
                var session = new FarmingSession(new(FarmingMode.Hoard, SaveUse.Create, 21, 2, false, true, true, true, false));
                session.LockHoardTarget(); Check(!session.TrySetTargets(new(false, false, false, true)), "reject toggle");
                Check(session.BandedEnabled && session.OpenGold && session.OpenSilver && !session.OpenBronze, "atomic rejection"); }),
            ("hoard-lock-does-not-disable-other-permitted-chest-options", () => {
                var session = new FarmingSession(new(FarmingMode.Hoard, SaveUse.Create, 21, 2, false, true, true, true, false));
                session.LockHoardTarget(); Check(session.TrySetTargets(new(true, false, false, true)) && !session.OpenGold && session.OpenBronze, "preserve other live options"); }),
            ("hoard-lock-survives-floorset-continuation", () => {
                var session = Session(true); using (var context = Context()) using (var scenario = new FarmingScenario(session)) scenario.Initialize(context);
                session.CompleteAttempt(FarmingSessionPolicy.Decide(session.Plan, new(31, true, false, false, false)), 0);
                using var nextContext = Context(); using var next = new FarmingScenario(session); next.Initialize(nextContext);
                Check(!session.TrySetTargets(new(false, true, true, false)) && nextContext.RunOptions.Current.BandedEnabled, "same session remains locked"); }),
            ("hoard-lock-does-not-leak-into-the-next-task", () => {
                var previous = Session(true); previous.LockHoardTarget(); var next = Session(false);
                Check(next.TrySetTargets(new(true, true, true, false)) && next.BandedEnabled, "new task may choose again"); }),
        };
        foreach (var (name, action) in tests) { action(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private static FarmingSession Session(bool enabled) => new(new(FarmingMode.FreeExploration, SaveUse.Create, 31, 2, false, enabled, true, true, false));
    private static RunContext Context() => new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
}
