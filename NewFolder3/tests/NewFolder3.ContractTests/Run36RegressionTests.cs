using System.Reflection;
using System.Text.Json;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;

namespace NewFolder3;

internal static class Run36RegressionTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    public static int Run()
    {
        var tests = new (string, Action)[] {
            ("free-hoard-toggle-is-optional-and-starts-at-each-valid-group", FreePlans),
            ("free-hoard-does-not-unlock-other-mode-or-chest-options", ModeIsolation),
            ("free-hoard-default-is-off-even-with-legacy-hoard-enabled", DefaultOff),
            ("free-hoard-preference-roundtrip-and-mode-isolation", PersistedPreferences),
            ("free-hoard-live-target-and-next-floorset-propagation", LiveTargets),
            ("free-hoard-collection-does-not-count-intermediate-clear", CompletionBoundaries),
            ("free-hoard-does-not-bind-a-fixed-detailed-map", NoFixedMap),
            ("free-intuition-new-use-is-limited-to-first-seven-normal-floors", IntuitionFloors),
            ("free-intuition-high-floors-allow-98-but-not-99-or-100", HighIntuition),
            ("free-hoard-native-positive-beats-historical-distribution", PositiveEvidence),
            ("free-hoard-native-negative-stops-search-not-progression", NegativeEvidence),
            ("free-hoard-count-five-is-only-a-hint", DistributionHints),
            ("free-intuition-missing-text-remains-unknown", UnknownText),
            ("free-hoard-no-candidates-does-not-invent-a-waypoint", MissingCandidates),
            ("free-hoard-indicator-replaces-historical-candidates", IndicatorFirst),
            ("free-hoard-high-floor-detour-requires-a-known-time-reserve", TimeReserve),
            ("free-hoard-detour-never-enables-off-route-supply-chests", HoardOnlyDetour),
            ("free-hoard-time-expiry-prunes-branches-again", DetourExpiry),
            ("free-hoard-clear-before-search-and-no-premature-passage", ObjectiveOrder),
            ("optional-hoard-skip-resolves-work-without-claiming-absence", OptionalSkip),
            ("optional-hoard-late-positive-resumes-only-unknown-result", OptionalResume),
            ("optional-hoard-time-reserve-overrides-unknown-and-stays-final", OptionalReserve),
            ("optional-hoard-skip-cannot-affect-fixed-hoard-mode", OptionalScope),
            ("cleanup-repeated-requests-never-renew-progress", NoFakeProgress),
            ("cleanup-real-milestones-renew-progress", RealProgress),
            ("cleanup-old-ui-oscillation-is-not-new-progress", NoUiOscillation),
            ("cleanup-recovery-exhaustion-is-bounded", BoundedRecovery),
            ("cleanup-only-real-npc-approach-renews-progress", ApproachProgress),
            ("cleanup-zoning-pauses-active-budget", PausedCleanup),
            ("cleanup-total-budget-survives-navigation-progress", TotalCleanupBudget),
            ("failure-cleanup-must-complete-before-retry-and-never-counts", FailureCleanup),
            ("failure-cleanup-errors-preserve-ownership-and-stop", CleanupError),
            ("removed-hold-cannot-return-through-json-or-api", RemovedHold),
            ("needle-enemy-has-wolf-priority-and-mimic-still-wins", NeedlePriority),
            ("needle-priority-stays-in-its-own-normal-floor-range", NeedleScope),
            ("diagnostic-empty-version-cannot-throw-into-control", EmptyVersion),
        };
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private static FarmingPlan Plan(bool hoard = true, int floor = 31) =>
        new(FarmingMode.FreeExploration, SaveUse.Create, floor, 3, false, hoard, true, true, false);
    private static FsdSettings Settings() => new() { NecromancerAutoBandedFarmEnabled = true };
    private static void FreePlans()
    {
        for (int floor = 1; floor <= 91; floor += 10)
            foreach (bool hoard in new[] { false, true })
                Check(FarmingPlan.TryCreate(FarmingMode.FreeExploration, SaveUse.Create, floor, 3, false,
                    hoard, true, true, false, out var plan, out _) && plan!.BandedEnabled == hoard, "free plan must accept either hoard choice");
    }
    private static void ModeIsolation()
    {
        Check(!FarmingTargetPolicy.LocksHoard(FarmingMode.FreeExploration) &&
            FarmingTargetPolicy.LocksGold(FarmingMode.FreeExploration) && FarmingTargetPolicy.LocksSilverBronze(FarmingMode.FreeExploration), "unlock only hoard");
        foreach (var mode in new[] { FarmingMode.DeepProgression, FarmingMode.FinalBoss })
            Check(!FarmingTargetPolicy.IsValid(mode, new(true, true, true, false)), "other modes remain constrained");
        Check(!FarmingTargetPolicy.IsValid(FarmingMode.FreeExploration, new(true, false, true, false)) &&
            !FarmingTargetPolicy.IsValid(FarmingMode.FreeExploration, new(true, true, true, true)), "gold/silver/bronze policy unchanged");
    }
    private static void DefaultOff()
    {
        var settings = new FarmingSettings { Mode = FarmingMode.FreeExploration };
        Check(settings.GetPreferences(Settings()).BandedEnabled == false, "legacy fixed-hoard true must not enable free-hoard");
    }
    private static void PersistedPreferences()
    {
        var settings = new FarmingSettings { Mode = FarmingMode.FreeExploration };
        settings.GetPreferences(Settings()).BandedEnabled = true;
        settings = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(settings))!;
        Check(settings.GetPreferences(Settings()).BandedEnabled == true, "preference must persist");
        settings.Mode = FarmingMode.DeepProgression;
        Check(settings.GetPreferences(Settings()).BandedEnabled == false, "free setting cannot affect challenge mode");
        settings.Mode = FarmingMode.FreeExploration;
        Check(settings.GetPreferences(Settings()).BandedEnabled == true, "mode switch must retain free setting");
    }
    private static RunContext Context() => new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
    private static void LiveTargets()
    {
        var session = new FarmingSession(Plan(false));
        Check(session.TrySetTargets(new(true, true, true, false)), "runtime toggle accepted");
        using (var context = Context()) using (var scenario = new FarmingScenario(session))
        {
            scenario.Initialize(context);
            Check(context.RunOptions.Current.BandedEnabled && context.RunOptions.Current.HoardDistributionHintsOnly &&
                !context.RunOptions.Current.DiscoveryOnly && context.RunOptions.Current.LeaveMode == LeaveMode.AfterFinishDungeon,
                "initialize must use live session targets and keep normal completion");
        }
        session.CompleteAttempt(FarmingSessionPolicy.Decide(session.Plan, new(31, true, false, false, false)), 1);
        using var nextContext = Context(); using var next = new FarmingScenario(session);
        next.Initialize(nextContext);
        Check(session.NextFloor == 41 && nextContext.RunOptions.Current.BandedEnabled, "next group inherits toggle");
    }
    private static void CompletionBoundaries()
    {
        var early = FarmingSessionPolicy.Decide(Plan(), new(31, false, false, true, false));
        var intermediate = FarmingSessionPolicy.Decide(Plan(), new(31, true, false, true, false));
        var final = FarmingSessionPolicy.Decide(Plan(), new(91, true, false, true, false));
        Check(!early.CountCycle && !intermediate.CountCycle && intermediate.NextFloor == 41 && final.CountCycle, "hoard count cannot complete a climb");
        Check(FarmingSessionPolicy.ReachedStopBoundary(Plan() with { StopAfterFloor = 40 }, new(31, true, false, true, false)), "explicit stop still respected");
    }
    private static void NoFixedMap() { Check(!Plan().ShowsDetailedMap && !Plan(true, 91).ShowsDetailedMap, "free exploration must not inherit 21/31 catalog key"); }
    private static void IntuitionFloors()
    {
        for (int floor = 1; floor <= 90; floor++)
            Check(FreeExplorationHoardPolicy.AllowsNewIntuition(FarmingMode.FreeExploration, floor) == (floor % 10 is >= 1 and <= 7), "first-seven rule");
        Check(FreeExplorationHoardPolicy.AllowsNewIntuition(FarmingMode.Hoard, 28), "fixed hoard mode unaffected");
    }
    private static void HighIntuition()
    {
        for (int floor = 91; floor <= 98; floor++) Check(FreeExplorationHoardPolicy.AllowsNewIntuition(FarmingMode.FreeExploration, floor), "91-98 permitted");
        Check(!FreeExplorationHoardPolicy.AllowsNewIntuition(FarmingMode.FreeExploration, 99) &&
            !FreeExplorationHoardPolicy.AllowsNewIntuition(FarmingMode.FreeExploration, 100), "no boss/reward use");
    }
    private static FloorPlanSnapshot Evidence() => new() { BandedEnabled = true, HoardDistributionHintsOnly = true,
        FloorsetBandedCount = 5, FloorsetHoardOpportunity = FloorsetHoardOpportunity.Maxed };
    private static void PositiveEvidence() => Check(PlanGenerator.ResolveHoardEvidenceState(Evidence() with { ChatSaysHoard = true, UsedIntuitionThisFloor = true }) == HoardEvidenceState.IntuitionWaitingForIndicator, "positive text cannot be lost to count or missing coordinate");
    private static void NegativeEvidence() => Check(PlanGenerator.ResolveHoardEvidenceState(Evidence() with { ChatSaysNoHoard = true }) == HoardEvidenceState.IntuitionNoHoard, "authoritative absence resolves hoard search");
    private static void DistributionHints()
    {
        Check(PlanGenerator.ResolveHoardEvidenceState(Evidence()) == HoardEvidenceState.BlindSearch, "five is only a hint in free mode");
        Check(PlanGenerator.ResolveHoardEvidenceState(Evidence() with { HoardDistributionHintsOnly = false }) == HoardEvidenceState.FloorsetMaxed, "fixed mode unchanged");
    }
    private static void UnknownText() => Check(CurrentIntuitionResolutionPlanner.Decide(new() { UsedIntuitionThisFloor = true, ElapsedMillisecondsSinceUse = 60000, ResolutionWindowMilliseconds = 4500 }).Kind == CurrentIntuitionResolutionKind.Wait, "timeout cannot manufacture absence");
    private static void MissingCandidates() => Check(RoomSearchBuilder.Build(new() { ShouldProbeHoard = true, HoardEvidenceState = HoardEvidenceState.BlindSearch }).Waypoints.Count == 0, "missing data must not invent a coordinate");
    private static void IndicatorFirst()
    {
        var result = RoomSearchBuilder.Build(new() { ShouldProbeHoard = true, CachedHoardIndicatorInRoom = true,
            HoardEvidenceState = HoardEvidenceState.IntuitionDirect, FallbackTrapCount = 50 });
        Check(result.Waypoints.Count == 1 && result.Waypoints[0].Source == RoomSearchWaypointSource.CachedHoardIndicator, "exact indicator replaces blind candidates");
    }
    private static void TimeReserve()
    {
        bool Allowed(int? seconds, FarmingMode mode = FarmingMode.FreeExploration) => FreeExplorationHoardPolicy.AllowsHighFloorDetour(mode, true, 98, seconds);
        Check(!Allowed(null) && !Allowed(600) && Allowed(601) && !Allowed(3600, FarmingMode.DeepProgression), "known budget required and challenge mode unchanged");
        Check(FreeExplorationHoardPolicy.HighFloorReserveSeconds(91) > FreeExplorationHoardPolicy.HighFloorReserveSeconds(98), "reserve remaining floors too");
    }
    private static PassageRouteScope Scope()
    {
        var scope = new PassageRouteScope();
        Check(scope.Initialize(0, 2, new[,] { { 0, 1, 2, 1 }, { 1, 0, 1, 2 }, { 2, 1, 0, 3 }, { 1, 2, 3, 0 } }), "scope setup");
        return scope;
    }
    private static RoomPlanEntry[] Route() => [new(3, true, true, true, HoardEvidenceState.BlindSearch), new(1, false, true)];
    private static void HoardOnlyDetour()
    {
        var route = FreeExplorationHoardPolicy.FilterRoute(Route(), Scope(), true);
        Check(route.Count == 2 && route[0].RoomIndex == 3 && route[0].ShouldProbeHoard &&
            !route[0].ShouldSearchChests && route[1].ShouldSearchChests, "detour preserves treasure but strips ordinary supplies");
    }
    private static void DetourExpiry()
    {
        var scope = Scope();
        var route = FreeExplorationHoardPolicy.FilterRoute(Route(), scope, false);
        Check(route.Count == 1 && route[0].RoomIndex == 1, "budget expiry prunes branch");
        scope.ObserveRoom(2);
        Check(FreeExplorationHoardPolicy.FilterRoute(Route(), scope, false).Count == 0, "passed rooms cannot reappear for supplies");
    }
    private static void ObjectiveOrder()
    {
        var snapshot = new ObjectiveArbiterSnapshot(false, false, true, false, false, false, true, false, true, false, PassageFirst: true);
        Check(ObjectiveArbiter.Decide(snapshot).PrimaryObjective == FloorObjectiveKind.ActivatePassage, "open before high-floor hoard");
        snapshot = snapshot with { PassageOpen = true, CombatInProgress = true };
        Check(ObjectiveArbiter.Decide(snapshot).PrimaryObjective == FloorObjectiveKind.FinishCombatBeforePassage, "clear old hate first");
        snapshot = snapshot with { CombatInProgress = false };
        Check(ObjectiveArbiter.Decide(snapshot).PrimaryObjective == FloorObjectiveKind.CompleteKnownHoard &&
            ObjectiveArbiter.Decide(snapshot).Channels.Transition == CommandChannelPermission.Blocked, "known treasure blocks passage until resolved/excluded");
    }
    private static AutoPilotExecutor Executor(bool free = true)
    {
        var executor = new AutoPilotExecutor(_ => (false, false, false), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        executor.ApplyRunOptions(new() { BandedEnabled = true, HoardDistributionHintsOnly = free });
        Set(executor, "_hasPlanningSnapshot", true);
        Set(executor, "_lastHoardEvidenceState", HoardEvidenceState.IntuitionPending);
        executor.ApplyRetainedRoute(Route());
        return executor;
    }
    private static void OptionalSkip()
    {
        var e = Executor();
        Check(e.TrySkipOptionalHoard("intuition-text-unresolved") && !e.HoardSearchEnabled && e.IsHoardWorkResolved &&
            !e.HasAuthoritativeHoardResolution && !e.IsHoardEvidenceUnstable, "policy skips work without forging a native negative result");
        Check(e.PlannedRoute.All(x => !x.ShouldProbeHoard && !x.ShouldVisitForIntel) &&
            e.PlannedRoute.Any(x => x.ShouldSearchChests), "skip removes hoard but leaves normal chest policy to controller");
    }
    private static void OptionalResume()
    {
        var e = Executor(); e.TrySkipOptionalHoard("intuition-text-unresolved");
        Check(!e.TryResumeOptionalHoard(false) && !e.HoardSearchEnabled, "unknown cannot resume itself");
        Check(e.TryResumeOptionalHoard(true) && e.HoardSearchEnabled && !e.IsHoardWorkResolved, "late current-floor evidence makes work pending again");
        Check(!e.TryResumeOptionalHoard(true), "resume is idempotent");
    }
    private static void OptionalReserve()
    {
        var e = Executor(); e.TrySkipOptionalHoard("intuition-text-unresolved");
        Check(e.TrySkipOptionalHoard("high-floor-time-reserve") && !e.TryResumeOptionalHoard(true) &&
            e.OptionalHoardSkipReason == "high-floor-time-reserve", "positive cannot overrule an exhausted time budget");
        Check(!e.TrySkipOptionalHoard("high-floor-time-reserve"), "no repeated skip/log spam");
    }
    private static void OptionalScope()
    {
        var e = Executor(false);
        Check(!e.TrySkipOptionalHoard("intuition-text-unresolved") && e.HoardSearchEnabled && !e.IsHoardWorkResolved,
            "fixed hoard requirements remain mandatory");
    }
    private static OwnedSaveCleanupWatchdog Watchdog() { var w = new OwnedSaveCleanupWatchdog(); w.Reset(Now); return w; }
    private static void NoFakeProgress()
    {
        var w = Watchdog();
        for (int i = 1; i < 10; i++) { w.Observe(0, Now.AddSeconds(i)); Check(w.Tick(Now.AddSeconds(i)) == CleanupWatchdogDecision.Continue, "grace"); }
        Check(w.Tick(Now.AddSeconds(10)) == CleanupWatchdogDecision.Recover, "repeated requests cannot renew no-progress timer");
    }
    private static void RealProgress()
    {
        var w = Watchdog(); w.Observe(1, Now.AddSeconds(9));
        Check(w.Tick(Now.AddSeconds(10)) == CleanupWatchdogDecision.Continue && w.Tick(Now.AddSeconds(19)) == CleanupWatchdogDecision.Recover, "observed menu is real progress");
    }
    private static void NoUiOscillation()
    {
        var w = Watchdog(); w.Observe(2, Now); w.Observe(1, Now.AddSeconds(8)); w.Observe(2, Now.AddSeconds(9));
        Check(w.Tick(Now.AddSeconds(10)) == CleanupWatchdogDecision.Recover, "old menu/save oscillations are not progress");
    }
    private static void BoundedRecovery()
    {
        var w = Watchdog();
        for (int i = 1; i <= 3; i++) Check(w.Tick(Now.AddSeconds(i * 10)) == CleanupWatchdogDecision.Recover, "bounded retry");
        Check(w.Tick(Now.AddSeconds(40)) == CleanupWatchdogDecision.Fail, "stop after three recoveries");
    }
    private static void ApproachProgress()
    {
        var w = Watchdog(); w.Observe(0, Now, 30); w.Observe(0, Now.AddSeconds(9), 29.5f);
        Check(w.Tick(Now.AddSeconds(10)) == CleanupWatchdogDecision.Recover, "jitter is not approach");
        w = Watchdog(); w.Observe(0, Now, 30); w.Observe(0, Now.AddSeconds(9), 25);
        Check(w.Tick(Now.AddSeconds(10)) == CleanupWatchdogDecision.Continue, "real approach is progress");
    }
    private static void PausedCleanup()
    {
        var w = Watchdog(); w.Tick(Now.AddSeconds(5)); w.Tick(Now.AddSeconds(105), true);
        Check(w.Tick(Now.AddSeconds(109)) == CleanupWatchdogDecision.Continue && w.Tick(Now.AddSeconds(110)) == CleanupWatchdogDecision.Recover, "zoning excluded from active time");
    }
    private static void TotalCleanupBudget()
    {
        var w = Watchdog();
        for (int i = 0; i <= 120; i += 5) { w.Observe(0, Now.AddSeconds(i), 1000 - i * 2); var d = w.Tick(Now.AddSeconds(i)); if (i == 120) Check(d == CleanupWatchdogDecision.Fail, "total budget cannot renew indefinitely"); }
    }
    private sealed class Cleanup : IDeleteSaveFlow
    {
        private RunContext? _context;
        public bool Ready, Error;
        public int Calls;
        public void Prepare(RunContext context) => _context = context;
        public bool Update(IFramework framework) { Calls++; if (Error) _context!.StatusIsError = true; return Ready || Error; }
    }
    private static void Set(object value, string name, object data) => value.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(value, data);
    private static (RunContext, FarmingSession, FarmingScenario) FailedScenario(Cleanup cleanup)
    {
        var plan = Plan(false, 1) with { Mode = FarmingMode.DeepProgression };
        var session = new FarmingSession(plan, 0); var context = Context(); context.FarmingPlan = plan; context.MarkDutyFailed("recorded-death");
        var scenario = new FarmingScenario(session, deleteSaveFlow: cleanup);
        Set(scenario, "_context", context); Set(scenario, "_entered", true); Set(scenario, "_entryFloor", 61); Set(scenario, "_restDone", true);
        return (context, session, scenario);
    }
    private static void FailureCleanup()
    {
        var cleanup = new Cleanup(); var (context, session, scenario) = FailedScenario(cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Check(!scenario.IsComplete && !scenario.CountsAsCycle && session.OwnedSlot == 0, "no retry before cleanup verified");
            cleanup.Ready = true; scenario.Update(null!);
            Check(scenario.IsComplete && !scenario.CountsAsCycle && scenario.RecoversDutyFailure && session.Failures == 1 &&
                session.OwnedSlot == -1 && session.NextFloor == 1 && session.Plan.TargetCycles == 3, "failure retries from one, retains target, never counts success");
        }
    }
    private static void CleanupError()
    {
        var cleanup = new Cleanup { Error = true }; var (context, session, scenario) = FailedScenario(cleanup);
        using (context) using (scenario)
        {
            scenario.Update(null!);
            Check(scenario.IsComplete && context.StatusIsError && !scenario.CountsAsCycle && session.OwnedSlot == 0, "unverified deletion must not release ownership or create a new save");
        }
    }
    private static void RemovedHold()
    {
        var settings = JsonSerializer.Deserialize<CheckpointEntrySettings>("{\"HoldOnFailure\":true,\"Enabled\":true}")!;
        Check(typeof(FarmingPlan).GetProperty("HoldOnFailure") == null && typeof(FsdStartRequest).GetProperty("HoldOnFailure") == null &&
            typeof(CheckpointEntrySettings).GetProperty("HoldOnFailure") == null && settings.Enabled, "legacy unknown JSON is inert and all active properties removed");
    }
    private static void NeedlePriority()
    {
        Check(MobCombatPolicy.Priority(4, 68, 14179) == MobCombatPolicy.Priority(4, 95, 14222), "needle equals wolf");
        var selected = MobCombatPolicy.Select(4, 68, [new(1, 14179, 100, 3, true), new(2, 999, 1, 1, true)], 2);
        Check(selected?.Id == 1, "needle must be actual selected target");
        selected = MobCombatPolicy.Select(4, 68, [new(1, 14179, 100, 3, true), new(3, 14264, 100, 5, true)], 1);
        Check(selected?.Id == 3, "mimic remains higher priority");
    }
    private static void NeedleScope()
    {
        foreach (int floor in new[] { 61, 68, 69 }) Check(MobCombatPolicy.Priority(4, floor, 14179) == 1, "normal group floors");
        Check(MobCombatPolicy.Priority(4, 70, 14179) == 0 && MobCombatPolicy.Priority(3, 68, 14179) == 0, "no boss/other dungeon expansion");
    }
    private static void EmptyVersion()
    {
        Check(DiagnosticMetadata.Read(() => new Version("").ToString()) == null, "bad external version cannot throw");
        Check(DiagnosticMetadata.Read(() => "1.2.3") == "1.2.3" && DiagnosticMetadata.Read(() => null) == null, "valid/null metadata retained");
    }
}
