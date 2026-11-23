using System.Reflection;
using System.Runtime.CompilerServices;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace NewFolder3;

internal static class Hoh65DoorSequenceTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static ObjectiveArbiterSnapshot Snapshot(bool open, bool engaged, bool banded = false,
        bool known = false, bool discover = false, bool chests = false, bool terminal = true) =>
        new(false, banded, known, discover, terminal, open, !open, engaged, !open || engaged, false,
            RequiredChestWork: chests,
            PassageFirst: HohHighFloorPolicy.WaitsForPassage(FarmingMode.FreeExploration, 2, 94, open) || engaged);

    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh65-door-preparation-only91-through99", () => {
                foreach (int floor in Enumerable.Range(1, 100))
                    Check(HohHighFloorPolicy.WaitsForPassage(FarmingMode.FreeExploration, 2, floor, false) == (floor is >=91 and <=99), "scope");
                Check(!HohHighFloorPolicy.WaitsForPassage(FarmingMode.FreeExploration, 4, 94, false), "PT unchanged"); }),
            ("hoh65-open-door-releases-preparation-only", () => {
                foreach (int floor in Enumerable.Range(91, 9))
                    Check(!HohHighFloorPolicy.WaitsForPassage(FarmingMode.FreeExploration, 2, floor, true), "open releases preparation"); }),
            ("hoh65-closed-door-defers-visible-banded-chest", () => {
                var d = ObjectiveArbiter.Decide(Snapshot(false, false, banded: true, chests: true));
                Check(d.PrimaryObjective == FloorObjectiveKind.ActivatePassage && d.Channels.Interaction == CommandChannelPermission.Blocked, "kill to open before chest"); }),
            ("hoh65-closed-door-defers-hoard-search-and-planned-chests", () => {
                foreach (var s in new[] { Snapshot(false,false,known:true,terminal:false), Snapshot(false,false,discover:true,terminal:false), Snapshot(false,false,chests:true) })
                    Check(ObjectiveArbiter.Decide(s).PrimaryObjective == FloorObjectiveKind.ActivatePassage, "no optional route before open"); }),
            ("hoh65-closed-door-engagement-finishes-in-place", () => {
                var d = ObjectiveArbiter.Decide(Snapshot(false,true,banded:true,chests:true));
                Check(d.PrimaryObjective == FloorObjectiveKind.FinishCombatBeforePassage && d.Channels.Interaction == CommandChannelPermission.Blocked && d.Channels.Transition == CommandChannelPermission.Blocked, "current hate precedes other work"); }),
            ("hoh65-open-door-still-finishes-current-hate", () => {
                var d = ObjectiveArbiter.Decide(Snapshot(true,true,banded:true,chests:true));
                Check(d.PrimaryObjective == FloorObjectiveKind.FinishCombatBeforePassage && d.Channels.Interaction == CommandChannelPermission.Blocked && d.Channels.Transition == CommandChannelPermission.Blocked, "door does not drop engagement"); }),
            ("hoh65-open-cleared-door-restores-original-work-priority", () => {
                Check(ObjectiveArbiter.Decide(Snapshot(true,false,banded:true,chests:true)).PrimaryObjective == FloorObjectiveKind.OpenVisibleBandedChest, "banded retained");
                Check(ObjectiveArbiter.Decide(Snapshot(true,false,known:true,terminal:false)).PrimaryObjective == FloorObjectiveKind.CompleteKnownHoard, "known retained");
                Check(ObjectiveArbiter.Decide(Snapshot(true,false,discover:true,terminal:false)).PrimaryObjective == FloorObjectiveKind.DiscoverHoard, "discovery retained");
                Check(ObjectiveArbiter.Decide(Snapshot(true,false,chests:true)).PrimaryObjective == FloorObjectiveKind.OpenPlannedChest, "chest retained"); }),
            ("hoh65-pending-chest-work-prevents-direct-exit", () => {
                var d=ObjectiveArbiter.Decide(Snapshot(true,false,chests:true));
                Check(d.Channels.Transition == CommandChannelPermission.Blocked, "no premature exit");
                Check(ObjectiveArbiter.Decide(Snapshot(true,false)).PrimaryObjective == FloorObjectiveKind.EnterPassage, "exit after original work resolves"); }),
            ("hoh65-controller-adapter-gates-closed-door-and-sidecar", () => {
                var duty = new DutyState();
                typeof(DutyState).GetProperty("DungeonId")!.SetValue(duty, (uint)2);
                typeof(DutyState).GetProperty("Floor")!.SetValue(duty, (byte)94);
                using var context = new RunContext(new FsdSettings(), duty, new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
                context.FarmingPlan = new(FarmingMode.FreeExploration,SaveUse.Create,1,1,false,false,true,true,false);
                var t=typeof(FloorExplorationController);var c=RuntimeHelpers.GetUninitializedObject(t);
                t.GetField("_ctx",Private)!.SetValue(c,context);
                t.GetField("<Kind>k__BackingField",Private)!.SetValue(c,FloorRuntimeKind.Normal);
                t.GetField("<DungeonId>k__BackingField",Private)!.SetValue(c,(uint)2);
                t.GetField("<Floor>k__BackingField",Private)!.SetValue(c,(byte)94);
                Check((bool)t.GetProperty("HohPassagePreparation",Private)!.GetValue(c)!, "production adapter active");
                Check(!(bool)t.GetProperty("AllowsChestSidecarInteraction",Private)!.GetValue(c)!, "sidecar cannot bypass pre-door phase");
                typeof(DutyState).GetProperty("PassageOpen")!.SetValue(duty,true);
                Check(!(bool)t.GetProperty("HohPassagePreparation",Private)!.GetValue(c)!, "open restores normal controller branch"); }),
        ];
        foreach (var (name,test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
}
