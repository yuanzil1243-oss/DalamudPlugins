using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Combat;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace NewFolder3;

internal static class Run41InitialColorTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 7, 54, 40, DateTimeKind.Utc);
    private static readonly Vector3 Entry = new(-599.948f, -0.00000143051f, -289.113f);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("pt99-opening-point-is-the-entrance-light-floor-center", () => {
                var center = Pt99Arena.InitialLightPoolCenter;
                Check(Vector2.Distance(center, new(-604.4116f, -287.5067f)) < 0.001f, "fixed reference polygon centroid");
                Check(Pt99ColorFloor.Contains(Pt99Color.Light, center, 0.8f), "well inside actual light floor");
                Check(!Pt99ColorFloor.Contains(Pt99Color.Dark, center, 0), "not a dark pool"); }),
            ("pt99-opening-replay-entry-uses-world-coordinate-navigation", () => {
                using var f = new Fixture(); f.Controller.UpdateInitialColor(f.Context, Entry, Now);
                Check(f.Navigator.Starts == 1 && f.Navigator.Goal == Goal(Entry.Y), "navigate directly to fixed center");
                Check(f.Context.Pt99InitialColorMovementActive && f.Handoffs.SequenceEqual(new[] { true }), "opening owns movement");
                Check(Field(f.Controller, "_movement") == null, "opening never creates the raw directional-input controller"); }),
            ("pt99-opening-arrival-side-never-selects-another-pool", () => {
                foreach (var p in new[] { Entry, new Vector3(-600, 0, -300), new Vector3(-608, 0, -290) })
                {
                    using var f = new Fixture(); f.Controller.UpdateInitialColor(f.Context, p, Now);
                    Check(f.Navigator.Goal == Goal(p.Y), "same fixed floor center regardless of entry position");
                } }),
            ("pt99-opening-does-not-reissue-a-running-route", () => {
                using var f = new Fixture(); f.Controller.UpdateInitialColor(f.Context, Entry, Now);
                f.Controller.UpdateInitialColor(f.Context, Entry + new Vector3(-0.1f, 0, 0.1f), Now.AddSeconds(1));
                Check(f.Navigator.Starts == 1 && f.Handoffs.Count == 1, "one navigation request while moving"); }),
            ("pt99-opening-arrival-waits-for-real-buff", () => {
                using var f = new Fixture(); f.Controller.UpdateInitialColor(f.Context, Entry, Now);
                f.Controller.UpdateInitialColor(f.Context, Goal(Entry.Y), Now.AddSeconds(1));
                Check(!f.Navigator.Running && f.Context.Pt99InitialColorMovementActive, "stop at center without claiming color");
                Check(f.Controller.Status.Contains("等待实际Buff") && !f.Handoffs.Contains(false), "arrival alone cannot release battle"); }),
            ("pt99-opening-reset-cancels-navigation-and-hands-back", () => {
                using var f = new Fixture(); f.Controller.UpdateInitialColor(f.Context, Entry, Now);
                f.Controller.Reset(f.Context);
                Check(!f.Navigator.Running && !f.Context.Pt99InitialColorMovementActive, "opening fully released");
                Check(f.Handoffs.SequenceEqual(new[] { true, false }) && f.Controller.Status == "inactive", "combat ownership restored"); }),
            ("pt99-opening-refused-handoff-never-starts-navigation", () => {
                using var f = new Fixture(); f.AcceptHandoff = false;
                f.Controller.UpdateInitialColor(f.Context, Entry, Now);
                Check(f.Navigator.Starts == 0 && !f.Context.Pt99InitialColorMovementActive, "no movement without confirmed handoff"); }),
            ("pt99-opening-navigation-failure-does-not-fall-back-to-directional-input", () => {
                using var f = new Fixture(); f.Navigator.AcceptStart = false;
                f.Controller.UpdateInitialColor(f.Context, Entry, Now);
                Check(!f.Context.Pt99InitialColorMovementActive && f.Handoffs.Last() == false, "failure releases ownership");
                Check(Field(f.Controller, "_movement") == null && f.Controller.Status.Contains("导航未完成"), "no raw-input fallback"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private static Vector3 Goal(float y) => new(Pt99Arena.InitialLightPoolCenter.X, y, Pt99Arena.InitialLightPoolCenter.Y);
    private sealed class TestNavigator : INavigator
    {
        internal int Starts;
        internal bool Running, AcceptStart = true;
        internal Vector3 Goal;
        public bool PathfindAndMoveTo(Vector3 goal, bool fly) { Starts++; Goal = goal; Running = AcceptStart; return AcceptStart; }
        public bool RepathTo(Vector3 goal, bool fly) => PathfindAndMoveTo(goal, fly);
        public void CancelAll() => Running = false;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        internal readonly Pt99ColorMovementController Controller = new();
        internal readonly TestNavigator Navigator = new();
        internal readonly List<bool> Handoffs = new();
        internal bool AcceptHandoff = true;
        internal Fixture()
        {
            Context.SetBossMovementOverride = active => { Handoffs.Add(active); return !active || AcceptHandoff; };
            var navigation = new NavigationHelper(Navigator, () => Navigator.Running, () => Now, _ => { }, _ => { }, () => false);
            typeof(Pt99ColorMovementController).GetField("_initialNavigation", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Controller, navigation);
        }
        public void Dispose() { Controller.Reset(Context); Context.Dispose(); }
    }
}
