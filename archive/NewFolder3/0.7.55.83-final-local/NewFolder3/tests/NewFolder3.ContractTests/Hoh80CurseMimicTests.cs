using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class Hoh80CurseMimicTests
{
    public static int Run()
    {
        (string Name, Action Test)[] tests =
        [
            ("hoh80-track-priority-before81-without-enabling-high-floor-gathering", () => {
                foreach (int floor in new[] { 1, 9, 21, 36, 61, 79, 81, 93, 99 }) {
                    Check(MobCombatPolicy.TrackingEnabled(FarmingMode.FreeExploration, 2, floor), "ordinary HoH must feed priority movement");
                    if (floor < 81) Check(!MobCombatPolicy.UsesControlledHighFloor(FarmingMode.FreeExploration, 2, floor), "do not move the six-enemy gathering policy to low floors");
                }
                Check(!MobCombatPolicy.TrackingEnabled(FarmingMode.FreeExploration, 2, 60), "HoH boss retains original controller");
                Check(MobCombatPolicy.TrackingEnabled(FarmingMode.FreeExploration, 4, 61), "PT existing support retained"); }),
            ("hoh80-purity-combat-permission-is-hoh-only", () => {
                Check(ProgressionItemPolicy.AllowsCombat(7, false, FloorItemUsePurpose.Automatic, 2), "HoH curse recovery must not wait for combat to end");
                foreach (uint dungeon in new uint[] { 1, 3, 4 })
                    Check(!ProgressionItemPolicy.AllowsCombat(7, false, FloorItemUsePurpose.Automatic, dungeon), "other dungeon behavior unchanged");
                Check(!ProgressionItemPolicy.AllowsCombat(13, false, FloorItemUsePurpose.Automatic, 2), "no general combat item bypass"); }),
            ("hoh80-curse-recovery-requires-curse-and-available-stock", () => {
                foreach (bool curse in new[] { false, true }) foreach (bool stock in new[] { false, true })
                    foreach (bool ready in new[] { false, true }) {
                        var d = GeneralAutoPomanderPlanner.Decide(new() { CanAttemptPomanderUse = ready, PurityUsable = stock, HasCurseStatus = curse });
                        Check(d.ShouldUse == (curse && stock && ready), "curse, usable inventory, and request gate all required");
                    } }),
            ("hoh80-curse-recovery-retains-single-request-and-confirmation", () => {
                var s = new FloorItemUseSession(); var now = DateTime.UtcNow;
                var pending = s.Begin(new(FloorItemUseKind.Pomander, 7), FloorItemUsePurpose.Automatic, 2, now, "curse recovery", false, 0, 0, 0, 0);
                Check(s.Pending == pending && pending.CountBeforeDispatch == 2, "dispatch must remain pending until native evidence"); }),
            ("hoh80-mimic-priority-all-normal-hoh-floors", () => {
                foreach (int floor in new[] { 1, 9, 21, 36, 61, 79, 81, 93, 99 })
                    Check(Hoh70FloorPolicy.Priority(FarmingMode.FreeExploration, 2, floor, 0, 7394, 0) >
                        Hoh70FloorPolicy.Priority(FarmingMode.FreeExploration, 2, floor, 8995, 7584, 0), "engaged mimic must be the first fight"); }),
            ("hoh80-mimic-policy-excludes-boss-and-other-dungeons", () => {
                foreach (int floor in new[] { 0, 10, 60, 70, 100 })
                    Check(Hoh70FloorPolicy.Priority(FarmingMode.FreeExploration, 2, floor, 0, 7394, 0) == 0, "boss/result scope unchanged");
                Check(Hoh70FloorPolicy.Priority(FarmingMode.FreeExploration, 4, 61, 0, 7394, 0) == 0, "PT names do not share HoH priority"); }),
            ("hoh80-opened-chest-mimic-stays-first-until-defeated", () => {
                var now = DateTime.UtcNow; var box = new HohMimicChestState();
                box.Begin(1073771888, System.Numerics.Vector3.Zero, now, []);
                Check(box.Observe(now.AddMilliseconds(369), true, [new(1073771921, System.Numerics.Vector3.Zero, true)]), "recorded chest-to-mimic timing");
                var selection = new HohCombatTargetState(); selection.BeginFloor(FarmingMode.FreeExploration, 2, 61);
                var ordinary = new HohCombatCandidate(1073771906, true, true, true, true, true, 0, 34035, 4);
                var mimic = new HohCombatCandidate(box.MimicId, true, true, true, true, false,
                    Hoh70FloorPolicy.Priority(FarmingMode.FreeExploration, 2, 61, 0, 7394, 0), 1000, 9);
                for (int i = 0; i < 30; i++)
                    Check(selection.Select([ordinary, mimic], ordinary.Id, true) == mimic.Id, "preferred/highest-HP ordinary target must not steal focus");
                Check(MobMovementPolicy.Choose(false, mimic.StoneskinPriority > 0, false, false) == MobMovementOwner.Fight, "pause exploration for fight");
                Check(MobMovementPolicy.Choose(true, true, false, false) == MobMovementOwner.Mechanics, "mechanics avoidance remains first");
                Check(selection.Select([ordinary, mimic with { Alive = false }], 0, true) == ordinary.Id, "release focus after confirmed death"); }),
            ("hoh80-idle-mimic-never-steals-an-engaged-fight", () => {
                var s = new HohCombatTargetState(); s.BeginFloor(FarmingMode.FreeExploration, 2, 61);
                var ordinary = new HohCombatCandidate(1, true, true, true, true, true, 0, 10, 1);
                var idle = new HohCombatCandidate(2, true, true, false, true, false, 4, 1000, 2);
                Check(s.Select([ordinary, idle], 2, false) == 1, "priority does not create engagement"); }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private static void Check(bool value, string message)
    { if (!value) throw new Exception(message); }
}
