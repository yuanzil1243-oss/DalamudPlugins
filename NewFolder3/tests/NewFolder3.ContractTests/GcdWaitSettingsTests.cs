using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class GcdWaitSettingsTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        { test(); count++; Console.WriteLine("  ok  gcd-wait-" + name); }
        foreach (int seconds in new[] { 1, 2, 6, 10, 30, 60, 300 })
            Check("user-timeout-boundary-" + seconds, () =>
            {
                var guard = new GcdStallWatchdog();
                Assert(!guard.Update(1000, 42, true, false, seconds));
                Assert(!guard.Update(1000 + seconds * 1000 - 1, 42, true, false, seconds));
                Assert(guard.Update(1000 + seconds * 1000, 42, true, false, seconds));
                Assert(!guard.Update(1_000_000, 42, true, false, seconds));
            });
        Check("legacy-missing-field-defaults-to-six-without-fingerprint-change", () =>
        {
            var settings = JsonSerializer.Deserialize<FarmingSettings>("{\"DungeonId\":4}")!;
            Assert(GcdStallWatchdog.NormalizeWaitSeconds(settings.GcdStallWaitSeconds) == 6);
            Assert(!settings.GcdStallProtection);
            Assert(!JsonSerializer.Serialize(settings).Contains("GcdStallWaitSeconds"));
        });
        foreach (int seconds in new[] { 1, 6, 15, 300 })
            Check("settings-roundtrip-" + seconds, () =>
            {
                var settings = new FarmingSettings { GcdStallProtection = true, GcdStallWaitSeconds = seconds };
                var restored = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(settings))!;
                Assert(restored.GcdStallProtection && restored.GcdStallWaitSeconds == seconds);
            });
        Check("invalid-values-are-bounded-and-zero-is-legacy-default", () =>
        {
            Assert(GcdStallWatchdog.NormalizeWaitSeconds(0) == 6);
            Assert(GcdStallWatchdog.NormalizeWaitSeconds(-10) == 1);
            Assert(GcdStallWatchdog.NormalizeWaitSeconds(int.MinValue) == 1);
            Assert(GcdStallWatchdog.NormalizeWaitSeconds(int.MaxValue) == 300);
        });
        foreach (string delay in new[] { "ability-animation", "casting", "range", "action-not-ready", "queued" })
            Check("transient-" + delay + "-does-not-reset-idle", () =>
            {
                var guard = new GcdStallWatchdog();
                guard.Update(1000, 42, true, false, 4, false);
                Assert(!guard.Update(5000, 42, true, false, 4, false));
                Assert(!guard.Attempted && guard.IdleMilliseconds == 4000);
                Assert(guard.Update(5100, 42, true, false, 4, true));
            });
        Check("cooldown-progress-counts-from-gcd-start-not-cooldown-end", () =>
        {
            var guard = new GcdStallWatchdog();
            guard.Update(2000, 42, true, true, 6, false, 1000);
            guard.Update(3400, 42, true, true, 6, false, 2400);
            Assert(!guard.Update(3500, 42, true, false, 6));
            Assert(!guard.Update(6999, 42, true, false, 6));
            Assert(guard.Update(7000, 42, true, false, 6));
        });
        Check("short-timeout-never-interrupts-live-gcd", () =>
        {
            var guard = new GcdStallWatchdog();
            Assert(!guard.Update(2400, 42, true, true, 1, true, 2000));
            Assert(guard.Update(3000, 42, true, false, 1));
        });
        Check("native-rejection-does-not-claim-progress-or-repeat", () =>
        {
            var guard = new GcdStallWatchdog(); guard.Update(1000, 42, true, false, 2);
            Assert(guard.Update(3000, 42, true, false, 2));
            Assert(!guard.Update(10000, 42, true, false, 2));
            Assert(guard.Attempted);
            guard.Update(11000, 42, true, true, 2, false, 0);
            Assert(!guard.Attempted);
            Assert(guard.Update(14000, 42, true, false, 2));
        });
        Check("target-change-restarts-custom-wait", () =>
        {
            var guard = new GcdStallWatchdog(); guard.Update(1000, 42, true, false, 3);
            Assert(!guard.Update(3999, 43, true, false, 3));
            Assert(!guard.Update(6998, 43, true, false, 3));
            Assert(guard.Update(6999, 43, true, false, 3));
        });
        Check("toggle-job-or-setting-reset-discards-old-deadline", () =>
        {
            var guard = new GcdStallWatchdog(); guard.Update(1000, 42, true, false, 1);
            guard.Reset();
            Assert(!guard.Update(5000, 42, true, false, 10));
            Assert(!guard.Update(14999, 42, true, false, 10));
            Assert(guard.Update(15000, 42, true, false, 10));
        });
        Check("outside-deep-dungeon-does-not-inherit-a-pending-stall", () =>
        {
            var guard = new GcdStallWatchdog();
            Assert(guard.UpdateScope(true, true));
            Assert(!guard.Update(1000, 42, true, false, 6));
            Assert(!guard.Update(6999, 42, true, false, 6));
            Assert(!guard.UpdateScope(true, false));
            Assert(guard.IdleMilliseconds == 0 && !guard.Attempted);
            Assert(!guard.UpdateScope(true, false));
            Assert(guard.UpdateScope(true, true));
            Assert(!guard.Update(20000, 42, true, false, 6));
            Assert(!guard.Update(25999, 42, true, false, 6));
            Assert(guard.Update(26000, 42, true, false, 6));
        });
        Check("leaving-after-an-attempt-rearms-only-after-a-new-wait", () =>
        {
            var guard = new GcdStallWatchdog();
            Assert(guard.UpdateScope(true, true));
            guard.Update(1000, 42, true, false, 2);
            Assert(guard.Update(3000, 42, true, false, 2));
            Assert(guard.Attempted);
            Assert(!guard.UpdateScope(true, false));
            Assert(!guard.Attempted && guard.IdleMilliseconds == 0);
            Assert(guard.UpdateScope(true, true));
            Assert(!guard.Update(10000, 42, true, false, 2));
            Assert(guard.Update(12000, 42, true, false, 2));
        });
        Check("remaining-in-deep-dungeon-preserves-user-wait", () =>
        {
            var guard = new GcdStallWatchdog();
            Assert(guard.UpdateScope(true, true));
            guard.Update(1000, 42, true, false, 3);
            Assert(guard.UpdateScope(true, true));
            Assert(!guard.Update(3999, 42, true, false, 3));
            Assert(guard.UpdateScope(true, true));
            Assert(guard.Update(4000, 42, true, false, 3));
        });
        Check("disabled-scope-discards-pending-deadline", () =>
        {
            var guard = new GcdStallWatchdog();
            guard.Update(1000, 42, true, false, 1);
            Assert(!guard.UpdateScope(false, true));
            Assert(guard.UpdateScope(true, true));
            Assert(!guard.Update(5000, 42, true, false, 1));
            Assert(guard.Update(6000, 42, true, false, 1));
        });
        Check("weapon-dependent-range-is-valid", () =>
            Assert(GcdRecoveryActionPolicy.IsHostileGcd(true, -1, 58, 0)));
        Check("additional-gcd-group-is-valid", () =>
            Assert(GcdRecoveryActionPolicy.IsHostileGcd(true, 25, 0, 58)));
        Check("fixed-melee-range-is-valid", () =>
            Assert(GcdRecoveryActionPolicy.IsHostileGcd(true, 3, 58, 0)));
        Check("ability-self-action-and-unknown-range-are-not-gcds", () =>
        {
            Assert(!GcdRecoveryActionPolicy.IsHostileGcd(true, 25, 1, 0));
            Assert(!GcdRecoveryActionPolicy.IsHostileGcd(false, 25, 58, 0));
            Assert(!GcdRecoveryActionPolicy.IsHostileGcd(true, 0, 58, 0));
            Assert(!GcdRecoveryActionPolicy.IsHostileGcd(true, -2, 58, 0));
        });
        return count;
    }

    private static void Assert(bool condition)
    { if (!condition) throw new InvalidOperationException("GCD wait settings contract failed."); }
}
