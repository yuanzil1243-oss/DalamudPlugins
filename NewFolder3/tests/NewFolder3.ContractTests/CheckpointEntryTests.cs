using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace NewFolder3;

internal static class CheckpointEntryTests
{
    public static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("entry-cleanup-closes-only-save-and-menu-and-confirms-disappearance", ClosesParents),
            ("entry-cleanup-throttles-requests-and-retries-lost-close", Retries),
            ("entry-cleanup-timeout-does-not-authorize-automation", Timeout),
            ("entry-cleanup-never-closes-parents-beneath-a-modal", Modal),
            ("entry-cleanup-reset-allows-new-attempt", Reset),
            ("wrong-entry-closes-save-panel-before-reporting-floor-mismatch", WrongEntry),
            ("correct-31-entry-waits-for-entry-ui-before-automation", CorrectEntry),
            ("entry-cleanup-failure-preserves-save-and-blocks-entry-acceptance", FailedCleanup),
            ("late-save-panel-is-closed-after-entry-was-already-accepted", LatePanel)
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine($"  ok  {name}"); }
        return tests.Length;
    }

    private static readonly DateTime Now = new(2026, 11, 11, 3, 29, 53, DateTimeKind.Utc);
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void ClosesParents()
    {
        var open = new HashSet<string> { "DeepDungeonSaveData", "DeepDungeonMenu", "Unrelated" };
        var closed = new List<string>();
        var cleanup = new DeepDungeonEntryUiCleanup(open.Contains, name => { closed.Add(name); open.Remove(name); }, () => Now);
        Assert(cleanup.Update(out _, out bool requested) == EntryUiCleanupResult.Waiting && requested, "Must request close first.");
        Assert(closed.SequenceEqual(["DeepDungeonSaveData", "DeepDungeonMenu"]) && open.SetEquals(["Unrelated"]), "Only entry parents.");
        Assert(cleanup.Update(out _, out requested) == EntryUiCleanupResult.Ready && !requested, "Observe disappearance before continuing.");
    }

    private static void Retries()
    {
        var now = Now; int calls = 0;
        var cleanup = new DeepDungeonEntryUiCleanup(name => name == "DeepDungeonSaveData", _ => calls++, () => now);
        cleanup.Update(out _, out _);
        for (int i = 0; i < 20; ++i) { now = Now.AddMilliseconds(i * 10); cleanup.Update(out _, out _); }
        Assert(calls == 1, "Must not close on every frame.");
        now = Now.AddMilliseconds(300); cleanup.Update(out _, out _);
        Assert(calls == 2, "Lost close request must retry.");
    }

    private static void Timeout()
    {
        var now = Now;
        var cleanup = new DeepDungeonEntryUiCleanup(name => name == "DeepDungeonSaveData", _ => { }, () => now);
        cleanup.Update(out _, out _); now = Now.AddSeconds(3.1);
        Assert(cleanup.Update(out string status, out bool requested) == EntryUiCleanupResult.Failed && !requested && status.Length > 0,
            "Stuck panel must stop the task, not accept entry.");
    }

    private static void Modal()
    {
        foreach (string modal in new[] { "SelectString", "SelectYesno", "ContentsFinderConfirm" })
        {
            var open = new HashSet<string> { "DeepDungeonSaveData", modal }; int calls = 0;
            var cleanup = new DeepDungeonEntryUiCleanup(open.Contains, _ => calls++, () => Now);
            Assert(cleanup.Update(out _, out bool requested) == EntryUiCleanupResult.Waiting && !requested && calls == 0,
                "Do not force-close parent chains beneath a modal.");
            open.Remove(modal); cleanup.Update(out _, out requested);
            Assert(requested && calls == 1, "Close after the modal disappears.");
        }
    }

    private static void Reset()
    {
        var now = Now; int calls = 0;
        var cleanup = new DeepDungeonEntryUiCleanup(name => name == "DeepDungeonSaveData", _ => calls++, () => now);
        cleanup.Update(out _, out _); now = Now.AddSeconds(4);
        Assert(cleanup.Update(out _, out _) == EntryUiCleanupResult.Failed, "Reproduce timeout.");
        cleanup.Reset();
        Assert(cleanup.Update(out _, out bool requested) == EntryUiCleanupResult.Waiting && requested && calls == 2,
            "Retry timing and deadline must reset per attempt.");
    }

    private static (FarmingScenario Scenario, RunContext Context, FarmingSession Session) Entering(
        int actualFloor, DeepDungeonEntryUiCleanup cleanup)
    {
        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 31, 1, false, false, true, true, false)
        { UsesDiagnosticCheckpoints = true, StopAfterFloor = 50 };
        var session = new FarmingSession(plan, 0);
        var context = new RunContext(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        context.FarmingPlan = plan;
        SetDuty(context, nameof(DutyState.IsInDuty), true);
        SetDuty(context, nameof(DutyState.DungeonId), 4u);
        SetDuty(context, nameof(DutyState.Floor), (byte)actualFloor);
        var scenario = new FarmingScenario(session, cleanup);
        typeof(FarmingScenario).GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(scenario, context);
        return (scenario, context, session);
    }

    private static bool Entered(FarmingScenario scenario) =>
        (bool)typeof(FarmingScenario).GetField("_entered", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scenario)!;
    private static void SetDuty(RunContext context, string name, object value) =>
        typeof(DutyState).GetProperty(name)!.SetValue(context.Duty, value);

    private static void WrongEntry()
    {
        var open = new HashSet<string> { "DeepDungeonSaveData", "DeepDungeonMenu" };
        var cleanup = new DeepDungeonEntryUiCleanup(open.Contains, name => open.Remove(name), () => Now);
        var (scenario, context, session) = Entering(1, cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Assert(open.Count == 0 && !context.StatusIsError && !Entered(scenario) && !scenario.EntryAccepted, "Close panel before floor validation.");
            scenario.Update(null!);
            Assert(context.StatusIsError && !scenario.EntryAccepted && context.StatusLine.Contains("31") && context.StatusLine.Contains("1"), "Wrong floor must still be rejected.");
            scenario.Update(null!);
            Assert(scenario.IsComplete && session.OwnedSlot == 0 && session.NextFloor == 31, "Preserve save; no automatic restart/delete.");
        }
    }

    private static void CorrectEntry()
    {
        bool open = true;
        var cleanup = new DeepDungeonEntryUiCleanup(name => name == "DeepDungeonSaveData" && open, _ => open = false, () => Now);
        var (scenario, context, session) = Entering(31, cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Assert(!Entered(scenario) && !scenario.EntryAccepted && !open, "Wait for close acknowledgement.");
            scenario.Update(null!);
            Assert(Entered(scenario) && scenario.EntryAccepted && !context.StatusIsError && session.OwnedSlot == 0 && !scenario.IsComplete, "Accept correct 31 after cleanup.");
        }
    }

    private static void LatePanel()
    {
        bool open = false;
        var cleanup = new DeepDungeonEntryUiCleanup(name => name == "DeepDungeonSaveData" && open,
            _ => open = false, () => Now);
        var (scenario, context, session) = Entering(31, cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Assert(scenario.EntryAccepted, "Initial frame with no panel should accept entry.");
            open = true;
            scenario.Update(null!);
            Assert(!open && !context.StatusIsError && session.OwnedSlot == 0,
                "Late panel must be closed without selecting/deleting another save.");
        }
    }

    private static void FailedCleanup()
    {
        var now = Now;
        var cleanup = new DeepDungeonEntryUiCleanup(name => name == "DeepDungeonSaveData", _ => { }, () => now);
        var (scenario, context, session) = Entering(31, cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!); now = Now.AddSeconds(3.1); scenario.Update(null!);
            Assert(context.StatusIsError && !Entered(scenario) && session.OwnedSlot == 0, "Stuck UI must not authorize automation or cleanup save.");
        }
    }
}
