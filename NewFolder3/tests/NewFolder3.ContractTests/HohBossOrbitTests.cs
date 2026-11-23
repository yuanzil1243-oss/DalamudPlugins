using System.Numerics;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class HohBossOrbitTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HohBossOrbitFrame Frame = new(
        new(true, true, true, 2, 10, DeepDungeonFloorKind.Boss, false, 1),
        true, true, true, true, 123, new(5, 0, 0), Vector3.Zero, 2, .5f);

    internal static int Run()
    {
        (string Name, Action Test)[] cases = [
            ("default-command-needs-no-coordinates", () => Check(HohBossOrbitCommand.Parse([]) is { Radius: null, Clockwise: true }, "default")),
            ("command-supports-radius-and-direction", () => Check(HohBossOrbitCommand.Parse(["6.5", "ccw"]) is { Radius: 6.5f, Clockwise: false }, "options")),
            ("direction-only-keeps-auto-radius", () => Check(HohBossOrbitCommand.Parse(["ccw"]).Radius == null, "auto")),
            ("stop-and-status-parse", () => Check(HohBossOrbitCommand.Parse(["OFF"]).Stop && HohBossOrbitCommand.Parse(["status"]).Status, "lifecycle")),
            ("rejects-coordinate-like-and-nonfinite-input", () => {
                foreach (var args in new string[][] { ["1", "2", "3"], ["NaN"], ["Infinity"], ["0"], ["21"], ["cw", "ccw"], ["off", "5"] })
                    Throws(() => HohBossOrbitCommand.Parse(args)); }),
            ("default-is-off-and-does-not-touch-movement", () => { using var h = new Rig(); h.Tick(); Check(!h.C.Enabled && h.Acquires == 0 && h.Moves == 0, "opt in"); }),
            ("pt-and-other-dungeons-cannot-enable", () => {
                foreach (uint id in new uint[] { 1, 3, 4 }) { using var h = new Rig(); Check(!h.Start(Frame with { State = Frame.State with { DungeonId = id } }), "HoH scope"); } }),
            ("normal-and-result-floors-reject", () => {
                foreach (var k in new[] { DeepDungeonFloorKind.Mob, DeepDungeonFloorKind.Result, DeepDungeonFloorKind.Unknown })
                { using var h = new Rig(); Check(!h.Start(Frame with { State = Frame.State with { FloorKind = k } }), "boss only"); } }),
            ("rejects-inactive-prepull-dead-and-custom-movement", () => {
                foreach (var f in new[] { Frame with { RunActive = false }, Frame with { InCombat = false }, Frame with { PlayerReady = false }, Frame with { BmrConfigured = false }, Frame with { TargetId = 0 } })
                { using var h = new Rig(); Check(!h.Start(f), "valid context required"); } }),
            ("supports-hoh-boss-floors-ten-to-ninety", () => {
                for (byte f = 10; f <= 90; f += 10) { using var h = new Rig(); Check(h.Start(Frame with { State = Frame.State with { Floor = f } }), "all boss floors"); } }),
            ("invalid-floor-and-transition-reject", () => {
                foreach (byte f in new byte[] { 0, 11, 100 }) { using var h = new Rig(); Check(!h.Start(Frame with { State = Frame.State with { Floor = f } }), "valid floors"); }
                using var a = new Rig(); Check(!a.Start(Frame with { State = Frame.State with { IsTransitioning = true } }), "transition"); }),
            ("auto-radius-uses-hitbox", () => { using var h = new Rig(); Check(h.Start(radius: null) && h.C.Radius == 4, "hitbox + player + margin"); }),
            ("rejects-radius-through-boss", () => { using var h = new Rig(); Check(!h.Start(radius: 2.5f), "outside collision circle"); }),
            ("rejects-distant-player-and-invalid-position", () => {
                using var h = new Rig(); Check(!h.Start(Frame with { Player = new(30, 0, 0) }), "no cross-arena move");
                Check(!h.Start(Frame with { Center = new(float.NaN, 0, 0) }), "finite"); }),
            ("small-arc-clockwise-stays-on-radius", () => { using var h = new Rig(); h.Start(); h.Tick(); Check(h.Moves == 1 && h.Destination.Z > 0 && Math.Abs(h.Destination.Length() - 5) < .001f && Vector3.Distance(Frame.Player, h.Destination) <= 1.01f, "small arc"); }),
            ("small-arc-counterclockwise", () => { using var h = new Rig(); h.Start(clockwise: false); h.Tick(); Check(h.Destination.Z < 0, "reverse"); }),
            ("tracks-boss-center-without-coordinates", () => {
                using var h = new Rig(); h.Start(); h.Tick(); var c = new Vector3(.5f, 0, .2f);
                h.Tick(.2, Frame with { Center = c }); Check(Math.Abs(Vector3.Distance(h.Destination, c) - 5) < .001f, "dynamic center"); }),
            ("two-hundred-millisecond-cadence", () => {
                using var h = new Rig(); h.Start(); h.Tick(); h.Tick(.05); h.Tick(.199); Check(h.Moves == 1, "throttle"); h.Tick(.2); Check(h.Moves == 2, "200ms"); }),
            ("failed-movement-handoff-never-writes-input", () => {
                using var h = new Rig { OwnershipAccepted = false }; h.Start(); h.Tick(); Check(h.Moves == 0 && h.Acquires == 1 && h.Releases == 0, "handoff"); }),
            ("safety-unavailable-or-dangerous-keeps-bmr-ownership", () => {
                using var h = new Rig { Safe = false }; h.Start(); h.Tick(); Check(h.Moves == 0 && h.Acquires == 0 && h.C.Enabled, "yield"); }),
            ("new-danger-releases-and-later-resumes", () => {
                using var h = new Rig(); h.Start(); h.Tick(); h.Safe = false; h.Tick(.2);
                Check(h.Stops == 1 && h.Releases == 1 && h.C.Enabled, "yield active movement");
                h.Safe = true; h.Tick(5); Check(h.Moves == 2 && h.Acquires == 2 && h.C.Enabled, "pause time not counted as stuck"); }),
            ("ground-probes-reject-holes", () => {
                using var h = new Rig(); h.Ground = p => p.Z > .2f && p.Z < .4f ? null : p;
                h.Start(); h.Tick(); Check(h.Moves == 0 && h.Acquires == 0, "sample interior"); }),
            ("ground-probes-require-edge-margin", () => {
                using var h = new Rig(); h.Ground = p => p.X > 5.2f ? null : p;
                h.Start(); h.Tick(); Check(h.Moves == 0, "walkable margin"); }),
            ("ground-cannot-snap-to-other-floor-or-island", () => {
                foreach (var v in new[] { new Vector3(0, 2, 0), new Vector3(2, 0, 0), new Vector3(float.NaN, 0, 0) })
                { using var h = new Rig(); h.Ground = p => p + v; h.Start(); h.Tick(); Check(h.Moves == 0, "no geometry snapping"); } }),
            ("approaches-requested-radius-in-small-steps", () => {
                using var h = new Rig(); h.Start(Frame with { Player = new(8, 0, 0) }); h.Tick(frame: Frame with { Player = new(8, 0, 0) });
                Check(h.Destination.Z == 0 && Math.Abs(h.Destination.X - 7.2f) < .001f, "bounded radial approach"); }),
            ("switching-target-stops-even-inside-update-throttle", () => {
                using var h = new Rig(); h.Start(); h.Tick(); h.Tick(.01, Frame with { TargetId = 456 }); Check(!h.C.Enabled && h.Stops == 1 && h.Releases == 1, "never orbit adds"); }),
            ("stops-on-fsd-stop-death-combat-end-and-config-change", () => {
                foreach (var f in new[] { Frame with { RunActive = false }, Frame with { PlayerReady = false }, Frame with { InCombat = false }, Frame with { BmrConfigured = false }, Frame with { TargetId = 0 } })
                { using var h = new Rig(); h.Start(); h.Tick(); h.Tick(.1, f); Check(!h.C.Enabled && h.Releases == 1, "terminal state"); } }),
            ("stops-on-floor-or-dungeon-change", () => {
                foreach (var s in new[] { Frame.State with { Floor = 20 }, Frame.State with { DungeonId = 4 }, Frame.State with { IsTransitioning = true }, Frame.State with { IsInDuty = false } })
                { using var h = new Rig(); h.Start(); h.Tick(); h.Tick(.1, Frame with { State = s }); Check(!h.C.Enabled && h.Releases == 1, "floor lifecycle"); } }),
            ("boss-teleport-stops-experiment", () => {
                using var h = new Rig(); h.Start(); h.Tick(); h.Tick(.2, Frame with { Center = new(5, 0, 0) }); Check(!h.C.Enabled && h.Releases == 1, "teleport"); }),
            ("no-progress-stops-after-three-seconds", () => {
                using var h = new Rig(); h.Start(); h.Tick(); h.Tick(3.01); Check(!h.C.Enabled && h.Stops == 1 && h.Releases == 1, "bounded stuck"); }),
            ("actual-progress-renews-watchdog", () => {
                using var h = new Rig(); h.Start(); h.Tick(); h.Tick(2, Frame with { Player = new(4.9f, 0, .8f) }); h.Tick(4, Frame with { Player = new(4.5f, 0, 1.4f) }); Check(h.C.Enabled, "movement progress"); }),
            ("exception-releases-input-and-bmr", () => {
                using var h = new Rig(); h.Start(); h.Tick(); h.ThrowSafe = true; h.Tick(.2); Check(!h.C.Enabled && h.Releases == 1 && h.Stops == 1, "ipc loss"); }),
            ("manual-stop-and-dispose-release-once", () => {
                using var h = new Rig(); h.Start(); h.Tick(); h.C.Stop(); h.C.Stop(); h.C.Dispose(); Check(h.Releases == 1 && h.Stops == 1 && !h.C.Enabled, "idempotent cleanup"); })
        ];
        foreach (var (name, test) in cases) { test(); Console.WriteLine("  ok  hoh-orbit-" + name); }
        return cases.Length;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Throws(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("invalid input accepted"); }
    private sealed class Rig : IDisposable
    {
        public bool Safe = true, OwnershipAccepted = true, ThrowSafe;
        public int Acquires, Releases, Stops, Moves;
        public Vector3 Destination;
        public Func<Vector3, Vector3?> Ground = p => p;
        public HohBossOrbitController C;
        public Rig() => C = new(active => { if (active) Acquires++; else Releases++; return OwnershipAccepted; },
            p => { Destination = p; Moves++; }, () => Stops++, p => Ground(p),
            (_, _) => ThrowSafe ? throw new InvalidOperationException("IPC unavailable") : Safe, _ => { });
        public bool Start(HohBossOrbitFrame? frame = null, float? radius = 5, bool clockwise = true) => C.Start(frame ?? Frame, radius, clockwise, Now, out _);
        public void Tick(double seconds = 0, HohBossOrbitFrame? frame = null) => C.Update(frame ?? Frame, Now.AddSeconds(seconds));
        public void Dispose() => C.Dispose();
    }
}
