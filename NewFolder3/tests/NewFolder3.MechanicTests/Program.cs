using System.Diagnostics;
using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

var now = new DateTime(2026, 11, 11, 10, 24, 59, DateTimeKind.Utc);
var tests = new List<(string, Action)>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
Vector3 V(float x, float z, float y = 0) => new(x, y, z);

tests.Add(("passage-arrival-latches-and-horizontal-jitter-does-not-repath", () =>
{
    var state = new PassageStandstillState();
    Check(state.Update(true, V(.4f, 0), V(0, 0), now), "arrival");
    Check(state.Update(true, V(.7f, .3f), V(0, 0), now.AddSeconds(1)), "jitter");
    Check(!state.Update(true, V(1.3f, 0), V(0, 0), now.AddSeconds(2)), "displacement must recenter");
}));
tests.Add(("passage-no-latch-outside-entry-circle", () =>
    Check(!new PassageStandstillState().Update(true, V(.6f, 0), V(0, 0), now), "too far")));
tests.Add(("passage-combat-or-objective-change-releases", () =>
{
    var state = new PassageStandstillState();
    state.Update(true, V(0, 0), V(0, 0), now);
    Check(!state.Update(false, V(0, 0), V(0, 0), now), "release");
}));
tests.Add(("passage-does-not-latch-another-height-or-invalid-position", () =>
{
    Check(!new PassageStandstillState().Update(true, V(0, 0, 4), V(0, 0), now), "height");
    Check(!new PassageStandstillState().Update(true, V(float.NaN, 0), V(0, 0), now), "nan");
    Check(!new PassageStandstillState().Update(true, V(0, 0, float.NaN), V(0, 0), now), "nan height");
}));
tests.Add(("passage-timeout-recenters-without-per-frame-reacquisition", () =>
{
    var state = new PassageStandstillState();
    state.Update(true, V(0, 0), V(0, 0), now);
    Check(!state.Update(true, V(0, 0), V(0, 0), now.AddSeconds(8)), "timeout");
    Check(!state.Update(true, V(0, 0), V(0, 0), now.AddSeconds(8.1)), "cooldown");
    Check(state.Update(true, V(0, 0), V(0, 0), now.AddSeconds(9)), "retry");
}));
tests.Add(("passage-floor-reset-discards-latch", () =>
{
    var state = new PassageStandstillState();
    state.Update(true, V(0, 0), V(0, 0), now); state.Reset();
    Check(!state.Holding, "reset");
}));
tests.Add(("pt50-sequence-uses-real-events-and-deduplicates", () =>
{
    var state = new Pt50MechanicState();
    state.ObserveCast(43534, new(-300, -303), 3, now);
    state.ObserveCast(43534, new(-300, -303), 2, now.AddSeconds(1));
    Check(state.NextHitAt == now.AddSeconds(3) && state.RemainingHits == 4, "cast must not restart");
    state.ObserveAction(43534, 1, new(-300, -303), now.AddSeconds(3));
    Check(!state.ObserveAction(43534, 1, new(-300, -303), now.AddSeconds(3)), "duplicate");
    Check(state.RemainingHits == 3 && !state.FirstHit, "first resolution");
    state.ObserveAction(43535, 2, new(-295, -303), now.AddSeconds(5.1));
    state.ObserveAction(43535, 3, new(-292, -299), now.AddSeconds(7.2));
    state.ObserveAction(43536, 4, new(-298, -293), now.AddSeconds(9.3));
    Check(!state.ChaseActive && state.SandActive, "four hits complete");
}));
tests.Add(("pt50-missing-events-are-detected-without-synthetic-hits", () =>
{
    var state = new Pt50MechanicState(); state.ObserveCast(43534, new(-300, -300), 3, now);
    Check(state.IsStale(now.AddSeconds(5.1)) && state.RemainingHits == 4, "stale");
}));
tests.Add(("pt50-wind-is-distinct-and-requires-resolution", () =>
{
    var state = new Pt50MechanicState(); state.ObserveCast(43538, new(-300, -300), 8, now);
    Check(state.WindAt == now.AddSeconds(8) && !state.ChaseActive, "wind");
    state.ObserveAction(43538, 8, new(-300, -300), now.AddSeconds(8));
    Check(state.WindAt == default && state.WindResolvedAt == now.AddSeconds(8), "wind resolution");
    state.Reset(); Check(!state.SandActive && state.WindAt == default, "reset");
}));
tests.Add(("pt50-every-generated-point-is-inside-a-rock-with-margin", () =>
{
    Check(Pt50IslandRoutePlanner.Points.Count > 30, "point coverage");
    foreach (var p in Pt50IslandRoutePlanner.Points)
        Check(Pt50IslandRoutePlanner.RockAt(p.Position, Pt50IslandRoutePlanner.RockMargin) == p.Rock, "point outside");
    Check(Pt50IslandRoutePlanner.Points.Select(p => p.Rock).Distinct().Count() == 7, "seven rocks");
}));
tests.Add(("pt50-prepositions-replay-start-before-complete-four-hop-route", () =>
{
    var player = new Vector2(-299.73682f, -303.4051f);
    var preparation = Pt50IslandRoutePlanner.PreparationPoint(player, now);
    Check(preparation != null && Vector2.Distance(player, preparation.Value.Position) < 2, "prepare on same rock");
    player = preparation!.Value.Position;
    var circle = player;
    var timer = Stopwatch.StartNew();
    var route = Pt50IslandRoutePlanner.Plan(player, circle, true, 4, 2.93, now);
    Console.WriteLine($"  planner: {timer.ElapsedMilliseconds}ms; {route?.Steps.Count ?? 0} steps");
    Check(route != null, "no route from replay start");
    for (int i = 0; i < route!.Steps.Count; ++i)
    {
        var next = route.Steps[i];
        Check(Pt50IslandRoutePlanner.TravelTime(player, next.Position) <= (i == 0 ? 2.93 : 2.1) - .25, "late landing");
        if (i != 0) circle = Pt50IslandRoutePlanner.Pursue(circle, next.Position);
        Check(Vector2.Distance(circle, next.Position) >= 6.6, "AOE overlaps landing");
        Console.WriteLine($"    {i+1}: rock {next.Rock+1}, {next.Position}, travel {Pt50IslandRoutePlanner.TravelTime(player,next.Position):F2}s");
        player = next.Position;
    }
    Check(route.Steps.Count == 4, "no complete route from replay start");
}));
tests.Add(("pt50-failed-replay-mid-sand-does-not-invent-reachable-escape", () =>
{
    var route = Pt50IslandRoutePlanner.Plan(new(-295.5f, -304.65f), new(-295.98f, -304.95f), false, 1, .1, now,
        initialSinking: 3.5);
    Check(route == null, "impossible deadline");
}));
tests.Add(("pt50-long-sand-crossing-is-rejected", () =>
    Check(double.IsPositiveInfinity(Pt50IslandRoutePlanner.TravelTime(new(-315, -315), new(-285, -285))), "sinking")));
