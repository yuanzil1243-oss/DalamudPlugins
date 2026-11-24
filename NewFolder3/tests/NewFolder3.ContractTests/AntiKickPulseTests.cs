using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class AntiKickPulseTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        { test(); count++; Console.WriteLine("  ok  anti-kick-" + name); }
        var idle = new AntiKickInputTimers(70, 80, 90);
        Check("under-threshold-does-not-send", () =>
            Assert(new AntiKickPulseScheduler().Update(1000, true, new(59, 59, 59), false) == AntiKickPulseCommand.None));
        Check("exact-threshold-presses", () =>
            Assert(new AntiKickPulseScheduler().Update(1000, true, new(0, 60, 0), false) == AntiKickPulseCommand.Press));
        Check("pulse-spans-frames-and-releases-after-200ms", () =>
        {
            var guard = new AntiKickPulseScheduler();
            Assert(guard.Update(1000, true, idle, false) == AntiKickPulseCommand.Press);
            Assert(guard.Update(1199, true, idle, false) == AntiKickPulseCommand.None && guard.Holding);
            Assert(guard.Update(1200, true, idle, false) == AntiKickPulseCommand.Release && !guard.Holding);
        });
        Check("no-second-press-until-one-minute", () =>
        {
            var guard = new AntiKickPulseScheduler(); guard.Update(1000, true, idle, false);
            guard.Update(1200, true, idle, false);
            Assert(guard.Update(60999, true, idle, false) == AntiKickPulseCommand.None);
            Assert(guard.Update(61000, true, idle, false) == AntiKickPulseCommand.Press);
        });
        Check("turn-off-or-leave-scene-releases-immediately", () =>
        {
            var guard = new AntiKickPulseScheduler(); guard.Update(1000, true, idle, false);
            Assert(guard.Update(1001, false, default, false) == AntiKickPulseCommand.Release && !guard.Holding);
            Assert(guard.Update(1200, false, idle, false) == AntiKickPulseCommand.None);
        });
        Check("outside-scope-no-press-even-with-long-idle", () =>
            Assert(new AntiKickPulseScheduler().Update(1000, false, idle, false) == AntiKickPulseCommand.None));
        Check("physical-modifier-prevents-new-press", () =>
        {
            var guard = new AntiKickPulseScheduler();
            Assert(guard.Update(1000, true, idle, true) == AntiKickPulseCommand.None);
            Assert(guard.Update(1100, true, idle, false) == AntiKickPulseCommand.Press);
        });
        Check("invalid-timers-never-authorize-input", () =>
        {
            foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -1 })
                Assert(new AntiKickPulseScheduler().Update(1000, true, new(invalid, 60, 60), false) == AntiKickPulseCommand.None);
        });
        Check("lost-timer-observation-still-releases-owned-key", () =>
        {
            var guard = new AntiKickPulseScheduler(); guard.Update(1000, true, idle, false);
            Assert(guard.Update(1200, true, new(float.NaN, 0, 0), false) == AntiKickPulseCommand.Release);
        });
        Check("failed-delivery-preserves-backoff", () =>
        {
            var guard = new AntiKickPulseScheduler(); guard.Update(1000, true, idle, false);
            guard.CancelPulse();
            Assert(!guard.Holding && guard.Update(1001, true, idle, false) == AntiKickPulseCommand.None);
            Assert(guard.Update(61000, true, idle, false) == AntiKickPulseCommand.Press);
        });
        Check("clock-rollback-releases-before-new-input", () =>
        {
            var guard = new AntiKickPulseScheduler(); guard.Update(1000, true, idle, false);
            Assert(guard.Update(1, true, idle, false) == AntiKickPulseCommand.Release && !guard.Holding);
        });
        Check("unload-reset-clears-state", () =>
        {
            var guard = new AntiKickPulseScheduler(); guard.Update(1000, true, idle, false); guard.Reset();
            Assert(!guard.Holding);
        });
        Check("observed-reset-is-not-implied-by-input-delivery", () =>
        {
            Assert(!new AntiKickInputTimers(72, 82, 92).ObservedResetSince(idle));
            Assert(new AntiKickInputTimers(2, 2, 2).ObservedResetSince(idle));
            Assert(!new AntiKickInputTimers(2, 82, 2).ObservedResetSince(idle));
            Assert(!new AntiKickInputTimers(float.NaN, 2, 2).ObservedResetSince(idle));
            Assert(!new AntiKickInputTimers(1, 1, 1).ObservedResetSince(new(5, 5, 5)));
        });
        return count;
    }
    private static void Assert(bool condition)
    { if (!condition) throw new InvalidOperationException("Anti-kick pulse contract failed."); }
}
