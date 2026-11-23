using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace NewFolder3;

internal static class Hoh52ChestOvercapTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type ControllerType = typeof(FloorExplorationController);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh52-floor29-steel-keeps-real-controller-chest-task", () => {
                using var f = new Fixture(2, 29, 3);
                f.Recover(); f.AssertPreserved();
                Check(f.Chest.PendingGoldOvercapSlotIndex == 3, "steel relief must reach the existing item dispatch flow");
            }),
            ("hoh52-floor32-petrification-keeps-real-controller-chest-task", () => {
                using var f = new Fixture(2, 32, 15);
                f.Recover(); f.AssertPreserved();
                Check(f.Chest.PendingGoldOvercapSlotIndex == 15, "petrification cap cannot disappear during objective adoption");
            }),
            ("hoh52-floor39-unused-witching-reaches-existing-policy-skip", () => {
                using var f = new Fixture(2, 39, HohItemPolicy.Witching);
                Check(!GeneralAutoPomanderPlanner.ShouldUseSlot(new() { CanAttemptPomanderUse = true }, HohItemPolicy.Witching),
                    "shared full-chest policy does not waste witching just to free space");
                f.Recover(); f.AssertPreserved(); f.AssertExistingPolicySkip();
            }),
            ("hoh52-no-use-items-stay-rejected-by-the-shared-planner", () => {
                foreach (uint slot in new uint[] { 6, HohItemPolicy.Witching, HohItemPolicy.Concealment })
                    Check(!GeneralAutoPomanderPlanner.ShouldUseSlot(new() { CanAttemptPomanderUse = true }, slot),
                        "unsupported full items must still go to policy skip, not a new consumption fallback");
            }),
            ("hoh52-magicite-uses-the-same-objective-retention", () => {
                using var f = new Fixture(2, 58, null);
                f.Chest.PendingHohMagiciteOvercap = true;
                f.Recover(); f.AssertPreserved();
                Check(f.Chest.PendingHohMagiciteOvercap, "silver chest relief survives too");
            }),
            ("hoh52-after-consumption-keeps-original-chest-until-looted", () => {
                using var f = new Fixture(2, 99, null);
                f.Chest.HohRelief.ConfirmConsumption(2);
                f.Recover(); f.AssertPreserved();
                Check(f.Chest.HohRelief.AwaitingLoot && f.PendingRelief, "door priority cannot discard original chest after spending");
                Check(!f.Chest.HohRelief.CanRelieve(true, 3, false), "retention must not enable a second consumption");
                f.Chest.HohRelief.ObserveLoot(3, false);
                Check(!f.PendingRelief, "actual reacquisition releases the override");
            }),
            ("hoh52-recorded-375-cap-updates-do-not-restart-the-task", () => {
                using var f = new Fixture(2, 29, 3);
                var deadline = f.Chest.OvercapReliefDeadline;
                for (int i = 0; i < 375; i++) { f.Recover(); f.AssertPreserved(); }
                Check(f.Chest.OvercapReliefDeadline == deadline, "updates never renew the bounded relief deadline");
            }),
            ("hoh52-petrification-relief-keeps-the-closed-door-objective-stable", () => {
                using var f = new Fixture(2, 32, 15);
                for (int i = 0; i < 207; i++)
                {
                    var decision = ObjectiveArbiter.Decide(new(false, false, false, false, true,
                        false, true, true, true, true, RecoverActiveChest: f.PendingRelief));
                    Check(decision.PrimaryObjective == FloorObjectiveKind.OpenPlannedChest, "cannot flip back to ActivatePassage");
                    f.Recover(); f.AssertPreserved();
                }
            }),
            ("hoh52-pt-isolation-does-not-retain-hoh-relief-flags", () => {
                using var f = new Fixture(4, 29, 3);
                f.Chest.PendingHohMagiciteOvercap = true;
                f.Chest.HohRelief.ConfirmConsumption(2);
                Check(!f.PendingRelief, "HoH flags must not affect PT");
                f.AssertOriginalPreemption();
            }),
            ("hoh52-palace-isolation-keeps-original-objective-preemption", () => {
                using var f = new Fixture(1, 32, 15);
                Check(!f.PendingRelief, "palace must not acquire the HoH override");
                f.AssertOriginalPreemption();
            }),
            ("hoh52-uncapped-hoh-chest-keeps-original-objective-preemption", () => {
                using var f = new Fixture(2, 32, null);
                Check(!f.PendingRelief, "no native cap opportunity means no retention");
                f.AssertOriginalPreemption();
            }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    // Exercise the production controller lifecycle without loading the game's native item API.
    // Intercept ordinary preemption at its first event; all preservation assertions use real
    // ObjectiveExecution, ChestInteractionAttempt and WaypointTaskRunner instances.
    private sealed class Fixture : IDisposable
    {
        private readonly RunContext _context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        private readonly FloorExplorationController _controller;
        private readonly object _execution;
        private readonly Type _executionType;
        private readonly WaypointTaskRunner _runner;
        private readonly RoomWaypoint _waypoint;
        internal readonly ChestInteractionAttempt Chest;
        private int _aborts;
        private readonly List<string> _events = new();
        internal bool PendingRelief => (bool)ControllerType.GetProperty("HasPendingHohRelief", Instance)!.GetValue(_controller)!;

        internal Fixture(uint dungeon, byte floor, uint? slot)
        {
            _controller = (FloorExplorationController)RuntimeHelpers.GetUninitializedObject(ControllerType);
            var navigation = new NavigationHelper(new Navigator(), () => false, () => DateTime.Now, _ => { }, _ => { }, () => false);
            Set("_ctx", _context); Set("_navHelper", navigation);
            Set("<DungeonId>k__BackingField", dungeon); Set("<Floor>k__BackingField", floor);
            Set("RecordReplayEvent", (Action<string, object>)((name, _) => {
                _events.Add(name);
                if (name == "room-search-aborted") { _aborts++; throw new PreemptionReached(); }
            }));
            _executionType = ControllerType.GetNestedType("ObjectiveExecution", BindingFlags.NonPublic)!;
            _execution = Activator.CreateInstance(_executionType, Instance, null,
                [FloorObjectiveKind.ActivatePassage, navigation, null!], null)!;
            ControllerType.GetProperty("ActiveExecution", Instance)!.SetValue(_controller, _execution);
            _waypoint = new(new Vector3(10, 0, 10), slot.HasValue ? RoomObjectiveType.ChestGold : RoomObjectiveType.ChestSilver);
            Chest = new(_waypoint) { EntityId = 42, PendingGoldOvercapSlotIndex = slot, OvercapReliefDeadline = DateTime.UtcNow.AddSeconds(15) };
            _executionType.GetProperty("ActiveWaypoint")!.SetValue(_execution, _waypoint);
            _executionType.GetProperty("ChestAttempt")!.SetValue(_execution, Chest);
            _runner = (WaypointTaskRunner)_executionType.GetProperty("TaskRunner")!.GetValue(_execution)!;
            typeof(WaypointTaskRunner).GetField("_phase", Instance)!.SetValue(_runner, TaskPhase.WaitingPost);
        }

        private void Set(string name, object value) => ControllerType.GetField(name, Instance)!.SetValue(_controller, value);
        internal void Recover() => Check((bool)ControllerType.GetMethod("EnsureObjectiveExecution", Instance)!
            .Invoke(_controller, [FloorObjectiveKind.OpenPlannedChest])!, "objective adoption must succeed");
        internal void AssertPreserved()
        {
            Check(ReferenceEquals(_execution, ControllerType.GetProperty("ActiveExecution", Instance)!.GetValue(_controller)), "keep exact execution");
            Check(ReferenceEquals(Chest, _executionType.GetProperty("ChestAttempt")!.GetValue(_execution)), "keep original chest");
            Check(_runner.Phase == TaskPhase.WaitingPost && _aborts == 0, "do not reset or abort the existing task");
            Check((RoomWaypoint?)_executionType.GetProperty("ActiveWaypoint")!.GetValue(_execution) == _waypoint, "keep waypoint");
        }
        internal void AssertOriginalPreemption()
        {
            try { Recover(); throw new Exception("unexpected HoH retention outside a pending HoH transaction"); }
            catch (TargetInvocationException e) when (e.InnerException is PreemptionReached) { Check(_aborts == 1, "old preemption path reached exactly once"); }
        }
        internal unsafe void AssertExistingPolicySkip()
        {
            var next = new RoomWaypoint(new Vector3(20, 0, 10), RoomObjectiveType.ChestGold);
            var room = (RoomSearchContext)Activator.CreateInstance(typeof(RoomSearchContext), Instance, null,
                [1, Vector3.Zero, 30f, new List<RoomWaypoint> { _waypoint, next }, true, true, false, false, 0, 0, 0], null)!;
            var executor = new AutoPilotExecutor(_ => (false, false, false), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
            typeof(AutoPilotExecutor).GetField("_roomContext", Instance)!.SetValue(executor, room);
            Set("<Executor>k__BackingField", executor);
            Set("<ObjectEvidence>k__BackingField", new FloorObjectEvidenceTracker());
            // The unchanged TryHandleGoldChestOvercap rejection branch performs these
            // two operations before calling the same production skip handler used by PT.
            Chest.PendingGoldOvercapSlotIndex = null;
            _runner.Reset(cancelNavigation: false);
            ControllerType.GetMethod("HandleTaskSkip", Instance)!.Invoke(_controller,
                [Pointer.Box((void*)0, typeof(InstanceContentDeepDungeon*)), null!, "GoldChestOvercapSkip",
                 WaypointOutcomeKind.PolicySkipped, 0d, TaskPhase.WaitingPost]);
            Check(!PendingRelief && ControllerType.GetProperty("ActiveChestAttempt", Instance)!.GetValue(_controller) == null,
                "existing policy skip must clear the active full chest transaction");
            Check(executor.CurrentWaypoint == next && room.CurrentWaypointIndex == 1,
                "advance to the next task instead of opening the skipped chest again");
            Check(room.BuildOutcomeSnapshot(false).Chests.SkippedCount == 1 && _events.Contains("waypoint-skipped"),
                "record policy skip rather than an unresolved retry");
            Check(!room.AppendLiveChest(_waypoint.Position, _waypoint.Type), "live scan must not append the same skipped chest again");
            Check(_aborts == 0 && !Chest.HohRelief.Consumed, "skip without restarting or consuming anything");
        }
        public void Dispose() => _context.Dispose();
    }
    private sealed class PreemptionReached : Exception { }
    private sealed class Navigator : INavigator
    {
        public bool PathfindAndMoveTo(Vector3 dest, bool alignCamera) => true;
        public bool RepathTo(Vector3 dest, bool alignCamera) => true;
        public void CancelAll() { }
    }
}