tests.Add(("pt50-falling-rock-conflict-is-rejected-at-resolution", () =>
{
    var danger = new[] { new Pt50DangerCircle(new(-294, -310), 5, now.AddSeconds(1)) };
    Check(!Pt50IslandRoutePlanner.PathAvoids(new(-300, -305), new(-294, -310), now, 1, danger), "AOE");
}));
tests.Add(("pt50-wind-entry-is-sand-and-inside-reduced-arena", () =>
{
    foreach (var rock in Pt50IslandRoutePlanner.Points.GroupBy(p => p.Rock).Select(g => g.First()))
    {
        var sand = Pt50IslandRoutePlanner.WindSandPoint(rock.Position);
        Check(sand.HasValue && Pt50IslandRoutePlanner.RockAt(sand.Value) < 0, "must be sand");
        float radius = Vector2.Distance(sand!.Value, new(-300, -300));
        Check(radius is >= 4 and <= 13.8f, "wind arena");
        Check(Pt50IslandRoutePlanner.NearestRock(sand.Value, now) != null, "return after wind");
    }
}));
tests.Add(("pt50-all-seven-prepared-rocks-have-complete-routes", () =>
{
    foreach (var group in Pt50IslandRoutePlanner.Points.GroupBy(p => p.Rock))
    {
        var start = group.OrderBy(p => Vector2.DistanceSquared(p.Position, new(-300,-300))).First().Position;
        var route = Pt50IslandRoutePlanner.Plan(start, start, true, 4, 2.8, now);
        Check(route?.Steps.Count == 4, $"rock {group.Key+1} has no four-hit route");
    }
}));
tests.Add(("pt50-wind-arena-rejects-center-hole-crossing", () =>
{
    Check(!Pt50IslandRoutePlanner.InsideWindArena(new(-310,-300), new(-290,-300)), "center hole");
    Check(!Pt50IslandRoutePlanner.InsideWindArena(new(-310,-300), new(-316,-300)), "outer wall");
    Check(Pt50IslandRoutePlanner.InsideWindArena(new(-310,-300), new(-300,-310)), "peripheral hop");
}));
tests.Add(("pt50-chase-after-wind-remains-inside-reduced-arena", () =>
{
    var start = Pt50IslandRoutePlanner.PreparationPoint(new(-300,-304), now, reducedArena: true)!.Value.Position;
    var route = Pt50IslandRoutePlanner.Plan(start, start, true, 4, 2.8, now, reducedArena: true);
    Check(route?.Steps.Count == 4, "post-wind route");
    foreach (var step in route!.Steps)
    {
        Check(Pt50IslandRoutePlanner.InsideWindArena(start, step.Position), "illegal arena crossing");
        start = step.Position;
    }
}));
tests.Add(("movement-override-failed-bmr-disable-never-grants-ownership", () =>
{
    var controller = new FsdBossMechanicsController(command => command != "/bmrai off");
    var snapshot = new DeepDungeonStateSnapshot(true, true, true, 4, 50, DeepDungeonFloorKind.Boss, false, 1);
    controller.Update(true, snapshot, new(), FarmingMode.DeepProgression);
    Check(controller.IsEnabled && !controller.SetInternalMovementOverride(true) && controller.IsEnabled, "disable failed");
}));
tests.Add(("movement-override-excludes-bmr-until-release-and-stop", () =>
{
    var commands = new List<string>();
    var controller = new FsdBossMechanicsController(command => { commands.Add(command); return true; });
    var snapshot = new DeepDungeonStateSnapshot(true, true, true, 4, 49, DeepDungeonFloorKind.Mob, false, 1);
    controller.Update(true, snapshot, new(), FarmingMode.DeepProgression);
    Check(controller.IsEnabled && controller.SetInternalMovementOverride(true) && !controller.IsEnabled, "acquire");
    int before = commands.Count;
    controller.Update(true, snapshot, new(), FarmingMode.DeepProgression);
    Check(commands.Count == before && !controller.IsEnabled, "BMR must remain disabled");
    controller.SetInternalMovementOverride(false);
    controller.Update(true, snapshot, new(), FarmingMode.DeepProgression);
    Check(controller.IsEnabled && commands[^1] == "/bmrai on", "release");
    controller.SetInternalMovementOverride(true); controller.Stop();
    Check(!controller.IsEnabled && commands[^1] == "/bmrai off", "stop");
}));
tests.Add(("pt50-feature-can-be-disabled-and-persisted", () =>
{
    var settings = new FsdBossMechanicsSettings(); Check(settings.Pt50IslandMovementEnabled, "default on");
    settings.Pt50IslandMovementEnabled = false;
    var copy = System.Text.Json.JsonSerializer.Deserialize<FsdBossMechanicsSettings>(System.Text.Json.JsonSerializer.Serialize(settings));
    Check(copy?.Pt50IslandMovementEnabled == false, "persist off");
}));
tests.Add(("checkpoint-menu-matches-whole-floor-in-reordered-menu", () =>
{
    string[] options = ["从第21层开始", "从第31层开始", "从第1层开始", "从第51层开始", "从第71层开始", "取消"];
    foreach (int start in PilgrimsTraverseCheckpointPolicy.NewSaveStarts)
    {
        Check(PilgrimsTraverseCheckpointPolicy.SelectOption(options, start, out int index) == CheckpointMenuResult.Selected,
            $"missing {start}");
        Check(index == Array.IndexOf(options, $"从第{start}层开始"), "wrong checkpoint");
    }
}));
tests.Add(("checkpoint-missing-or-ambiguous-entry-is-not-option-zero", () =>
{
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["第1层", "第21层", "取消"], 31, out int index) ==
        CheckpointMenuResult.Missing && index == -1, "locked checkpoint");
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["第31层", "第31层"], 31, out index) ==
        CheckpointMenuResult.Ambiguous && index == -1, "ambiguous checkpoint");
}));
tests.Add(("checkpoint-menu-recognizes-localized-labels-and-ignores-other-dialogs", () =>
{
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["第３１層から", "取消"], 31, out int i) ==
        CheckpointMenuResult.Selected && i == 0, "fullwidth");
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["Enter from floor 31", "Cancel"], 31, out i) ==
        CheckpointMenuResult.Selected && i == 0, "English");
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["建立新存檔", "取消"], 31, out _) ==
        CheckpointMenuResult.NotFloorset, "save creation dialog");
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["存档1", "存档2"], 1, out _) ==
        CheckpointMenuResult.NotFloorset, "unrelated numbers");
}));
tests.Add(("checkpoint-range-only-uses-supported-starts-and-ten-floor-endings", () =>
{
    Check(PilgrimsTraverseCheckpointPolicy.IsStopFloor(31, 50), "31-50");
    foreach (var pair in new[] { (11, 50), (41, 50), (31, 30), (31, 49), (31, 110) })
        Check(!PilgrimsTraverseCheckpointPolicy.IsStopFloor(pair.Item1, pair.Item2), "invalid range");
    foreach (int start in PilgrimsTraverseCheckpointPolicy.NewSaveStarts)
        Check(PilgrimsTraverseCheckpointPolicy.IsStopFloor(start, start + 9), "own group");
}));
tests.Add(("checkpoint-ui-request-is-opt-in-and-does-not-affect-other-modes", () =>
{
    var settings = new CheckpointEntrySettings();
    Check(settings.CreateRequest(FarmingMode.FreeExploration, FsdStartScenario.Farming).DiagnosticStartFloor == 31, "free exploration starts with the configured checkpoint");
    settings.Enabled = true;
    var request = settings.CreateRequest(FarmingMode.FreeExploration, FsdStartScenario.Farming);
    Check(request.DiagnosticStartFloor == 31 && request.StopAfterFloor == 50 && request.ResumeSlot == null, "test request");
    settings.ResumeSlot = 1;
    settings.StartFloor = 51;
    settings.StopAfterFloor = 100;
    var resume = settings.CreateRequest(FarmingMode.FreeExploration, FsdStartScenario.Farming);
    Check(resume.ResumeSlot == 1 && resume.DiagnosticStartFloor == null && resume.StopAfterFloor == 100,
        "existing-save request carries only slot and stop boundary");
    Check(settings.CreateRequest(FarmingMode.DeepProgression, FsdStartScenario.Farming).DiagnosticStartFloor == null, "challenge stays on floor one");
    Check(settings.CreateRequest(FarmingMode.Hoard, FsdStartScenario.Farming).DiagnosticStartFloor == null, "hoard unchanged");
    Check(settings.CreateRequest(FarmingMode.FreeExploration, FsdStartScenario.ControlledSurvey).DiagnosticStartFloor == null, "survey unchanged");
    settings.Enabled = false;
    Check(settings.CreateRequest(FarmingMode.FreeExploration, FsdStartScenario.Farming).StopAfterFloor == null, "switch back to free exploration default");
}));
tests.Add(("checkpoint-ui-preferences-persist-without-changing-normal-plan-rule", () =>
{
    var settings = new FarmingSettings { CheckpointEntry = new() { Enabled = true, StartFloor = 51, StopAfterFloor = 80, ResumeSlot = 1 } };
    var copy = System.Text.Json.JsonSerializer.Deserialize<FarmingSettings>(System.Text.Json.JsonSerializer.Serialize(settings));
    Check(copy?.CheckpointEntry is { Enabled: true, StartFloor: 51, StopAfterFloor: 80, ResumeSlot: 1 }, "persist");
    Check(!FarmingPlan.TryCreate(FarmingMode.DeepProgression, SaveUse.Create, 31, 1, false,
        false, true, true, false, out _, out _), "normal challenge must still require floor 1");
}));
tests.Add(("checkpoint-cn-holy-road-labels-select-31-instead-of-first-option", () =>
{
    string[] options = ["从第1圣路开始", "从第21圣路开始", "从第31圣路开始", "从第51圣路开始", "从第71圣路开始", "取消"];
    foreach (int start in PilgrimsTraverseCheckpointPolicy.NewSaveStarts)
    {
        Check(PilgrimsTraverseCheckpointPolicy.SelectOption(options, start, out int i) == CheckpointMenuResult.Selected, $"missing {start}");
        Check(i == Array.IndexOf(options, $"从第{start}圣路开始"), "wrong holy road");
    }
}));
tests.Add(("checkpoint-traditional-and-fullwidth-holy-road-labels", () =>
{
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["從第１聖路開始", "從第３１聖路開始"], 31, out int i) ==
        CheckpointMenuResult.Selected && i == 1, "traditional holy road");
}));
tests.Add(("checkpoint-locked-cn-31-never-falls-back-to-1", () =>
{
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["第1圣路", "第21圣路", "取消"], 31, out int i) ==
        CheckpointMenuResult.Missing && i == -1, "locked holy road must not be generic dialog");
}));
tests.Add(("checkpoint-numeric-floor-labels-are-recognized-even-with-unsupported-start", () =>
{
    Check(PilgrimsTraverseCheckpointPolicy.SelectOption(["第11圣路", "取消"], 31, out int i) ==
        CheckpointMenuResult.Missing && i == -1, "unknown floor menu must not select first option");
}));

