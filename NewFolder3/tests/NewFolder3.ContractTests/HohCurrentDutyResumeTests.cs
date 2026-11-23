using DeepDungeon.Fsd.Core;
namespace NewFolder3;
internal static class HohCurrentDutyResumeTests
{
    internal static int Run()
    {
        var plan = new FarmingPlan(FarmingMode.FreeExploration, SaveUse.Create, 1, 1, false, false, true, true, false)
            { DungeonId = 2, StopAfterFloor = 100 };
        (string Name, bool Actual, bool Expected)[] cases = [
            ("hoh-resume-floor56-explicit-slot", HohCurrentDutyResumePolicy.Allows(plan, 0, 2, 56, false, false, true, 600), true),
            ("hoh-resume-never-adopts-pt", HohCurrentDutyResumePolicy.Allows(plan, 0, 4, 56, false, false, true, 600), false),
            ("hoh-resume-needs-slot", HohCurrentDutyResumePolicy.Allows(plan, null, 2, 56, false, false, true, 600), false),
            ("hoh-resume-rejects-invalid-slot", HohCurrentDutyResumePolicy.Allows(plan, 2, 2, 56, false, false, true, 600), false),
            ("hoh-resume-waits-for-transition", HohCurrentDutyResumePolicy.Allows(plan, 0, 2, 56, true, false, true, 600), false),
            ("hoh-resume-rejects-unread-state", HohCurrentDutyResumePolicy.Allows(plan, 0, 2, 56, false, true, true, 600), false),
            ("hoh-resume-rejects-dead-player", HohCurrentDutyResumePolicy.Allows(plan, 0, 2, 56, false, false, false, 600), false),
            ("hoh-resume-rejects-expired-duty", HohCurrentDutyResumePolicy.Allows(plan, 0, 2, 56, false, false, true, 0), false),
            ("hoh-resume-rejects-unknown-timer", HohCurrentDutyResumePolicy.Allows(plan, 0, 2, 56, false, false, true, null), false),
            ("hoh-resume-rejects-terminal-floor", HohCurrentDutyResumePolicy.Allows(plan, 0, 2, 100, false, false, true, 600), false),
            ("hoh-resume-rejects-completed-boundary", HohCurrentDutyResumePolicy.Allows(plan with { StopAfterFloor = 50 }, 0, 2, 56, false, false, true, 600), false),
            ("hoh-resume-rejects-pt-plan", HohCurrentDutyResumePolicy.Allows(plan with { DungeonId = 4 }, 0, 2, 56, false, false, true, 600), false)
        ];
        foreach (var c in cases) { if (c.Actual != c.Expected) throw new Exception(c.Name); Console.WriteLine("  ok  " + c.Name); }
        return cases.Length;
    }
}
