using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;

namespace NewFolder3;

internal static class ProbeNavigationTests
{
    public static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("recorded-midpoint-reproduces-navigation-failure", RecordedMidpointReproducesFailure),
            ("recorded-tight-probes-stay-separate", TightProbesStaySeparate),
            ("separate-probes-accept-recorded-endpoint-precision", SeparateProbesCanComplete),
            ("wide-overlap-retains-both-source-probes", WideOverlapRetainsSources),
            ("merged-arrival-stays-inside-both-footprints", MergedCoverageIsSafe),
            ("failed-midpoint-expands-without-marking-success", ExpansionPreservesRequiredWork),
            ("live-chest-append-preserves-progress-and-deduplicates", LiveChestAppend),
            ("expanded-probes-complete-original-work", ExpandedProbesCompleteWork),
            ("expanded-probe-failure-remains-failure", ExpandedFailureRemainsFailure),
            ("expansion-preserves-other-waypoints", ExpansionPreservesOtherWaypoints),
            ("expansion-rejects-stale-waypoint", ExpansionRejectsStaleWaypoint),
            ("expansion-is-bounded-and-visits-nearest-source-first", ExpansionIsBounded),
            ("tight-cross-tier-probes-stay-separate", CrossTierProbesStaySeparate)
        ];
        foreach (var (name, test) in tests)
        {
            test();
            Console.WriteLine($"  ok  {name}");
        }
        return tests.Length;
    }

    private static readonly Vector3 RecordedTarget = new(-368.03387f, 1.9999964f, 430.97943f);
    private static readonly Vector3 RecordedPlayer = new(-368.01178f, 1.9999964f, 430.68228f);

    private static List<RoomWaypoint> TightProbes()
    {
        const float halfDistance = 1.7f - 0.01f - 0.28564584f;
        return
        [
            new(RecordedTarget - new Vector3(halfDistance, 0, 0), RoomObjectiveType.Trap),
            new(RecordedTarget + new Vector3(halfDistance, 0, 0), RoomObjectiveType.Trap)
        ];
    }

    private static RoomWaypoint WideProbe() => RoomSearchContext.MergeOverlappingProbeWaypoints(
        [new(Vector3.Zero, RoomObjectiveType.Trap), new(new(0.8f, 0, 0), RoomObjectiveType.Trap)]).Single();

    private static void RecordedMidpointReproducesFailure()
    {
        var clock = new TestClock();
        var runner = Runner(clock);
        runner.Configure(RecordedTarget, 0.28564584f, null, 0, _ => true, 4);
        Assert(Vector3.Distance(RecordedTarget, RecordedPlayer) is > 0.297f and < 0.299f, "Recorded position mismatch.");
        TaskResult result = TaskResult.InProgress;
        for (int i = 0; i <= 9; i++)
        {
            clock.Now = clock.Start.AddSeconds(i);
            result = runner.Update(RecordedPlayer);
        }
        Assert(result == TaskResult.NavigationFailed, "The original precision mismatch must reproduce the stuck timeout.");
    }

    private static void TightProbesStaySeparate()
    {
        var originals = TightProbes();
        var result = RoomSearchContext.MergeOverlappingProbeWaypoints(originals);
        Assert(result.SequenceEqual(originals), "The recorded narrow overlap must retain both original positions.");
    }

    private static void SeparateProbesCanComplete()
    {
        var clock = new TestClock();
        var runner = Runner(clock);
        foreach (var probe in RoomSearchContext.MergeOverlappingProbeWaypoints(TightProbes()))
        {
            runner.Configure(probe.Position, 1.4f, null, 0, elapsed => elapsed >= 3, 4);
            var endpoint = probe.Position + RecordedPlayer - RecordedTarget;
            Assert(runner.Update(endpoint) == TaskResult.Arrived, "Original probe must accept recorded endpoint precision.");
            clock.Now = clock.Now.AddSeconds(3);
            Assert(runner.Update(endpoint) == TaskResult.Complete, "Probe must still stand for its full detection interval.");
        }
    }

    private static void WideOverlapRetainsSources()
    {
        var merged = WideProbe();
        Assert(merged.HasExplicitArrivalRadius && merged.ArrivalRadius >= 1, "Wide overlap should still be optimized.");
        Assert(merged.SourceProbes == new SharedProbeSources(Vector3.Zero, new(0.8f, 0, 0)), "Recovery must retain exact originals.");
    }

    private static void MergedCoverageIsSafe()
    {
        for (int i = 0; i <= 340; i++)
        {
            float separation = i / 100f;
            var result = RoomSearchContext.MergeOverlappingProbeWaypoints(
                [new(Vector3.Zero, RoomObjectiveType.Trap), new(new(separation, 0, 0), RoomObjectiveType.Trap)]);
            if (result.Count != 1) continue;
            var merged = result[0];
            Assert(merged.ArrivalRadius >= 1, "Do not produce precision-dependent midpoint tasks.");
            Assert(merged.ArrivalRadius + separation / 2 <= RoomSearchContext.TrapMarkerTriggerRadius - 0.009f,
                "Every allowed arrival position must remain inside both original detection footprints.");
        }
    }

    private static void ExpansionPreservesRequiredWork()
    {
        var merged = WideProbe();
        var context = Context([merged]);
        Assert(context.TryExpandCurrentSharedProbe(merged, Vector3.Zero), "Midpoint must expand.");
        var progress = context.BuildOutcomeSnapshot(false).Hoard;
        Assert(progress.RequiredCount == 2 && progress.CompletedCount == 0 && !progress.Failed,
            "Expansion must require both probes without marking success or poisoning the room with the midpoint failure.");
        Assert(RoomObjectiveOutcomePlanner.Decide(context.BuildOutcomeSnapshot(false)).Hoard == ObjectiveOutcomeKind.Pending,
            "Expanded work must remain pending.");
    }

    private static void ExpandedProbesCompleteWork()
    {
        var merged = WideProbe();
        var context = Context([merged]);
        context.TryExpandCurrentSharedProbe(merged, Vector3.Zero);
        CompleteCurrent(context);
        Assert(!RoomObjectiveOutcomePlanner.Decide(context.BuildOutcomeSnapshot(false)).MarkHoardSearched,
            "Completing only one original cannot complete the merged coverage.");
        CompleteCurrent(context);
        Assert(RoomObjectiveOutcomePlanner.Decide(context.BuildOutcomeSnapshot(false)).MarkHoardSearched,
            "Both originals must complete the required coverage.");
    }

    private static void ExpandedFailureRemainsFailure()
    {
        var merged = WideProbe();
        var context = Context([merged]);
        context.TryExpandCurrentSharedProbe(merged, Vector3.Zero);
        context.RecordWaypointOutcome(context.CurrentWaypoint!.Value, WaypointOutcomeKind.Failed, "NavigationFailed");
        context.AdvanceWaypoint();
        CompleteCurrent(context);
        var result = RoomObjectiveOutcomePlanner.Decide(context.BuildOutcomeSnapshot(false));
        Assert(result.Hoard == ObjectiveOutcomeKind.Failed && !result.MarkHoardSearched, "Never turn unreachable coverage into searched coverage.");
    }

    private static void ExpansionPreservesOtherWaypoints()
    {
        var chest = new RoomWaypoint(new(4, 0, 0), RoomObjectiveType.ChestBronze);
        var merged = WideProbe();
        var later = new RoomWaypoint(new(8, 0, 0), RoomObjectiveType.Trap);
        var context = Context([chest, merged, later]);
        CompleteCurrent(context);
        Assert(context.TryExpandCurrentSharedProbe(merged, Vector3.Zero), "Expansion after a completed chest must work.");
        Assert(context.CurrentWaypointIndex == 1 && context.Waypoints[0] == chest && context.Waypoints[3] == later,
            "Expansion must preserve progress and surrounding tasks.");
        Assert(context.BuildOutcomeSnapshot(false).Chests.CompletedCount == 1, "Chest progress must survive expansion.");
    }

    private static void ExpansionRejectsStaleWaypoint()
    {
        var merged = WideProbe();
        var context = Context([merged]);
        Assert(!context.TryExpandCurrentSharedProbe(merged with { Position = Vector3.One }, Vector3.Zero), "Reject stale task identity.");
        Assert(context.TotalWaypointCount == 1, "Rejected expansion must leave tasks intact.");
    }

    private static void ExpansionIsBounded()
    {
        var merged = WideProbe();
        var context = Context([merged]);
        Assert(context.TryExpandCurrentSharedProbe(merged, new(0.9f, 0, 0)), "Initial expansion failed.");
        Assert(context.CurrentWaypoint!.Value.Position == new Vector3(0.8f, 0, 0), "Visit the nearest original first.");
        Assert(!context.TryExpandCurrentSharedProbe(context.CurrentWaypoint.Value, Vector3.Zero), "An original probe must not recursively expand.");
        Assert(!context.TryExpandCurrentSharedProbe(merged, Vector3.Zero), "The same midpoint cannot expand twice.");
        Assert(context.TotalWaypointCount == 2, "Recovery must be bounded.");
    }

    private static void CrossTierProbesStaySeparate()
    {
        var probes = TightProbes();
        var result = RoomSearchContext.OrderWaypointsByEvidenceTier(Vector3.Zero,
            Array.Empty<RoomWaypoint>(), [probes[0]], [probes[1]]);
        Assert(result.Count == 2 && result.Contains(probes[0]) && result.Contains(probes[1]),
            "Cross-tier merging must enforce the same precision guard.");
    }

    private static void LiveChestAppend()
    {
        var context = Context([new(Vector3.Zero, RoomObjectiveType.ChestGold)]);
        CompleteCurrent(context);
        Assert(context.AppendLiveChest(new(3,0,0), RoomObjectiveType.ChestSilver), "New drop must enter existing room work.");
        Assert(!context.AppendLiveChest(new(3,0,0), RoomObjectiveType.ChestSilver), "Duplicate observation must not queue twice.");
        var before = context.BuildOutcomeSnapshot(false).Chests;
        Assert(before.RequiredCount == 2 && before.CompletedCount == 1, "Preserve completed work; require actual new chest completion.");
        CompleteCurrent(context);
        Assert(context.BuildOutcomeSnapshot(false).Chests.CompletedCount == 2, "New chest is accounted for exactly once.");
    }

    private static void CompleteCurrent(RoomSearchContext context)
    {
        context.RecordWaypointOutcome(context.CurrentWaypoint!.Value, WaypointOutcomeKind.Completed, "Completed");
        context.AdvanceWaypoint();
    }

    private static RoomSearchContext Context(List<RoomWaypoint> waypoints) =>
        (RoomSearchContext)Activator.CreateInstance(typeof(RoomSearchContext), BindingFlags.Instance | BindingFlags.NonPublic,
            null, [21, Vector3.Zero, 30f, waypoints, true, true, false, false, 0, 0, 0], null)!;

    private static WaypointTaskRunner Runner(TestClock clock) => new(new NavigationHelper(new TestNavigator(),
        () => false, () => clock.Now, _ => { }, _ => { }, () => false), () => clock.Now);

    private sealed class TestClock
    {
        public readonly DateTime Start = new(2026, 11, 10, 12, 6, 52, DateTimeKind.Utc);
        public DateTime Now;
        public TestClock() => Now = Start;
    }

    private sealed class TestNavigator : INavigator
    {
        public bool PathfindAndMoveTo(Vector3 dest, bool alignCamera) => true;
        public bool RepathTo(Vector3 dest, bool alignCamera) => true;
        public void CancelAll() { }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