tests.Add(("next-floor-items-require-an-actual-normal-floor-and-plan-continuation", () =>
{
    Check(ProgressionItemPolicy.CanBenefitNextFloor(4, 48, 50), "48 to 49");
    foreach (int floor in new[] { 9, 19, 29, 39, 49, 59, 69, 79, 89, 98, 99, 100 })
        Check(!ProgressionItemPolicy.CanBenefitNextFloor(4, floor, null), $"invalid destination after {floor}");
    Check(!ProgressionItemPolicy.CanBenefitNextFloor(4, 31, 31), "stopping boundary");
    Check(!ProgressionItemPolicy.CanBenefitNextFloor(0, 31, null), "unknown dungeon");
    Check(ProgressionItemPolicy.AffectsNextFloor(4) && ProgressionItemPolicy.AffectsNextFloor(5) &&
        ProgressionItemPolicy.AffectsNextFloor(6) && !ProgressionItemPolicy.AffectsNextFloor(8), "fortune affects current floor");
}));
tests.Add(("both-entry-and-overcap-affluence-refuse-boss-destination", () =>
{
    var init = new FloorInitSnapshot { CanAttemptPomanderUse = true, AffluenceUsable = true };
    Check(!FloorInitPlanner.Decide(init).ShouldUse, "entry before boss");
    Check(FloorInitPlanner.Decide(init with { NextFloorIsMob = true }).SlotIndex == 4, "entry before normal");
    var snapshot = new GeneralAutoPomanderSnapshot { CanAttemptPomanderUse = true, AffluenceUsable = true,
        FlightUsable = true, AllowStatusOverlap = true };
    Check(!GeneralAutoPomanderPlanner.ShouldUseSlot(snapshot, 4) &&
        !GeneralAutoPomanderPlanner.ShouldUseSlot(snapshot, 5), "overcap cannot bypass next floor");
}));
tests.Add(("combat-buff-overcap-is-allowed-on-normal-floors-without-enabling-routine-waste", () =>
{
    var reserve = FarmingItemPolicy.For(FarmingMode.DeepProgression);
    foreach (uint slot in new uint[] { 2, 3, 11 })
    {
        Check(ProgressionItemPolicy.AllowsCombat(slot, false, FloorItemUsePurpose.GoldChestOvercap), "combat overcap");
        Check(ProgressionItemPolicy.AllowsCombat(slot, true, FloorItemUsePurpose.Automatic), "boss combat");
        Check(!ProgressionItemPolicy.AllowsCombat(slot, false, FloorItemUsePurpose.Automatic), "ordinary automatic gate");
        Check(reserve.AllowsCombatBuff(3, false, true) && !reserve.AllowsCombatBuff(3, false), "reserve exception");
    }
    Check(!ProgressionItemPolicy.AllowsCombat(4, false, FloorItemUsePurpose.GoldChestOvercap), "not blanket combat permission");
}));
tests.Add(("serenity-is-reserved-except-three-serenity-with-a-real-overcap", () =>
{
    Check(SilverChestOvercapPolicy.ReliefStone(1, 0, 2, false, false) == 1, "fate first");
    Check(SilverChestOvercapPolicy.ReliefStone(0, 1, 2, false, false) == 2, "wisdom before serenity");
    Check(SilverChestOvercapPolicy.ReliefStone(0, 0, 3, true, true) == 3, "three serenity relief");
    Check(SilverChestOvercapPolicy.ReliefStone(0, 0, 2, false, false) == 0, "not full");
    Check(SilverChestOvercapPolicy.ReliefStone(1, 0, 2, true, false) == 0, "no useless clear after passage open");
}));
tests.Add(("dropped-chests-only-allow-a-short-forward-detour", () =>
{
    Check(SupplyRoutePolicy.OnWay(new(0,0), new(5,1), new(10,0)), "forward corridor");
    Check(!SupplyRoutePolicy.OnWay(new(0,0), new(-1,0), new(10,0)), "behind player");
    Check(!SupplyRoutePolicy.OnWay(new(0,0), new(5,10), new(10,0)), "long detour");
    Check(!SupplyRoutePolicy.OnWay(new(0,0), new(5,0), new(0,0)), "unknown forward path");
    Check(SupplyRoutePolicy.RemainingNormalFloorsInSet(31) == 8 &&
        SupplyRoutePolicy.RemainingNormalFloorsInSet(49) == 0 && SupplyRoutePolicy.RemainingNormalFloorsInSet(71) == 8, "budget only this ten-floor set");
    Check(!SupplyRoutePolicy.HasTime(1000, 8) && SupplyRoutePolicy.HasTime(1200, 8), "reserve subsequent floors and boss");
}));
tests.Add(("pt50-partial-route-can-save-next-hit-and-late-route-is-rejected", () =>
{
    var start = Pt50IslandRoutePlanner.PreparationPoint(new(-300,-304), now)!.Value.Position;
    var one = Pt50IslandRoutePlanner.Plan(start, start, true, 1, 2.8, now);
    Check(one?.Steps.Count == 1, "reachable next hit");
    Check(Pt50IslandRoutePlanner.ValidateNextStep(one, start, start, true, 2.8, now) == "", "partial route is usable");
    Check(Pt50IslandRoutePlanner.ValidateNextStep(one, start, start, true, .1, now) == "landing-too-late", "expired result");
    var hazard = new[] { new Pt50DangerCircle(one!.Steps[0].Position, 5, now.AddSeconds(2)) };
    Check(Pt50IslandRoutePlanner.ValidateNextStep(one, start, start, true, 2.8, now, hazard) == "falling-rock-conflict", "new hazard");
}));
tests.Add(("pt50-recovery-prefers-a-safe-point-on-the-current-island", () =>
{
    var player = new Vector2(-293, -293);
    int rock = Pt50IslandRoutePlanner.RockAt(player);
    Check(rock >= 0, "replay rock");
    var hazard = new[] { new Pt50DangerCircle(new(-288, -293), 5, now.AddSeconds(2)) };
    var landing = Pt50IslandRoutePlanner.PreparationPoint(player, now, hazard);
    Check(landing?.Rock == rock, "move within same island instead of crossing sand");
    Check(Pt50IslandRoutePlanner.PathAvoids(player, landing!.Value.Position, now,
        Pt50IslandRoutePlanner.TravelTime(player, landing.Value.Position), hazard), "avoid falling rock");
}));
tests.Add(("pt50-replay-emergency-rock-does-not-cross-center-hole", () =>
{
    var player = new Vector2(-287.109375f, -301.09604f);
    var recovery = Pt50IslandRoutePlanner.PreparationPoint(player, now, reducedArena: true);
    Check(recovery != null && recovery.Value.Rock == 0, "keep east island instead of BMR center crossing");
    Check(Pt50IslandRoutePlanner.InsideWindArena(player, recovery!.Value.Position), "hole and outer wall");
}));

