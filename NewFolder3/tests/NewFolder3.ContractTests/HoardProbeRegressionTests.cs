using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Coord;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace NewFolder3;

internal static class HoardProbeRegressionTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  hoard-probe-" + name); }
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
            Check("map-selection-dungeon-" + dungeon, () =>
            {
                for (int start = 1; start <= (dungeon == 1 ? 191 : 91); start += 10)
                    foreach (var mode in Enum.GetValues<FarmingMode>())
                        foreach (bool enabled in new[] { false, true })
                        {
                            var p = new FarmingPlan(mode, SaveUse.Create, start, 1, false, enabled, true, true, true) { DungeonId = dungeon };
                            string? expected = dungeon == 4 && enabled && mode == FarmingMode.Hoard
                                ? start == 21 ? "pt-21-30" : start == 31 ? "pt-31-40" : null : null;
                            Require(DetailedMapUsagePolicy.ScenarioKey(p) == expected && p.ShowsDetailedMap == (expected != null));
                        }
                int relicStart = dungeon == 1 ? 51 : 21;
                Require(RelicFarmingPolicy.TryCreate(dungeon, SaveUse.Create, relicStart, 1, false,
                    new(true, true, true, true), out var relic, out _));
                Require(DetailedMapUsagePolicy.ScenarioKey(relic!) == (dungeon == 4 ? "pt-21-30" : null));
                Require(DetailedMapUsagePolicy.ScenarioKey(relic! with { BandedEnabled = false }) == null);
                Require(FreeExplorationPolicy.TryCreate(dungeon, relicStart, relicStart + 9, 1, false,
                    new(true, true, true, true), false, false, out var free, out _));
                Require(DetailedMapUsagePolicy.ScenarioKey(free!) == null);
            });
        foreach (var scope in DetailedMapScenarioCatalog.Definitions)
            Check("runtime-map-scope-" + scope.Key, () =>
            {
                var catalog = Catalog(scope);
                foreach (uint dungeon in new uint[] { 0, 1, 2, 4 })
                    for (byte floor = 1; floor <= 200; floor++)
                        Require(DetailedMapUsagePolicy.Applies(catalog, dungeon, scope.TerritoryId, floor) ==
                            (dungeon == 4 && floor >= scope.FirstCoveredFloor && floor <= scope.LastCoveredFloor));
                Require(!DetailedMapUsagePolicy.Applies(catalog, 4, scope.TerritoryId + 1, scope.FirstCoveredFloor));
                Require(!DetailedMapUsagePolicy.Applies(catalog, 4, 593, scope.FirstCoveredFloor));
                Require(!DetailedMapUsagePolicy.Applies(catalog, 2, scope.TerritoryId, scope.FirstCoveredFloor));
                Require(!DetailedMapUsagePolicy.Applies(null, 4, scope.TerritoryId, scope.FirstCoveredFloor));
                Require(!DetailedMapUsagePolicy.Applies(new() { ScenarioKey = scope.Key, TerritoryId = scope.TerritoryId },
                    4, scope.TerritoryId, scope.FirstCoveredFloor));
                Require(!DetailedMapUsagePolicy.Applies(new() { ScenarioKey = "unknown", TerritoryId = scope.TerritoryId, Floors = scope.Floors },
                    4, scope.TerritoryId, scope.FirstCoveredFloor));
            });
        Check("death-palace-51-repro-falls-back-before-planning", () =>
        {
            var cat = Catalog(DetailedMapScenarioCatalog.PilgrimsTraverse21To30);
            Require(!DetailedMapUsagePolicy.Applies(cat, 1, 593, 51));
            bool rejected = false;
            try { DetailedMapRoomCandidatePlanner.BuildPriorityOrder(cat, new(), 51, []); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            Require(rejected); // Keep the upstream validation; guard its inputs instead.
        });
        Check("two-points-one-stop-and-original-radius", () =>
        {
            var group = Build(P("a", 0), P("b", 3)).Single();
            Require(group.Points.Count == 2 && group.Covers(group.Position));
            Require(group.Covers(new(1.5f, 0, 0)) && !group.Covers(new(1, 0, 0)));
            Require(Math.Abs(group.Position.X - 1.5f) < 0.001f);
            Require(BlindHoardProbeGroup.CoverageRadius == 1.8f);
        });
        Check("disjoint-and-navigation-tolerance-boundary-stay-separate", () =>
        {
            Require(Build(P("a", 0), P("b", 4)).Count == 2);
            Require(Build(P("a", 0), P("b", 3.6f)).Count == 2);
        });
        Check("overlap-chain-is-not-one-stop", () =>
        {
            var groups = Build(P("a", 0), P("b", 3), P("c", 6));
            Require(groups.Count == 2 && groups.Sum(g => g.Points.Count) == 3);
            Require(groups.All(g => g.Covers(g.Position)));
        });
        Check("pairwise-overlap-without-common-intersection-is-not-one-stop", () =>
        {
            var groups = Build(P("a", 0), P("b", 3.2f), P("c", 1.6f, z: 2.7712812f));
            Require(groups.Count == 2 && groups.Sum(g => g.Points.Count) == 3);
        });
        Check("three-point-common-intersection", () =>
        {
            var group = Build(P("a", 0), P("b", 2.8f), P("c", 1.4f, z: 2.424871f)).Single();
            Require(group.Points.Count == 3 && group.Covers(group.Position));
        });
        Check("different-room-height-or-unknown-room-does-not-merge", () =>
        {
            Require(Build(P("a", 0), P("b", 1) with { Room = 3 }).Count == 2);
            Require(Build(P("a", 0), P("b", 1, y: 0.6f)).Count == 2);
            Require(Build(P("a", 0) with { Room = -1 }, P("b", 1) with { Room = -1 }).Count == 2);
            Require(Build(P("a", 0), P("b", float.NaN)).Count == 2);
        });
        Check("blocked-common-region-keeps-original-tasks", () =>
        {
            var groups = BlindHoardProbePlanner.Build(56, [P("a", 0), P("b", 3)], (_, _) => false);
            Require(groups.Count == 2 && groups[0].Position == P("a", 0).Position && groups[1].Position == P("b", 3).Position);
        });
        Check("walkable-alternate-inside-shared-region", () =>
        {
            var groups = BlindHoardProbePlanner.Build(56, [P("a", 0), P("b", 2)], (_, p) => p.Z > 0.1f);
            Require(groups.Count == 1 && groups[0].Covers(groups[0].Position));
        });
        Check("ordering-and-membership-produce-stable-keys", () =>
        {
            var points = new[] { P("a", 0), P("b", 2), P("c", 2.5f) };
            var a = Build(points).Single(); var b = Build(points.Reverse().ToArray()).Single();
            Require(a.Key == b.Key && a.Position == b.Position);
            Require(a.Key != Build(points.Take(2).ToArray()).Single().Key);
        });
        Check("merge-keeps-exact-work-and-completed-points", () =>
        {
            var floor = Floor(); floor.MarkProbeCompleted("done");
            var sites = new List<ObjectiveSite> { Site("a", 0), Site("b", 3), Site("done", 1),
                Site("exact", 1) with { Kind = ObjectiveSiteKind.Hoard },
                Site("chest", 1) with { Kind = ObjectiveSiteKind.ConfirmedChest } };
            var probabilities = new Dictionary<string, double> { ["a"] = 0.2, ["b"] = 0.3 };
            BlindHoardWork.Merge(sites, floor, probabilities, (_, _) => true);
            var merged = sites.Single(s => s.BlindProbe != null);
            Require(sites.Count == 4 && merged.BlindProbe!.Points.Count == 2);
            Require(Math.Abs(probabilities[merged.Key] - 0.5) < 1e-6);
            Require(sites.Single(s => s.Key == "exact").BlindProbe == null && sites.Any(s => s.Key == "chest"));
            Require(floor.CompletedProbeCount == 1);
        });
        Check("completion-marks-only-captured-source-points-and-floor", () =>
        {
            var floor = Floor(); var site = Merged(); var token = new SiteWorkToken(floor.Generation, site.Key, 1);
            BlindHoardWork.Complete(floor, token with { FloorGeneration = 99 }, site);
            BlindHoardWork.Complete(floor, token with { Key = "wrong" }, site);
            Require(floor.CompletedProbeCount == 0);
            var next = new FloorKnowledge(floor.Generation, 1, 57, 0, 0);
            BlindHoardWork.Complete(next, token, site); Require(next.CompletedProbeCount == 0);
            BlindHoardWork.Complete(floor, token, site);
            Require(floor.IsProbeCompleted("a") && floor.IsProbeCompleted("b") && floor.CompletedProbeCount == 2);
            var recreated = new List<ObjectiveSite> { Site("a", 0), Site("b", 3) };
            BlindHoardWork.Merge(recreated, floor, new(), (_, _) => true);
            Require(recreated.All(s => s.BlindProbe == null));
        });
        Check("merged-op-needs-full-dwell-and-fresh-scan", () =>
        {
            var site = Merged(); var op = Op(site);
            for (int i = 0; i <= 6; i++) Require(Tick(op, site.Position, i * 0.5, 1) == OpStatus.Running);
            Require(Tick(op, site.Position, 3.5, 1) == OpStatus.Running);
            Require(Tick(op, site.Position, 4, 2, observed: new Vector3(0, 0, 0)) == OpStatus.Running);
            Require(Tick(op, site.Position, 4.5, 3, available: false) == OpStatus.Running);
            Require(Tick(op, site.Position, 5, 4) == OpStatus.Succeeded);
        });
        Check("merged-op-resets-on-exit-suspension-and-wrong-floor", () =>
        {
            var site = Merged(); var op = Op(site);
            for (int i = 0; i <= 3; i++) Require(Tick(op, site.Position, i * 0.5, 1) == OpStatus.Running);
            Require(Tick(op, new(0, 0, 0), 2, 2) == OpStatus.Running);
            for (int i = 5; i <= 9; i++) Require(Tick(op, site.Position, i * 0.5, i) == OpStatus.Running);
            op.OnSuspended(); op.OnResumed(DateTime.UnixEpoch.AddSeconds(5));
            for (int i = 10; i <= 15; i++) Require(Tick(op, site.Position, i * 0.5, i) == OpStatus.Running);
            Require(Tick(op, site.Position, 8, 99, floor: 57) == OpStatus.Running);
            Require(Tick(op, site.Position, 8.5, 100, room: 3) == OpStatus.Running);
        });
        Check("singleton-keeps-upstream-arrival-and-dwell", () =>
        {
            var site = Site("single", 0); var op = Op(site);
            for (int i = 0; i <= 6; i++) Require(Tick(op, new(1.2f, 0, 0), i * 0.5, 1) == OpStatus.Running);
            Require(Tick(op, new(1.2f, 0, 0), 3.5, 2) == OpStatus.Succeeded);
        });
        for (int seed = 0; seed < 80; seed++)
        {
            int captured = seed;
            Check("coverage-conservation-" + seed, () =>
            {
                var random = new Random(captured);
                var points = Enumerable.Range(0, 30).Select(i => new BlindHoardPoint("p" + i.ToString("D2"),
                    random.Next(2), new((float)random.NextDouble() * 10, 0, (float)random.NextDouble() * 10))).ToArray();
                var groups = Build(points);
                Require(groups.Count <= points.Length && groups.Sum(g => g.Points.Count) == points.Length);
                Require(groups.SelectMany(g => g.Points).Select(p => p.Key).Distinct().Count() == points.Length);
                Require(groups.All(g => g.Covers(g.Position) && g.Points.All(p => p.Room == g.Room)));
                Require(groups.Select(g => g.Key).SequenceEqual(Build(points.Reverse().ToArray()).Select(g => g.Key)));
                foreach (var group in groups.Where(g => g.Points.Count > 1))
                    foreach (var offset in new[] { new Vector3(0.099f, 0, 0), new(-0.099f, 0, 0), new(0, 0, 0.099f), new(0, 0, -0.099f) })
                        Require(group.Covers(group.Position + offset));
            });
        }
        return count;
    }

    private static DetailedMapCatalog Catalog(DetailedMapScenarioDefinition scope) =>
        new() { ScenarioKey = scope.Key, TerritoryId = scope.TerritoryId, Floors = scope.Floors };
    private static BlindHoardPoint P(string key, float x, float y = 0, float z = 0) => new(key, 2, new(x, y, z));
    private static IReadOnlyList<BlindHoardProbeGroup> Build(params BlindHoardPoint[] points) =>
        BlindHoardProbePlanner.Build(56, points, (_, _) => true);
    private static FloorKnowledge Floor() => new(42, 1, 56, 0, 0);
    private static ObjectiveSite Site(string key, float x) =>
        new(0, ObjectiveSiteKind.Candidate, 2, new(x, 0, 0), 0.1f, 4, ObjectiveSiteStatus.Open, Key: key);
    private static ObjectiveSite Merged()
    {
        var sites = new List<ObjectiveSite> { Site("a", 0), Site("b", 3) };
        BlindHoardWork.Merge(sites, Floor(), new(), (_, _) => true);
        return sites.Single();
    }
    private static SiteWorkOp Op(ObjectiveSite site) => new(site, new(42, site.Key, 1), () => false);
    private static OpStatus Tick(SiteWorkOp op, Vector3 position, double seconds, long sequence,
        Vector3? observed = null, bool available = true, int floor = 56, int room = 2)
    {
        var now = DateTime.UnixEpoch.AddSeconds(seconds);
        var facts = new FloorFacts(floor, room, default, position, default, false, false, false,
            new FloorObjectEvidenceSnapshot(available, 1, sequence, now, observed ?? position, 0, [], [], [], []));
        return op.Tick(facts, null!, now, OpAuthority.None);
    }
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Hoard probe regression failed."); }
}
