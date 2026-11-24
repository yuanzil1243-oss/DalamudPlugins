using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class RotationDiagnosticTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        { test(); count++; Console.WriteLine("  ok  rotation-diagnostics-" + name); }

        Check("ae-never-reads-loader-manifest-or-version", () =>
        {
            int writes = 0, warnings = 0;
            bool read = false;
            (string? Name, string? Version) actual = ("sentinel", "sentinel");
            RotationControlDiagnostics.Record(FsdRotationProvider.AEAssist,
                () => { read = true; throw new Exception("AE loader metadata must not be read"); },
                (name, version) => { actual = (name, version); writes++; }, _ => warnings++);
            Assert(!read && writes == 1 && warnings == 0 && actual == (null, null));
        });
        Check("other-provider-without-version-records-null", () =>
        {
            (string? Name, string? Version) actual = default;
            int writes = 0, warnings = 0;
            RotationControlDiagnostics.Record(FsdRotationProvider.RotationSolverReborn,
                () => ("RotationSolverReborn", null),
                (name, version) => { actual = (name, version); writes++; }, _ => warnings++);
            Assert(writes == 1 && warnings == 0 && actual == ("RotationSolverReborn", null));
        });
        Check("normal-version-retained", () =>
        {
            (string? Name, string? Version) actual = default;
            int writes = 0, warnings = 0;
            RotationControlDiagnostics.Record(FsdRotationProvider.RotationSolverReborn,
                () => ("RotationSolverReborn", new Version(1, 2, 3, 4)),
                (name, version) => { actual = (name, version); writes++; }, _ => warnings++);
            Assert(writes == 1 && warnings == 0 && actual == ("RotationSolverReborn", "1.2.3.4"));
        });
        Check("custom-without-plugin-records-null", () =>
        {
            (string? Name, string? Version) actual = ("sentinel", "sentinel");
            int writes = 0, warnings = 0;
            RotationControlDiagnostics.Record(FsdRotationProvider.Custom,
                () => throw new Exception("Custom must not read plugin metadata"),
                (name, version) => { actual = (name, version); writes++; }, _ => warnings++);
            Assert(writes == 1 && warnings == 0 && actual == (null, null));
        });
        Check("plugin-enumeration-failure-reported-without-replay", () =>
        {
            var expected = new InvalidOperationException("plugin unloaded while reading metadata");
            int warnings = 0;
            RotationControlDiagnostics.Record(FsdRotationProvider.RotationSolverReborn, () => throw expected,
                (_, _) => throw new Exception("Unexpected replay"), error =>
                { Assert(ReferenceEquals(expected, error)); warnings++; });
            Assert(warnings == 1);
        });
        Check("replay-failure-reported-with-original-stack", () =>
        {
            var expected = new IOException("replay sink unavailable");
            int warnings = 0;
            RotationControlDiagnostics.Record(FsdRotationProvider.AEAssist, () => ("AEAssistV3", null), (_, _) => throw expected,
                error => { Assert(ReferenceEquals(expected, error) && error.StackTrace != null); warnings++; });
            Assert(warnings == 1);
        });
        Check("warning-sink-failure-does-not-escape", () =>
            RotationControlDiagnostics.Record(FsdRotationProvider.RotationSolverReborn, () => throw new IOException("metadata"),
                (_, _) => { }, _ => throw new IOException("warning sink")));

        foreach (var provider in new[] { FsdRotationProvider.AEAssist, FsdRotationProvider.Custom })
        foreach (bool accepted in new[] { true, false })
        foreach (bool replayThrows in new[] { false, true })
        {
            Check($"boundaries-{provider}-accepted-{accepted}-replay-fails-{replayThrows}", () =>
            {
                var settings = new FsdRotationSettings { Provider = provider,
                    CustomEnableCommand = "/aestop off", CustomDisableCommand = "/aestop on" };
                var state = new DeepDungeonStateSnapshot(true, true, true, 1, 21, DeepDungeonFloorKind.Mob, false, 1);
                var requests = new List<(FsdRotationProvider Provider, bool Enabled, string Command)>();
                var reasons = new List<string>();
                var metadata = new List<(string? Name, string? Version)>();
                int warnings = 0;
                var controller = new FsdRotationController(_ => FsdRotationState.On,
                    (p, enabled, command) => { requests.Add((p, enabled, command)); return accepted; }, control =>
                    {
                        reasons.Add(control.Reason);
                        RotationControlDiagnostics.Record(provider,
                            () => throw new Exception("AE and Custom must not enumerate installed plugins"),
                            (name, version) =>
                            {
                                metadata.Add((name, version));
                                if (replayThrows) throw new IOException("replay unavailable");
                            }, _ => warnings++);
                    });
                void Update(bool combat = false, long preparation = 0, bool hold = false, bool active = true) =>
                    controller.Update(active, FarmingMode.DeepProgression, state, combat, settings, hold, preparation);
                void AssertResult(bool desired) => Assert(controller.DesiredEnabled == desired &&
                    controller.FailedProvider == (accepted ? null : provider));

                Update(); AssertResult(true);
                Update(); Assert(requests.Count == 1);
                Update(preparation: 1); AssertResult(true);
                Update(combat: true, preparation: 1); AssertResult(true);
                Update(combat: true, preparation: 1); Assert(requests.Count == 3);
                Update(combat: true, preparation: 1, hold: true); AssertResult(false);
                Update(combat: true, preparation: 1); AssertResult(true);
                Assert(requests.Count == 5 && requests.All(r => r.Provider == provider));
                Assert(requests.Take(3).All(r => r.Enabled && r.Command == "/aestop off"));
                Assert(!requests[3].Enabled && requests[3].Command == "/aestop on" && requests[4].Enabled);
                Assert(reasons.SequenceEqual(new[] { "duty-entry", "combat-preparation", "combat-started",
                    "output-policy-changed", "output-policy-changed" }));
                Assert(warnings == (replayThrows ? 5 : 0));
                Assert(metadata.Count == 5 && metadata.All(m => m == (null, null)));
                Update(active: false);
                Assert(!controller.DesiredEnabled && controller.FailedProvider == null && requests.Count == 5);
            });
        }
        Check("real-output-control-exception-still-propagates", () =>
        {
            var expected = new InvalidOperationException("actual output control failed");
            bool recorded = false, observed = false;
            var controller = new FsdRotationController(_ => FsdRotationState.On,
                (_, _, _) => throw expected, _ => recorded = true);
            try { controller.Update(true, FarmingMode.DeepProgression,
                new(true, true, true, 1, 21, DeepDungeonFloorKind.Mob, false, 1), false,
                new() { Provider = FsdRotationProvider.AEAssist }); }
            catch (InvalidOperationException error) { observed = ReferenceEquals(error, expected); }
            Assert(observed && !recorded);
        });
        Check("real-state-read-exception-still-propagates", () =>
        {
            var expected = new InvalidOperationException("actual read failed");
            bool controlled = false, observed = false;
            var controller = new FsdRotationController(_ => throw expected,
                (_, _, _) => { controlled = true; return true; });
            try { controller.Update(true, FarmingMode.DeepProgression,
                new(true, true, true, 1, 21, DeepDungeonFloorKind.Mob, false, 1), false,
                new() { Provider = FsdRotationProvider.AEAssist }); }
            catch (InvalidOperationException error) { observed = ReferenceEquals(error, expected); }
            Assert(observed && !controlled);
        });
        return count;
    }

    private static void Assert(bool value)
    { if (!value) throw new InvalidOperationException("Rotation diagnostic regression failed."); }
}