tests.Add(("fortune-before-fate-waits-for-effect-after-last-stock-is-consumed", () =>
{
    var state = new FortuneBeforeFateState();
    Check(state.Decide(false, false, 1, true, now) == FortunePreparation.RequestFortune, "prepare fortune");
    state.RecordDispatch();
    Check(state.Decide(false, true, 1, true, now.AddSeconds(1)) == FortunePreparation.Wait, "pending request");
    Check(state.Decide(false, false, 0, true, now.AddSeconds(2)) == FortunePreparation.Wait, "consumption alone cannot allow fate");
    Check(state.Decide(true, false, 0, true, now.AddSeconds(3)) == FortunePreparation.Ready, "effect active then fate");
}));
tests.Add(("fortune-before-fate-does-not-duplicate-requests-and-has-bounded-wait", () =>
{
    var state = new FortuneBeforeFateState();
    state.Decide(false, false, 3, true, now); state.RecordDispatch();
    Check(state.Decide(false, false, 3, true, now.AddSeconds(3)) == FortunePreparation.Wait, "no duplicate fortune");
    Check(state.Decide(false, false, 3, true, now.AddSeconds(9)) == FortunePreparation.Ready, "unconfirmed effect cannot stall forever");
}));
tests.Add(("fortune-before-fate-missing-forbidden-or-already-active-does-not-block", () =>
{
    var state = new FortuneBeforeFateState();
    Check(state.Decide(false, false, 0, true, now) == FortunePreparation.Ready, "missing");
    Check(state.Decide(false, false, 3, false, now) == FortunePreparation.Ready, "forbidden");
    Check(state.Decide(true, false, 3, true, now) == FortunePreparation.Ready, "active");
    Check(state.StartedAt == null, "no artificial window extension");
}));

