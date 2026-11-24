using System.Numerics;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class HohGazeMovementTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Vector3 Player = new(5, 0, 0);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh-gaze-movement-default-does-not-acquire", () => { using var r = new Rig(); r.Tick(false); Check(r.Acquires == 0 && r.Moves.Count == 0, "inactive"); }),
            ("hoh-gaze-movement-acquires-before-short-step", () => { using var r = new Rig(); r.Tick(); Check(r.Events[0] == "acquire" && r.Moves.Single() == new Vector3(6, 0, 0), "one meter away"); }),
            ("hoh-gaze-movement-faces-away-from-boss", () => { using var r = new Rig(); r.Tick(); Check(r.Faces.Single() == new Vector3(15, 0, 0), "away facing"); }),
            ("hoh-gaze-movement-denied-handoff-never-writes", () => { using var r = new Rig { Accept = false }; r.Tick(); Check(!r.C.OwnsMovement && r.Moves.Count == 0 && r.Faces.Count == 0, "BMR retains ownership"); }),
            ("hoh-gaze-movement-stops-after-step-keeps-facing", () => { using var r = new Rig(); r.Tick(); r.Tick(p: new(5.9f, 0, 0)); Check(r.Moves.Count == 1 && r.Stops == 1 && r.Faces.Count == 2 && r.C.OwnsMovement, "hold until gate ends"); }),
            ("hoh-gaze-movement-overshoot-does-not-turn-toward-boss", () => { using var r = new Rig(); r.Tick(); r.Tick(p: new(6.5f, 0, 0)); Check(r.Moves.Count == 1 && r.Faces.Last().X > 6.5f, "no reverse correction"); }),
            ("hoh-gaze-movement-no-progress-is-bounded", () => { using var r = new Rig(); r.Tick(); r.Tick(seconds: .6); Check(r.Moves.Count == 1 && r.Stops == 1 && r.C.OwnsMovement, "stop walking but hold facing"); }),
            ("hoh-gaze-movement-no-ground-faces-in-place", () => { using var r = new Rig { Ground = _ => null }; r.Tick(); Check(r.Moves.Count == 0 && r.Faces.Count == 1 && r.C.OwnsMovement, "no edge step"); }),
            ("hoh-gaze-movement-bmr-unsafe-path-faces-in-place", () => { using var r = new Rig { Safe = false }; r.Tick(); Check(r.Moves.Count == 0 && r.Faces.Count == 1, "hazard fallback"); }),
            ("hoh-gaze-movement-gap-in-segment-rejects-step", () => { using var r = new Rig { Ground = p => p.X is > 5.4f and < 5.6f ? null : p }; r.Tick(); Check(r.Moves.Count == 0 && r.Faces.Count == 1, "whole short segment"); }),
            ("hoh-gaze-movement-height-change-rejects-step", () => { using var r = new Rig { Ground = p => p + new Vector3(0, 1, 0) }; r.Tick(); Check(r.Moves.Count == 0, "same floor only"); }),
            ("hoh-gaze-movement-gate-open-releases-once", () => { using var r = new Rig(); r.Tick(); r.Tick(false); r.Tick(false); Check(r.Releases == 1 && r.Stops == 1 && !r.C.OwnsMovement, "release before output resumes"); }),
            ("hoh-gaze-movement-next-cast-plans-new-step", () => { using var r = new Rig(); r.Tick(); r.Tick(false); r.Tick(p: new(8, 0, 0)); Check(r.Acquires == 2 && r.Moves.Last() == new Vector3(9, 0, 0), "new cast position"); }),
            ("hoh-gaze-movement-overlapping-center-does-not-guess", () => { using var r = new Rig(); r.Tick(p: Vector3.Zero); Check(r.Acquires == 0 && r.Faces.Count == 0, "undefined facing left to BMR"); }),
            ("hoh-gaze-movement-invalid-position-releases", () => { using var r = new Rig(); r.Tick(); r.Tick(p: new(float.NaN, 0, 0)); Check(!r.C.OwnsMovement && r.Releases == 1, "invalid lifecycle"); }),
            ("hoh-gaze-movement-dispose-releases", () => { var r = new Rig(); r.Tick(); r.Dispose(); Check(r.Releases == 1 && r.Stops == 1, "unload lifecycle"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private sealed class Rig : IDisposable
    {
        public bool Accept = true, Safe = true;
        public int Acquires, Releases, Stops;
        public Func<Vector3, Vector3?> Ground = p => p;
        public readonly List<Vector3> Moves = [], Faces = [];
        public readonly List<string> Events = [];
        public readonly HohGazeMovementController C;
        public Rig() => C = new(on => { Events.Add(on ? "acquire" : "release"); if(on) Acquires++; else Releases++; return !on || Accept; },
            p => { Events.Add("move"); Moves.Add(p); }, () => Stops++, p => Faces.Add(p), p => Ground(p), (_, _) => Safe, _ => { });
        public void Tick(bool active = true, double seconds = 0, Vector3? p = null) => C.Update(active, p ?? Player, Vector3.Zero, Now.AddSeconds(seconds));
        public void Dispose() => C.Dispose();
    }
}
