using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class HohGazeOutputTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
    private static HohGazeObservation Gaze => new(true, 2, 50, true, 123, 9193, true, 11922, 3);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh50-gaze-pauses-from-cast-start", () => Check(new HohGazeOutputGuard().Update(Now, Gaze), "pause before final second")),
            ("hoh50-gaze-keeps-resolution-margin", () => {
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                var idle = Gaze with { CastActionId = 0, CastRemainingSeconds = 0 };
                Check(g.Update(Now.AddSeconds(3.7), idle) && !g.Update(Now.AddSeconds(3.75), idle), "resume only after resolution margin"); }),
            ("hoh50-other-spells-do-not-suppress", () => Check(!new HohGazeOutputGuard().Update(Now, Gaze with { CastActionId = 11924 }), "no pause for spark")),
            ("hoh50-pt-scope-immediately-releases", () => {
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                Check(!g.Update(Now, Gaze with { DungeonId = 4 }) && g.SuppressUntilUtc == default, "PT must never inherit gate"); }),
            ("hoh50-other-floors-immediately-release", () => {
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                Check(!g.Update(Now, Gaze with { Floor = 51 }), "floor lifecycle"); }),
            ("hoh50-stopped-run-immediately-releases", () => {
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                Check(!g.Update(Now, Gaze with { RunActive = false }), "stop lifecycle"); }),
            ("hoh50-dead-player-immediately-releases", () => {
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                Check(!g.Update(Now, Gaze with { PlayerAlive = false }), "death lifecycle"); }),
            ("hoh50-dead-or-missing-boss-releases", () => {
                foreach (var missing in new[] { Gaze with { BossAlive = false }, Gaze with { BossId = 0 } }) {
                    var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                    Check(!g.Update(Now, missing), "boss lifecycle"); } }),
            ("hoh50-wrong-boss-releases", () => {
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                Check(!g.Update(Now, Gaze with { BossBaseId = 18546 }), "no PT boss matching"); }),
            ("hoh50-new-boss-does-not-inherit-deadline", () => {
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                Check(!g.Update(Now, Gaze with { BossId = 456, CastActionId = 0 }), "new object independent"); }),
            ("hoh50-consecutive-casts-refresh-deadline", () => {
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze);
                g.Update(Now.AddSeconds(4), Gaze);
                Check(g.Update(Now.AddSeconds(7.7), Gaze with { CastActionId = 0 }) &&
                    !g.Update(Now.AddSeconds(7.75), Gaze with { CastActionId = 0 }), "second cast independent"); }),
            ("hoh50-invalid-cast-time-cannot-latch", () => {
                foreach (double invalid in new[] { double.NaN, double.PositiveInfinity })
                    Check(!new HohGazeOutputGuard().Update(Now, Gaze with { CastRemainingSeconds = invalid }), "finite deadline");
                var g = new HohGazeOutputGuard(); g.Update(Now, Gaze with { CastRemainingSeconds = -1 });
                Check(!g.Update(Now.AddSeconds(1), Gaze with { CastActionId = 0 }), "negative time bounded"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
}