tests.Add(("dropped-chest-batch-in-searched-room-gets-a-new-objective-identity", () =>
{
    using var session = new FloorObjectiveSession(7);
    var key = new RoomObjectiveKey(3, RoomObjectiveCategory.Chests, FloorObjectiveKind.OpenPlannedChest);
    var old = session.GetOrCreateObjective(key, key.Kind, false);
    session.ObjectiveLedger.ApplyOutcome(old.Identity, ObjectiveOutcomeKind.Succeeded);
    var added = session.GetOrCreateObjective(key with { Batch = 1 }, key.Kind, false);
    Check(added.Identity != old.Identity && added.Outcome == ObjectiveOutcomeKind.Pending, "fresh work after successful search");
    Check(session.GetRoomProgress(3).ChestsSearched, "preserve completed initial search");
    Check(session.ObjectiveLedger.ApplyOutcome(added.Identity, ObjectiveOutcomeKind.Succeeded).Status ==
        ObjectiveOutcomeApplyStatus.Accepted, "new chest batch can finish independently");
}));

tests.AddRange(EndgameRegressionTests.Cases());
tests.AddRange(Run30RegressionTests.Cases());
tests.AddRange(Run31RegressionTests.Cases());
tests.AddRange(Run32RegressionTests.Cases());
tests.AddRange(Run34RegressionTests.Cases());
int passed = 0;
foreach (var (name, test) in tests) { test(); ++passed; Console.WriteLine("PASS " + name); }
Console.WriteLine($"Mechanic tests: {passed} passed");
