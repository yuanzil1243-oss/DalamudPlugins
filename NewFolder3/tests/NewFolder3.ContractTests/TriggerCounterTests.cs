using System.Numerics;
using System.Text.Json;
using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class TriggerCounterTests
{
    private static readonly TriggerScene Scene = new(7, 0x12345, 1288, 4, 71);
    internal static int Run()
    {
        int passed = 0;
        void Check(string name, Action test) { test(); passed++; Console.WriteLine("  ok  counters-" + name); }
        Check("new-user-zero-and-no-unnecessary-file", () => WithFile(path =>
        {
            using var c = new FeatureTriggerCounters(path);
            Require(c.Snapshot() == new FeatureTriggerCountData() && c.Flush() && !File.Exists(path));
        }));
        Check("three-totals-persist-across-reload-and-update", () => WithFile(path =>
        {
            using (var c = new FeatureTriggerCounters(path))
            {
                c.Record(FeatureTriggerKind.AntiDisplacement); c.Record(FeatureTriggerKind.AntiDisplacement);
                c.Record(FeatureTriggerKind.ForcedMovement); c.Record(FeatureTriggerKind.GcdProtection);
                Require(c.Flush());
            }
            using var nextVersion = new FeatureTriggerCounters(path);
            Require(nextVersion.Snapshot() == new FeatureTriggerCountData(1, 2, 1, 1));
            nextVersion.Record(FeatureTriggerKind.GcdProtection); Require(nextVersion.Flush());
            using var again = new FeatureTriggerCounters(path);
            Require(again.Snapshot() == new FeatureTriggerCountData(1, 2, 1, 2));
            Require(File.Exists(path + ".bak"));
        }));
        Check("componentwise-monotonic-primary-and-backup", () => WithFile(path =>
        {
            Write(path, new(1, 1, 9, 3)); Write(path + ".bak", new(1, 8, 2, 5));
            using var c = new FeatureTriggerCounters(path);
            Require(c.Snapshot() == new FeatureTriggerCountData(1, 8, 9, 5));
            c.Record(FeatureTriggerKind.GcdProtection); Require(c.Flush());
            using var reloaded = new FeatureTriggerCounters(path);
            Require(reloaded.Snapshot() == new FeatureTriggerCountData(1, 8, 9, 6));
        }));
        Check("corrupt-primary-recovers-verified-backup-without-deleting-evidence", () => WithFile(path =>
        {
            Write(path + ".bak", new(1, 10, 20, 30)); File.WriteAllText(path, "damaged");
            using var c = new FeatureTriggerCounters(path);
            Require(c.Snapshot() == new FeatureTriggerCountData(1, 10, 20, 30) && c.Flush());
            Require(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*").Length == 1);
            using var recovered = new FeatureTriggerCounters(path);
            Require(recovered.Snapshot() == c.Snapshot());
        }));
        foreach (string invalid in new[] { "bad", "{}", "{\"SchemaVersion\":1,\"AntiDisplacement\":-1,\"ForcedMovement\":2,\"GcdProtection\":3}",
            "{\"SchemaVersion\":2,\"AntiDisplacement\":100,\"ForcedMovement\":200,\"GcdProtection\":300}" })
        {
            string captured = invalid;
            Check("invalid-or-newer-file-kept-" + passed, () => WithFile(path =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, captured);
                using var c = new FeatureTriggerCounters(path); c.Record(FeatureTriggerKind.GcdProtection);
                Require(!c.Flush() && c.SaveError != null && File.ReadAllText(path) == captured);
            }));
        }
        Check("failed-save-keeps-memory-and-retries", () => WithFile(path =>
        {
            string parent = Path.GetDirectoryName(path)!; File.WriteAllText(parent, "fixture obstacle");
            using var c = new FeatureTriggerCounters(path); c.Record(FeatureTriggerKind.AntiDisplacement);
            Require(!c.Flush() && c.Count(FeatureTriggerKind.AntiDisplacement) == 1 && c.SaveError != null);
            File.Delete(parent); Require(c.Flush() && c.SaveError == null);
            using var reloaded = new FeatureTriggerCounters(path); Require(reloaded.Count(FeatureTriggerKind.AntiDisplacement) == 1);
        }));
        Check("long-counter-saturates-instead-of-wrapping", () => WithFile(path =>
        {
            Write(path, new(1, long.MaxValue, long.MaxValue, long.MaxValue));
            using var c = new FeatureTriggerCounters(path);
            foreach (var kind in Enum.GetValues<FeatureTriggerKind>()) { c.Record(kind); Require(c.Count(kind) == long.MaxValue); }
        }));
        Check("concurrent-increments-and-save-are-not-lost", () => WithFile(path =>
        {
            using var c = new FeatureTriggerCounters(path);
            Parallel.For(0, 1000, i => { c.Record((FeatureTriggerKind)(i % 3)); if (i % 100 == 0) Require(c.Flush()); });
            Require(c.Flush()); using var next = new FeatureTriggerCounters(path);
            Require(next.Snapshot() == new FeatureTriggerCountData(1, 334, 333, 333));
        }));
        Check("displacement-identical-burst-deduplicated", () =>
        {
            using var c = new FeatureTriggerCounters(); var tracker = new DisplacementTriggerTracker(c);
            for (int i = 0; i < 100; i++) tracker.Suppressed(Scene, 1, 8, 1, 0, 3, i);
            Require(c.Count(FeatureTriggerKind.AntiDisplacement) == 1);
            tracker.Suppressed(Scene, 1, 8, 1, 0, 3, 500);
            tracker.Suppressed(Scene, 2, 8, 1, 0, 3, 501);
            Require(c.Count(FeatureTriggerKind.AntiDisplacement) == 3);
        });
        Check("displacement-scope-change-and-reset-keep-total", () =>
        {
            using var c = new FeatureTriggerCounters(); var tracker = new DisplacementTriggerTracker(c);
            tracker.Suppressed(Scene, 1, 8, 1, 0, 3, 0);
            tracker.Suppressed(Scene with { Floor = 72 }, 1, 8, 1, 0, 3, 1); tracker.Reset();
            tracker.Suppressed(Scene, 1, 8, 1, 0, 3, 2);
            Require(c.Count(FeatureTriggerKind.AntiDisplacement) == 3);
        });
        Check("forced-many-permission-calls-one-episode", () =>
        {
            using var c = new FeatureTriggerCounters(); var t = new ForcedMovementTriggerTracker(c);
            for (int i = 0; i < 100; i++)
            {
                foreach (uint id in new uint[] { 96, 97, 98, 99, 1001, 1006, 1007, 1008 })
                    t.Permission(Scene, new(i * .01f, 0, 0), id, 0, 1, i * 10);
                t.Position(Scene, new(i * .01f, 0, 0), i * 10 + 1);
            }
            Require(c.Count(FeatureTriggerKind.ForcedMovement) == 1);
            foreach (uint id in new uint[] { 96, 97, 98, 99, 1001, 1006, 1007, 1008 }) t.Permission(Scene, new(1, 0, 0), id, 1, 1, 2000);
            t.Position(Scene, new(1, 0, 0), 2600);
            t.Permission(Scene, new(1, 0, 0), 96, 0, 1, 2700); t.Position(Scene, new(1.2f, 0, 0), 2750);
            Require(c.Count(FeatureTriggerKind.ForcedMovement) == 2);
        });
        Check("forced-no-movement-or-normal-permission-not-counted", () =>
        {
            using var c = new FeatureTriggerCounters(); var t = new ForcedMovementTriggerTracker(c);
            t.Permission(Scene, Vector3.Zero, 96, 1, 1, 0); t.Position(Scene, Vector3.UnitX, 1);
            t.Permission(Scene, Vector3.Zero, 96, 0, 0, 10); t.Position(Scene, Vector3.UnitX, 11);
            t.Permission(Scene, Vector3.Zero, 96, 0, 1, 20); t.Position(Scene, new(.01f, 0, 0), 21);
            Require(c.Count(FeatureTriggerKind.ForcedMovement) == 0);
        });
        Check("forced-pauses-and-repeated-denials-not-new-episodes", () =>
        {
            using var c = new FeatureTriggerCounters(); var t = new ForcedMovementTriggerTracker(c);
            t.Permission(Scene, Vector3.Zero, 96, 0, 1, 0); t.Position(Scene, new(.2f, 0, 0), 1);
            t.Position(Scene, new(.2f, 0, 0), 10000);
            t.Permission(Scene, new(.2f, 0, 0), 96, 0, 1, 10001); t.Position(Scene, new(.4f, 0, 0), 10002);
            Require(c.Count(FeatureTriggerKind.ForcedMovement) == 1);
        });
        Check("forced-scope-teleport-and-reset-do-not-count-old-evidence", () =>
        {
            using var c = new FeatureTriggerCounters(); var t = new ForcedMovementTriggerTracker(c);
            t.Permission(Scene, Vector3.Zero, 96, 0, 1, 0); t.Position(Scene with { Floor = 72 }, Vector3.UnitX, 1);
            t.Permission(Scene, Vector3.Zero, 96, 0, 1, 2); t.Position(Scene, new(10, 0, 0), 3);
            t.Permission(Scene, Vector3.Zero, 96, 0, 1, 4); t.Reset(); t.Position(Scene, Vector3.UnitX, 5);
            Require(c.Count(FeatureTriggerKind.ForcedMovement) == 0);
        });
        Check("gcd-request-without-receipt-never-counted", () =>
        {
            using var c = new FeatureTriggerCounters(); var g = new GcdTriggerConfirmation(c);
            Require(g.ArmAccepted(0, true, false, 10, 11, 7, 2866, 8));
            Require(c.Count(FeatureTriggerKind.GcdProtection) == 0);
            Require(g.Observe(100, 7, 2866, 11, 8));
            Require(!g.Observe(101, 7, 2866, 11, 8) && c.Count(FeatureTriggerKind.GcdProtection) == 1);
        });
        foreach (var (accepted, queued, before, after) in new[] { (false, false, (ushort)1, (ushort)2), (true, true, (ushort)1, (ushort)2), (true, false, (ushort)1, (ushort)1) })
            Check("gcd-rejected-queued-or-unchanged-sequence-" + passed, () =>
            {
                using var c = new FeatureTriggerCounters(); var g = new GcdTriggerConfirmation(c);
                Require(!g.ArmAccepted(0, accepted, queued, before, after, 7, 2866, 8));
                Require(!g.Observe(1, 7, 2866, after, 8) && c.Count(FeatureTriggerKind.GcdProtection) == 0);
            });
        Check("gcd-other-sources-skills-targets-sequences-and-types-excluded", () =>
        {
            using var c = new FeatureTriggerCounters(); var g = new GcdTriggerConfirmation(c); g.Arm(0, 7, 2866, 8, 11);
            Require(!g.Observe(1, 9, 2866, 11, 8) && !g.Observe(2, 7, 100, 11, 8));
            Require(!g.Observe(3, 7, 2866, 12, 8) && !g.Observe(4, 7, 2866, 11, 10));
            Require(!g.Observe(5, 7, 2866, 11, 8, false) && g.Observe(6, 7, 2866, 11, 8));
            Require(c.Count(FeatureTriggerKind.GcdProtection) == 1);
        });
        Check("gcd-expired-cancelled-and-sequence-wrap", () =>
        {
            using var c = new FeatureTriggerCounters(); var g = new GcdTriggerConfirmation(c);
            g.Arm(0, 7, 2866, 8, 11); Require(!g.Observe(30001, 7, 2866, 11, 8));
            g.Arm(40000, 7, 2866, 8, 12); g.Reset(); Require(!g.Observe(40001, 7, 2866, 12, 8));
            Require(g.ArmAccepted(50000, true, false, ushort.MaxValue, 0, 7, 2866, 8));
            Require(g.Observe(50001, 7, 2866, 0, 8) && c.Count(FeatureTriggerKind.GcdProtection) == 1);
        });
        return passed;
    }
    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Persistent trigger counter regression."); }
    private static void Write(string path, FeatureTriggerCountData data)
    { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, JsonSerializer.Serialize(data)); }
    private static void WithFile(Action<string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "nf3-counter-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { test(Path.Combine(root, "LocalExtensions", "trigger-counts.json")); }
        finally { Directory.Delete(root, recursive: true); } // Exclusively this fixture's newly created directory.
    }
}
