using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace NewFolder3;

internal static class Run35RegressionTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Vector3 From = new(-371.44f, 1.99996f, 205.42f);
    private static readonly Vector3 To = new(-357.3325f, 2.54434f, 164.5071f);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    public static int Run()
    {
        var tests = new (string, Action)[] {
            ("passage-recorded-stall-escalates-and-cannot-reset-forever", RecordedStall),
            ("passage-validated-path-restores-navigation", SuccessfulPath),
            ("passage-pending-query-is-not-restarted-every-three-seconds", PendingQuery),
            ("passage-timed-out-query-is-cancelled-and-late-result-ignored", QueryTimeout),
            ("passage-faulted-query-is-observed-and-retried", FaultedQuery),
            ("passage-dispose-releases-owned-movement", DisposeMovement),
            ("passage-old-simplemove-cannot-overwrite-recovery", OldSimpleMove),
            ("passage-partial-path-is-rejected", PartialPath),
            ("passage-late-cancelled-result-never-moves-player", LateResult),
            ("passage-mechanic-wait-does-not-consume-active-budget", MechanicWait),
            ("passage-recovery-budget-survives-room-and-target-changes", TargetChanges),
            ("passage-jitter-does-not-count-as-progress", Jitter),
            ("passage-arrival-stops-owned-path", Arrival),
            ("passage-invalid-height-and-nan-path-rejected", InvalidPath),
            ("passage-long-valid-path-is-not-combat-35m-limited", LongPath),
            ("passage-navmesh-unavailable-fails-bounded", MeshUnavailable),
            ("timeout-zero-and-ejection-without-dutywiped-recovers", TimeoutZero),
            ("timeout-between-final-frames-recovers", TimeoutBetweenFrames),
            ("timeout-zoning-and-missing-player-wait", TimeoutZoning),
            ("timeout-ordinary-clear-wins", TimeoutCompletion),
            ("timeout-stale-sample-does-not-retry", TimeoutStale),
            ("timeout-unknown-exit-does-not-become-failure", TimeoutUnknown),
            ("timeout-new-attempt-clears-evidence", TimeoutReset),
            ("timeout-death-already-recorded-is-not-reported-twice", TimeoutDeath),
            ("timeout-does-not-trigger-at-in-duty-zero-or-loading-zero", TimeoutInside),
            ("timeout-failure-obeys-existing-count-and-save-guards", TimeoutSession),
            ("replay-default-is-on-and-ipc-errors-are-not-success", ReplayDefault),
            ("export-matches-time-and-validates-every-archived-hash", ExportMatched),
            ("export-no-bmr-still-returns-useful-logs-and-warning", ExportNoBmr),
            ("export-active-recording-is-a-snapshot-and-not-stopped", ExportActive),
            ("export-locked-replay-is-reported-without-losing-logs", ExportLocked),
            ("export-two-requests-never-overwrite-an-archive", ExportUnique),
        };
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private sealed class Harness : IDisposable
    {
        public DateTime Time = Now;
        public bool Ready = true, SimplePending;
        public int NormalCalls, Moves, Stops;
        public Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>> Query =
            (from, to, _) => Task.FromResult(new List<Vector3> { from, to });
        public readonly List<string> Events = [];
        public readonly PassageNavigationRecovery Recovery;
        public Harness() => Recovery = new(() => Ready, () => SimplePending, p => p,
            (f, t, c) => Query(f, t, c), _ => Moves++, () => { }, () => Stops++,
            (name, _) => Events.Add(name), () => Time);
        public NavDriveResult Tick(double sec = 0, Vector3? position = null, Vector3? target = null, NavDriveResult normal = NavDriveResult.Failed)
        { Time = Now.AddSeconds(sec); return Recovery.Tick(target ?? To, position ?? From, () => { NormalCalls++; return normal; }); }
        public void Dispose() => Recovery.Dispose();
    }
    private static void RecordedStall()
    {
        using var h = new Harness();
        for (int i = 0; i < 12; i++) h.Tick(i, normal: NavDriveResult.Moving);
        Check(!h.Recovery.Recovering, "normal navigation preserved before stall threshold");
        h.Tick(12, normal: NavDriveResult.Moving);
        Check(h.Recovery.Recovering, "observed stationary player must escalate even with Moving reports");
        NavDriveResult result = default;
        for (int i = 13; i <= 80; i++) result = h.Tick(i);
        int calls = h.NormalCalls, queries = h.Recovery.QueryCount;
        for (int i = 81; i < 384; i++) result = h.Tick(i);
        Check(result == NavDriveResult.Failed && h.NormalCalls == calls && h.Recovery.QueryCount == queries && queries <= 5,
            "reproduced 383 stationary ticks must not start 31 fresh helper budgets");
    }
    private static void SuccessfulPath() { using var h = new Harness(); h.Tick(); h.Tick(.1); Check(h.Moves == 1 && h.Events.Contains("passage-recovery-path-accepted"), "verified path must move"); }
    private static void PendingQuery()
    {
        using var h = new Harness(); var pending = new TaskCompletionSource<List<Vector3>>(); h.Query = (_, _, _) => pending.Task;
        h.Tick(); h.Tick(3); h.Tick(6); Check(h.Recovery.QueryCount == 1 && h.Moves == 0, "do not cancel at 3sec");
        pending.SetResult([From, To]); h.Tick(7); Check(h.Moves == 1, "late valid result accepted before timeout");
    }
    private static void OldSimpleMove() { using var h = new Harness { SimplePending = true }; h.Tick(); Check(h.Recovery.QueryCount == 0, "wait for old query"); Check(h.Tick(10) == NavDriveResult.Failed && h.Moves == 0, "never override unowned pending movement"); }
    private static void QueryTimeout()
    {
        using var h = new Harness(); var pending = new TaskCompletionSource<List<Vector3>>(); CancellationToken token = default;
        h.Query = (_, _, t) => { token = t; return pending.Task; }; h.Tick(); h.Tick(8);
        Check(token.IsCancellationRequested && h.Moves == 0, "query budget cancels only owned task");
        pending.SetResult([From, To]); h.Query = (f, t, _) => Task.FromResult(new List<Vector3> { f, t });
        h.Tick(9); Check(h.Moves == 0 && h.Recovery.QueryCount == 2, "old result ignored, bounded new attempt");
    }
    private static void FaultedQuery() { using var h = new Harness(); h.Query = (_, _, _) => Task.FromException<List<Vector3>>(new IOException("unreachable")); h.Tick(); h.Tick(.1); Check(h.Moves == 0 && h.Events.Contains("passage-recovery-path-rejected"), "fault is recorded, not an unobserved task"); }
    private static void DisposeMovement() { var h = new Harness(); h.Tick(); h.Tick(.1); h.Dispose(); Check(h.Stops == 1, "manual stop/unload releases path"); }
    private static void PartialPath() { using var h = new Harness(); h.Query = (_, _, _) => Task.FromResult(new List<Vector3> { From, (From + To) / 2 }); h.Tick(); h.Tick(.1); Check(h.Moves == 0 && h.Events.Contains("passage-recovery-path-rejected"), "partial endpoint must not count as reachable"); }
    private static void LateResult()
    {
        using var h = new Harness(); var old = new TaskCompletionSource<List<Vector3>>(); CancellationToken token = default;
        h.Query = (_, _, t) => { token = t; return old.Task; }; h.Tick(); h.Recovery.Suspend();
        Check(token.IsCancellationRequested, "own cancellation token"); old.SetResult([From, To]);
        h.Query = (f, t, _) => Task.FromResult(new List<Vector3> { f, t }); h.Tick(30); Check(h.Moves == 0, "ignore old result"); h.Tick(30.1); Check(h.Moves == 1, "resume from new position query");
    }
    private static void MechanicWait() { using var h = new Harness(); h.Tick(); h.Tick(.1); h.Recovery.Suspend(); Check(h.Stops == 1, "yield owned path"); Check(h.Tick(600) != NavDriveResult.Failed, "mechanic wait is not 600 seconds stuck"); }
    private static void TargetChanges() { using var h = new Harness(); h.Query = (_, _, _) => Task.FromResult(new List<Vector3>()); for (int i = 0; i < 25; i++) h.Tick(i, target: To + new Vector3(i % 2 * 4, 0, 0)); Check(h.Recovery.QueryCount == 5, "new room or actor does not refill query budget"); }
    private static void Jitter() { using var h = new Harness(); for (int i = 0; i < 15; i++) h.Tick(i, From + new Vector3((i % 2) * .2f, 0, 0), normal: NavDriveResult.Moving); Check(h.Recovery.Recovering, "recorded <.34m jitter cannot reset stall timer"); }
    private static void Arrival() { using var h = new Harness(); h.Tick(); h.Tick(.1); Check(h.Tick(1, To) == NavDriveResult.Arrived && h.Stops == 1, "portal arrival stops owned route"); }
    private static void InvalidPath() { Check(!PassageNavigationRecovery.ValidPath([From, To + Vector3.UnitY * 5], From, To), "height"); Check(!PassageNavigationRecovery.ValidPath([From, new(float.NaN, 0, 0)], From, To), "nan"); }
    private static void LongPath() => Check(PassageNavigationRecovery.ValidPath([From, To], From, To), "43m passage is valid; not a 35m combat recovery leg");
    private static void MeshUnavailable() { using var h = new Harness { Ready = false }; h.Tick(); Check(h.Tick(10) == NavDriveResult.Failed && h.Recovery.QueryCount == 0, "bounded navmesh readiness wait"); }

    private static DutyTimeoutEvidence Timer(int remaining = 0)
    { var t = new DutyTimeoutEvidence(); t.Observe(4, 5, 20, 3600, Now.AddSeconds(-20)); t.Observe(4, 5, remaining, 3600, Now); return t; }
    private static DutyTimeoutDecision Outside(DutyTimeoutEvidence t, int seconds, bool stable = true, bool completed = false, bool failed = false) => t.Evaluate(false, stable, completed, failed, Now.AddSeconds(seconds));
    private static void TimeoutZero() { var t = Timer(); Check(Outside(t, 1) == DutyTimeoutDecision.WaitingForExitEvidence, "event grace"); Check(Outside(t, 3) == DutyTimeoutDecision.Expired && t.LastFloor == 5, "no wiped event required"); }
    private static void TimeoutBetweenFrames() { var t = Timer(3); Outside(t, 2); Check(Outside(t, 4) == DutyTimeoutDecision.Expired, "deadline plus stable exit"); }
    private static void TimeoutZoning() { var t = Timer(); Outside(t, 1, false); Outside(t, 15, false); Outside(t, 16); Check(Outside(t, 17) == DutyTimeoutDecision.WaitingForExitEvidence && Outside(t, 18) == DutyTimeoutDecision.Expired, "wait for stable exit"); }
    private static void TimeoutCompletion() { var t = Timer(); Outside(t, 1); Check(Outside(t, 3, completed: true) == DutyTimeoutDecision.None, "completion beats timer estimate"); }
    private static void TimeoutStale() { var t = Timer(); Outside(t, 40); Check(Outside(t, 42) == DutyTimeoutDecision.None, "old timeout cannot turn disconnect into wipe"); }
    private static void TimeoutUnknown() { var t = Timer(100); Outside(t, 1); Check(Outside(t, 3) == DutyTimeoutDecision.None, "unknown exits retain fail-closed handling"); }
    private static void TimeoutReset() { var t = Timer(); t.Reset(); Check(Outside(t, 3) == DutyTimeoutDecision.None && t.LastFloor == 0, "no timer from prior run"); }
    private static void TimeoutDeath() { var t = Timer(); Check(Outside(t, 3, failed: true) == DutyTimeoutDecision.None, "deduplicate failure"); }
    private static void TimeoutInside() { var t = Timer(); Check(t.Evaluate(true, true, false, false, Now) == DutyTimeoutDecision.None, "zero inside not an ejection"); var initialZero = new DutyTimeoutEvidence(); initialZero.Observe(4, 5, 0, 3600, Now); Check(Outside(initialZero, 3) == DutyTimeoutDecision.None, "uninitialized zero not countdown evidence"); }
    private static void TimeoutSession()
    {
        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false, false, true, true, false);
        var fail = new FarmingAttemptResult(71, false, true, false, false);
        var d = FarmingSessionPolicy.Decide(plan, fail);
        Check(!d.CountCycle && d.RecoverFailure && d.NextFloor == 1 && d.DeleteOwnedSave, "retry and no success count");
        d = FarmingSessionPolicy.Decide(plan with { HoldOnFailure = true }, fail);
        Check(!d.CountCycle && !d.RecoverFailure && !d.DeleteOwnedSave, "diagnostic hold");
        d = FarmingSessionPolicy.Decide(plan with { SaveUse = SaveUse.Prepared, PreserveExternalSave = true }, fail);
        Check(!d.DeleteOwnedSave && !d.CountCycle, "external save guard");
        d = FarmingSessionPolicy.Decide(plan with { Mode = FarmingMode.FinalBoss, SaveUse = SaveUse.None, StartFloor = 99 }, fail);
        Check(!d.DeleteOwnedSave && !d.CountCycle && !d.RecoverFailure, "final boss remains no-save");
    }
    private static void ReplayDefault() { Check(new DeepDungeon.Fsd.Runtime.FsdSettings().AutoRecordBmrReplays, "default on"); Check(BmrReplayConfiguration.IsEnabled(["True"]) && !BmrReplayConfiguration.IsEnabled(["False"]) && !BmrReplayConfiguration.IsEnabled(["Config type not found"]) && !BmrReplayConfiguration.IsEnabled(null), "IPC readback must parse exact single bool"); }

    private sealed class ExportFixture : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "NewFolder3-35-tests-" + Guid.NewGuid().ToString("N"));
        public string Logs => Path.Combine(Root, "logs"); public string Replays => Path.Combine(Root, "custom-replays");
        public readonly DateTime Time = DateTime.UtcNow;
        public string ReplayPath => Path.Combine(Replays, "2130_MCH100__" + Time.ToLocalTime().ToString("yyyy_MM_dd_HH_mm_ss") + ".log");
        public ExportFixture(bool closed = true)
        {
            Directory.CreateDirectory(Logs); Directory.CreateDirectory(Replays);
            string log = JsonSerializer.Serialize(new { timestampUtc = Time.AddMinutes(-5), eventType = "floor-changed" }) + "\n" +
                JsonSerializer.Serialize(new { timestampUtc = Time, eventType = closed ? "run-recorder-closing" : "navigation" }) + "\n";
            File.WriteAllText(Path.Combine(Logs, "run.jsonl"), log);
            File.WriteAllBytes(ReplayPath, [66, 77, 82, 0, 1, 2]);
        }
        public RunLogExportResult Export(string? replays = null) => DeepDungeonRunLogExporter.ExportRecent(Logs, Root, "NewFolder3", Time, replays ?? Replays);
        public void Dispose() => Directory.Delete(Root, true);
    }
    private static void ExportMatched()
    {
        using var f = new ExportFixture();
        string old = Path.Combine(f.Replays, "old__2026_01_01_01_00_00.log"); File.WriteAllText(old, "unrelated"); File.SetLastWriteTimeUtc(old, new(2026, 1, 1, 1, 0, 0, DateTimeKind.Utc));
        var result = f.Export(); Check(result.ReplayCount == 1, "only overlapping replays");
        using var zip = ZipFile.OpenRead(result.ArchivePath);
        using var report = JsonDocument.Parse(zip.GetEntry("打包清單.json")!.Open());
        foreach (var file in report.RootElement.GetProperty("files").EnumerateArray())
        {
            using var input = zip.GetEntry(file.GetProperty("Entry").GetString()!)!.Open();
            Check(Convert.ToHexStringLower(SHA256.HashData(input)) == file.GetProperty("Sha256").GetString(), "hash every snapshot");
        }
    }
    private static void ExportNoBmr() { using var f = new ExportFixture(); var result = f.Export(Path.Combine(f.Root, "missing")); Check(result.ReplayCount == 0 && File.Exists(result.ArchivePath) && result.ReplayStatus.Contains("不存在"), "no BMR is not log-export failure"); }
    private static void ExportActive()
    {
        using var f = new ExportFixture(false); using var writer = new FileStream(f.ReplayPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        var result = f.Export(); Check(result.ReplayCount == 1 && writer.CanWrite, "export must not stop recording");
        using var zip = ZipFile.OpenRead(result.ArchivePath); using var report = JsonDocument.Parse(zip.GetEntry("打包清單.json")!.Open());
        Check(report.RootElement.GetProperty("files").EnumerateArray().All(x => x.GetProperty("SnapshotOnly").GetBoolean()), "no false completeness claims");
    }
    private static void ExportLocked() { using var f = new ExportFixture(); using var locked = new FileStream(f.ReplayPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); var result = f.Export(); Check(result.ReplayCount == 0 && result.ReplayStatus.Contains("警告"), "locked file warning and log success"); }
    private static void ExportUnique() { using var f = new ExportFixture(); var a = f.Export(); var b = f.Export(); Check(a.ArchivePath != b.ArchivePath && File.Exists(a.ArchivePath) && File.Exists(b.ArchivePath), "do not overwrite previous evidence"); }
}
