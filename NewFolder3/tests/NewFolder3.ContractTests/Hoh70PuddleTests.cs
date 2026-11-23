using System.Numerics;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class Hoh70PuddleTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 2, 19, 23, DateTimeKind.Utc);
    private static readonly Hoh70Puddle Pool = new(456, new(-295.895f, 0, -280.6543f));
    private static Hoh70Frame Frame => new(true, 2, 70, true, true, 123, 9195, true,
        Pool.Position + new Vector3(6, 0, 0), 0, true, false, [Pool]);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh70-replay-first-ululation-must-not-enter", () => {
                using var r = new Rig(); r.Tick();
                r.Tick(Frame with { CastActionId = 12208 }, 3);
                r.Tick(Frame, 6.1); r.Tick(Frame, 8.8); r.Tick(Frame, 11.8);
                Check(r.C.Stage == "wait-outside-for-charge" && r.Moves.All(p => Vector3.Distance(p, Pool.Position) >= 3.7f),
                    "the recorded post-ululation autoattacks at +8/+11 seconds must happen outside"); }),
            ("hoh70-charge-cast-authorizes-entry", () => {
                using var r = new Rig(); r.Tick(); r.Tick(Frame with { CastActionId = 12206 }, 12.2);
                Check(r.Moves.Last() == Pool.Position && r.C.Stage == "enter-on-charge-cast", "enter during cast, not preceding icon"); }),
            ("hoh70-other-player-charge-stays-outside", () => {
                using var r = new Rig(); r.Tick(Frame with { CastActionId = 12206, ChargeTargetsPlayer = false });
                Check(r.C.Stage == "wait-outside-for-charge", "only the marked player needs mini"); }),
            ("hoh70-mini-before-charge-recovers-outside", () => {
                using var r = new Rig(); r.Tick(Frame with { Player = Pool.Position, Mini = true });
                Check(Vector3.Distance(r.Moves.Last(), Pool.Position) >= 3.7f, "escape premature mini"); }),
            ("hoh70-mini-and-charge-end-do-not-release", () => {
                using var r = new Rig(); r.Enter();
                r.Tick(Frame with { Player = Pool.Position, Mini = true }, 18);
                Check(r.C.OwnsMovement && r.C.Stage == "hold-mini-until-devour" && r.Releases == 0, "wait through stun and instant Devour"); }),
            ("hoh70-devour-event-then-exit-and-release", () => {
                using var r = new Rig(); r.Enter(); r.C.ObserveAction(123, 12204);
                r.Tick(Frame with { Player = Pool.Position, Mini = true }, 20);
                var destination = r.Moves.Last();
                Check(Vector3.Distance(destination, Pool.Position) >= 3.7f && r.C.OwnsMovement, "leave after observed resolution");
                r.Tick(Frame with { Player = destination }, 21);
                Check(!r.C.OwnsMovement && r.Releases == 1, "return BMR after outside and mini cleared");
                r.Tick(Frame with { Player = destination }, 22);
                Check(r.Acquires == 1, "do not reacquire the old puddle after Devour"); }),
            ("hoh70-other-boss-event-cannot-release-mini", () => {
                using var r = new Rig(); r.Enter(); r.C.ObserveAction(999, 12204);
                r.Tick(Frame with { Player = Pool.Position, Mini = true }, 18);
                Check(r.C.Stage == "hold-mini-until-devour", "correct boss instance"); }),
            ("hoh70-charge-resolution-is-not-devour", () => {
                using var r = new Rig(); r.Enter(); r.C.ObserveAction(123, 12206);
                r.Tick(Frame with { Player = Pool.Position, Mini = true }, 18);
                Check(r.C.Stage == "hold-mini-until-devour", "do not leave when charge hits"); }),
            ("hoh70-duplicate-devour-cannot-restart-entry", () => {
                using var r = new Rig(); r.Enter(); r.C.ObserveAction(123, 12204); r.C.ObserveAction(123, 12204);
                r.Tick(Frame with { Player = Pool.Position, Mini = true }, 20);
                Check(r.C.Stage.StartsWith("exit-"), "duplicate resolution harmless"); }),
            ("hoh70-next-ululation-is-resolution-fallback", () => {
                using var r = new Rig(); r.Enter();
                r.Tick(Frame with { CastActionId = 12208, Player = Pool.Position, Mini = true }, 22);
                Check(r.C.Stage == "exit-on-next-ululation" && Vector3.Distance(r.Moves.Last(), Pool.Position) >= 3.7f, "positive subsequent mechanic evidence"); }),
            ("hoh70-puddle-temporarily-missing-during-stun-is-retained", () => {
                using var r = new Rig(); r.Enter();
                r.Tick(Frame with { Player = Pool.Position, Mini = true, Puddles = [] }, 18);
                Check(r.C.OwnsMovement && r.C.Stage == "hold-mini-until-devour", "do not abandon protection during stun"); }),
            ("hoh70-no-puddle-does-not-acquire", () => {
                using var r = new Rig(); r.Tick(Frame with { Puddles = [] }); Check(r.Acquires == 0, "BMR handles ordinary attacks"); }),
            ("hoh70-puddle-disappearance-outside-releases", () => {
                using var r = new Rig(); r.Tick(); r.Tick(Frame with { Puddles = [] }, 1);
                Check(!r.C.OwnsMovement && r.Releases == 1, "expired object no longer controls movement"); }),
            ("hoh70-denied-bmr-handoff-never-moves", () => {
                using var r = new Rig { Accept = false }; r.Tick(); Check(!r.C.OwnsMovement && r.Moves.Count == 0, "acquire first"); }),
            ("hoh70-navmesh-edge-selects-another-wait-point", () => {
                using var r = new Rig { Ground = p => p.X > Pool.Position.X + 2 ? null : p };
                r.Tick(Frame with { Player = Pool.Position });
                Check(r.C.OwnsMovement && r.Moves.Last().X <= Pool.Position.X + 2, "do not walk through an arena edge"); }),
            ("hoh70-collision-rejects-movement", () => {
                using var r = new Rig { Clear = false }; r.Tick(); Check(r.Acquires == 0, "no blind wall crossing"); }),
            ("hoh70-ground-gap-rejects-segment", () => {
                using var r = new Rig { Ground = p => Vector3.Distance(p, Pool.Position) is > 1 and < 2 ? null : p };
                r.Tick(Frame with { Player = Pool.Position }); Check(r.Acquires == 0, "sample whole route"); }),
            ("hoh70-wait-route-does-not-cross-puddle", () => {
                using var r = new Rig(); r.Tick(); Check(r.Moves.Last().X > Pool.Position.X, "nearest outside side"); }),
            ("hoh70-pt-and-other-floors-release", () => {
                foreach (var f in new[] { Frame with { DungeonId = 4 }, Frame with { Floor = 71 }, Frame with { BossBaseId = 18546 } }) {
                    using var r = new Rig(); r.Enter(); r.Tick(f, 16); Check(!r.C.OwnsMovement && r.Releases == 1, "never change PT or other floors"); } }),
            ("hoh70-stop-death-transition-release", () => {
                foreach (var f in new[] { Frame with { Enabled = false }, Frame with { PlayerAlive = false }, Frame with { InCombat = false },
                    Frame with { BossAlive = false }, Frame with { BossId = 0 } }) {
                    using var r = new Rig(); r.Enter(); r.Tick(f, 16); Check(!r.C.OwnsMovement && r.Releases == 1, "lifecycle"); } }),
            ("hoh70-new-boss-does-not-inherit-devour", () => {
                using var r = new Rig(); r.Enter(); r.Tick(Frame with { BossId = 789 }, 16);
                Check(r.C.Stage == "wait-outside-for-charge" && r.Releases == 1, "fresh actor identity"); }),
            ("hoh70-next-puddle-starts-new-cycle", () => {
                using var r = new Rig(); r.Enter(); r.C.ObserveAction(123, 12204);
                r.Tick(Frame with { Player = Pool.Position + new Vector3(4.5f, 0, 0) }, 20);
                r.Tick(Frame with { Puddles = [Pool with { Id = 567 }] }, 21);
                Check(r.C.Stage == "wait-outside-for-charge" && r.Acquires == 2, "new round independent"); }),
            ("hoh70-stale-sequence-cannot-latch-ownership", () => {
                using var r = new Rig(); r.Tick(); r.Tick(Frame, 31);
                Check(!r.C.OwnsMovement && r.Releases == 1, "bounded handoff"); }),
            ("hoh70-dispose-releases-input-and-bmr", () => {
                var r = new Rig(); r.Enter(); r.Dispose(); Check(r.Releases == 1 && !r.C.OwnsMovement, "unload cleanup"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private sealed class Rig : IDisposable
    {
        public bool Accept = true, Clear = true;
        public int Acquires, Releases, Stops;
        public Func<Vector3, Vector3?> Ground = p => p;
        public readonly List<Vector3> Moves = [];
        public readonly Hoh70PuddleController C;
        public Rig() => C = new(active => { if (active) Acquires++; else Releases++; return !active || Accept; },
            p => Moves.Add(p), () => Stops++, p => Ground(p), (_, _) => Clear, _ => { });
        public void Tick(Hoh70Frame? frame = null, double seconds = 0) => C.Update(frame ?? Frame, Now.AddSeconds(seconds));
        public void Enter() { Tick(); Tick(Frame with { CastActionId = 12206 }, 12); }
        public void Dispose() => C.Dispose();
    }
}
