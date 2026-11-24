using System.Reflection;
using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using Dalamud.Plugin.Services;

namespace NewFolder3;

internal static class Run41SaveCompletionTests
{
    private static FarmingPlan Plan => new(FarmingMode.FreeExploration, SaveUse.Create, 31, 3, true, false, true, true, false)
        { UsesDiagnosticCheckpoints = true, StopAfterFloor = 50 };
    private static FarmingAttemptResult Clear(int entry) => new(entry, true, false, false, false);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("free-save-default-and-old-settings-select-cleanup", () => {
                Check(!new FarmingPreferences().PreserveSaveOnCompletion, "default deletes");
                var old = JsonSerializer.Deserialize<FarmingPreferences>("{\"Cycles\":2}")!;
                Check(!old.PreserveSaveOnCompletion && old.Cycles == 2, "existing preferences migrate to default cleanup"); }),
            ("free-save-preserve-option-roundtrips-and-is-mode-local", () => {
                var settings = new FarmingSettings { Mode = FarmingMode.FreeExploration };
                settings.GetPreferences(new()).PreserveSaveOnCompletion = true;
                var copy = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(settings))!;
                Check(copy.GetPreferences(new()).PreserveSaveOnCompletion, "persist explicit opt-in");
                copy.Mode = FarmingMode.DeepProgression;
                Check(!copy.GetPreferences(new()).PreserveSaveOnCompletion, "separate mode preferences"); }),
            ("free-save-public-request-defaults-to-delete-and-can-opt-in", () => {
                var request = new FsdFarmingRequest(FarmingMode.FreeExploration, SaveUse.Create, 31, 1, false, new(false, true, true, false));
                Check(!request.PreserveSaveOnCompletion && (request with { PreserveSaveOnCompletion = true }).PreserveSaveOnCompletion, "explicit API control"); }),
            ("free-save-intermediate-floorset-retains-source", () => {
                var d = FarmingSessionPolicy.Decide(Plan, Clear(31));
                Check(!d.DeleteOwnedSave && !d.CountCycle && d.NextFloor == 41, "only end of selected exploration cleans"); }),
            ("free-save-selected-stop-defaults-to-delete", () => {
                var d = FarmingSessionPolicy.Decide(Plan, Clear(41));
                Check(d.DeleteOwnedSave && !d.CountCycle && !d.RecoverFailure, "stop boundary requires cleanup"); }),
            ("free-save-invalid-entry-cannot-authorize-boundary-cleanup", () => {
                var d = FarmingSessionPolicy.Decide(Plan, Clear(49));
                Check(!d.DeleteOwnedSave && !d.CountCycle && d.Error.Length > 0, "validate floorset before deciding to delete"); }),
            ("free-save-resumed-source-also-defaults-to-delete", () => {
                var d = FarmingSessionPolicy.Decide(Plan with { PreserveExternalSave = true, DeleteExternalSaveOnFailure = true }, Clear(41));
                Check(d.DeleteOwnedSave, "continuation no longer silently preserves on success"); }),
            ("free-save-explicit-preserve-skips-success-cleanup", () => {
                foreach (bool external in new[] { false, true })
                    Check(!FarmingSessionPolicy.Decide(Plan with { PreserveSaveOnCompletion = true, PreserveExternalSave = external }, Clear(41)).DeleteOwnedSave, "keep either source type"); }),
            ("free-save-preserve-does-not-bypass-failure-cleanup", () => {
                var d = FarmingSessionPolicy.Decide(Plan with { PreserveSaveOnCompletion = true, PreserveExternalSave = true, DeleteExternalSaveOnFailure = true }, new(41, false, true, false, false));
                Check(d.DeleteOwnedSave && d.RecoverFailure && !d.CountCycle, "failed save still follows retry policy"); }),
            ("free-save-floor-100-obeys-completion-option", () => {
                foreach (bool keep in new[] { false, true })
                {
                    var d = FarmingSessionPolicy.Decide(Plan with { StopAfterFloor = 100, PreserveExternalSave = true, PreserveSaveOnCompletion = keep }, Clear(91));
                    Check(d.CountCycle && d.DeleteOwnedSave == !keep, "terminal clear uses explicit completion setting");
                } }),
            ("free-save-scenario-waits-for-verified-delete-before-stop", () => {
                using var f = new Fixture(Plan); f.Scenario.Update(null!);
                Check(f.Cleanup.Prepared == 1 && !f.Scenario.IsComplete && f.Session.OwnedSlot == 1, "must wait for cleanup");
                f.Cleanup.Ready = true; f.Scenario.Update(null!); f.Scenario.Update(null!);
                Check(f.Scenario.IsComplete && !f.Scenario.ShouldLoop && f.Session.OwnedSlot == -1 && !f.Scenario.CountsAsCycle && f.Cleanup.Updates == 2,
                    "delete selected slot once, then stop without reentry"); }),
            ("free-save-cleanup-error-preserves-ownership-and-stops", () => {
                using var f = new Fixture(Plan); f.Cleanup.Fail = true; f.Scenario.Update(null!);
                Check(f.Context.StatusIsError && f.Scenario.IsComplete && f.Session.OwnedSlot == 1 && !f.Scenario.CountsAsCycle, "cannot claim deletion or success"); }),
            ("free-save-opt-in-scenario-stops-without-delete", () => {
                using var f = new Fixture(Plan with { PreserveSaveOnCompletion = true }); f.Scenario.Update(null!);
                Check(f.Scenario.IsComplete && !f.Scenario.ShouldLoop && f.Session.OwnedSlot == 1 && f.Cleanup.Prepared == 0, "keep actual selected slot"); }),
            ("free-save-terminal-preserve-cannot-reenter-completed-save", () => {
                using var f = new Fixture(Plan with { StopAfterFloor = 100, PreserveSaveOnCompletion = true }, 91);
                f.Scenario.Update(null!);
                Check(f.Scenario.IsComplete && f.Scenario.CountsAsCycle && !f.Scenario.ShouldLoop && f.Session.OwnedSlot == 1 && f.Cleanup.Prepared == 0, "preservation stops even infinite loop"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        internal readonly FarmingSession Session;
        internal readonly FarmingScenario Scenario;
        internal readonly CleanupFlow Cleanup = new();
        internal Fixture(FarmingPlan plan, int entry = 41)
        {
            Session = new(plan, 1); Context.FarmingPlan = plan; Context.MarkDutyCompleted();
            Scenario = new(Session, deleteSaveFlow: Cleanup);
            Set("_context", Context); Set("_entered", true); Set("_entryFloor", entry); Set("_restDone", true);
        }
        private void Set(string field, object value) => typeof(FarmingScenario).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Scenario, value);
        public void Dispose() { Scenario.Dispose(); Context.Dispose(); }
    }
    private sealed class CleanupFlow : IDeleteSaveFlow
    {
        internal int Prepared, Updates;
        internal bool Ready, Fail;
        private RunContext? _context;
        public void Prepare(RunContext context) { Prepared++; _context = context; }
        public bool Update(IFramework framework)
        { Updates++; if (Fail) _context!.StatusIsError = true; return Ready || Fail; }
    }
}
