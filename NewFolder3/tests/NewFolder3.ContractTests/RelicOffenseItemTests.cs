using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class RelicOffenseItemTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  relic-offense-" + name); }
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            int start = dungeon == 1 ? 51 : 21;
            Require(RelicFarmingPolicy.TryCreate(dungeon, SaveUse.Create, start, 2, false,
                new(true, true, true, true), out var plan, out _));
            var available = new RelicOffenseItemSnapshot(dungeon, start, true, true, true,
                true, false, true, false, true, false, true, false, 2, true, 2);
            Check("single-available-strength-is-spent-" + dungeon, () =>
                Require(RelicOffenseItemPolicy.Decide(plan, available) == RelicOffenseItem.Strength));
            Check("active-strength-allows-dungeon-specific-item-" + dungeon, () =>
            {
                var expected = dungeon == 2 ? RelicOffenseItem.Frailty : dungeon == 4 ? RelicOffenseItem.Haste : RelicOffenseItem.None;
                Require(RelicOffenseItemPolicy.Decide(plan, available with { StrengthActive = true }) == expected);
                Require(RelicOffenseItemPolicy.Decide(plan, available with { StrengthUsable = false }) == expected);
            });
            Check("one-frailty-per-floor-and-no-cross-dungeon-slot-" + dungeon, () =>
            {
                var s = available with { StrengthActive = true, HasteActive = true, JuniperActiveOrUsed = true };
                Require(RelicOffenseItemPolicy.Decide(plan, s) == (dungeon == 2 ? RelicOffenseItem.Frailty : RelicOffenseItem.None));
                Require(RelicOffenseItemPolicy.Decide(plan, s with { FrailtyActiveOrUsed = true }) == RelicOffenseItem.None);
            });
            Check("juniper-only-for-pt-and-once-per-floor-" + dungeon, () =>
            {
                var s = available with { StrengthActive = true, HasteActive = true, FrailtyActiveOrUsed = true };
                Require(RelicOffenseItemPolicy.Decide(plan, s) == (dungeon == 4 ? RelicOffenseItem.Juniper : RelicOffenseItem.None));
                Require(RelicOffenseItemPolicy.Decide(plan, s with { JuniperActiveOrUsed = true }) == RelicOffenseItem.None);
                Require(RelicOffenseItemPolicy.Decide(plan, s with { JuniperUsable = false }) == RelicOffenseItem.None);
                Require(RelicOffenseItemPolicy.Decide(plan, s with { JuniperCount = 1 }) == RelicOffenseItem.None);
            });
            Check("channel-exit-boss-and-other-mode-boundaries-" + dungeon, () =>
            {
                foreach (var blocked in new[] { available with { CanAttempt = false },
                    available with { NormalFloor = false }, available with { Floor = start - 1 },
                    available with { Floor = start + 10 }, available with { Dungeon = 999 } })
                    Require(RelicOffenseItemPolicy.Decide(plan, blocked) == RelicOffenseItem.None);
                Require(RelicOffenseItemPolicy.Decide(null, available) == RelicOffenseItem.None);
                Require(RelicOffenseItemPolicy.Decide(plan! with { RelicMaterial = false }, available) == RelicOffenseItem.None);
                Require(RelicOffenseItemPolicy.Decide(plan! with { FreeExploration = true }, available) == RelicOffenseItem.None);
            });
            Check("strength-and-haste-immediate-without-combat-or-stock-reserve-" + dungeon, () =>
            {
                Require(RelicOffenseItemPolicy.Decide(plan, available with { NeedsCombat = false }) == RelicOffenseItem.Strength);
                var haste = available with { NeedsCombat = false, StrengthActive = true, FrailtyReady = false };
                Require(RelicOffenseItemPolicy.Decide(plan, haste) == (dungeon == 4 ? RelicOffenseItem.Haste : RelicOffenseItem.None));
            });
            Check("floor-reset-enables-next-frailty-or-juniper-" + dungeon, () =>
            {
                var s = available with { Floor = start + 1, StrengthActive = true, HasteActive = true };
                Require(RelicOffenseItemPolicy.Decide(plan, s) == (dungeon == 2 ? RelicOffenseItem.Frailty :
                    dungeon == 4 ? RelicOffenseItem.Juniper : RelicOffenseItem.None));
            });
        }
        Check("hoh-only-one-frailty-held-for-boss", () =>
        {
            var plan = HohPlan();
            var stock = Frailty(21, 1);
            for (int floor = 21; floor <= 29; floor++)
                Require(RelicOffenseItemPolicy.Decide(plan, stock with { Floor = floor, FrailtyReady = true }) == RelicOffenseItem.None);
            Require(RelicOffenseItemPolicy.Decide(plan, stock with { Floor = 30, NormalFloor = false }) == RelicOffenseItem.Frailty);
            Require(RelicOffenseItemPolicy.Decide(plan, stock with { Floor = 30, NormalFloor = false, FrailtyActiveOrUsed = true }) == RelicOffenseItem.None);
        });
        for (int floor = 21; floor <= 29; floor++)
        {
            int acquired = floor;
            Check("hoh-second-frailty-acquired-on-" + floor, () =>
            {
                var tracker = new HohRelicFrailtyReserve();
                Require(!tracker.Observe(acquired, 1));
                bool ready = tracker.Observe(acquired, 2);
                Require(ready == (acquired == 29));
                Require(tracker.NextSpendFloor == (acquired == 29 ? 29 : acquired + 1));
                var snapshot = Frailty(acquired, 2) with { FrailtyReady = ready };
                Require(RelicOffenseItemPolicy.Decide(HohPlan(), snapshot) ==
                    (acquired == 29 ? RelicOffenseItem.Frailty : RelicOffenseItem.None));
                int spend = acquired == 29 ? 29 : acquired + 1;
                Require(tracker.Observe(spend, 2));
                Require(RelicOffenseItemPolicy.Decide(HohPlan(), snapshot with { Floor = spend, FrailtyReady = true }) == RelicOffenseItem.Frailty);
                Require(!tracker.Observe(spend, 1));
                Require(RelicOffenseItemPolicy.Decide(HohPlan(), snapshot with { Floor = spend, FrailtyCount = 1, FrailtyReady = true }) == RelicOffenseItem.None);
            });
        }
        Check("hoh-delayed-or-blocked-use-keeps-the-next-floor-request", () =>
        {
            var tracker = new HohRelicFrailtyReserve();
            Require(!tracker.Observe(22, 2));
            Require(tracker.Observe(23, 2) && tracker.Observe(23, 3) && tracker.Observe(24, 3));
            Require(!tracker.Observe(21, 2) && tracker.NextSpendFloor == 22);
        });
        Check("hoh-stock-acquired-just-before-transfer-is-ready-on-arrival", () =>
        {
            var tracker = new HohRelicFrailtyReserve();
            Require(!tracker.Observe(21, 1));
            Require(tracker.Observe(22, 2) && tracker.NextSpendFloor == 22);
            Require(!tracker.Observe(22, 1));
            Require(!tracker.Observe(22, 2) && tracker.NextSpendFloor == 23);
        });
        Check("hoh-third-stock-still-preserves-one-and-no-double-use-on-same-floor", () =>
        {
            var tracker = new HohRelicFrailtyReserve();
            Require(!tracker.Observe(21, 3));
            Require(tracker.Observe(22, 3));
            Require(RelicOffenseItemPolicy.Decide(HohPlan(), Frailty(22, 3) with { FrailtyReady = true }) == RelicOffenseItem.Frailty);
            Require(RelicOffenseItemPolicy.Decide(HohPlan(), Frailty(22, 2) with { FrailtyReady = true, FrailtyActiveOrUsed = true }) == RelicOffenseItem.None);
            Require(tracker.Observe(23, 2));
            Require(!tracker.Observe(23, 1));
        });
        Check("existing-general-status-and-ban-protection-kept", () =>
        {
            var s = new GeneralAutoPomanderSnapshot { CanAttemptPomanderUse = true, StrengthUsable = true };
            Require(GeneralAutoPomanderPlanner.Decide(s).SlotIndex == FloorInitPlanner.StrengthPomanderSlotIndex);
            Require(!GeneralAutoPomanderPlanner.Decide(s with { HasStrengthStatus = true }).ShouldUse);
            Require(!GeneralAutoPomanderPlanner.Decide(s with { CanAttemptPomanderUse = false }).ShouldUse);
            Require(!DeepDungeonFloorItemUsePolicy.CanUsePomander(1, FloorInitPlanner.StrengthPomanderSlotIndex));
            Require(!DeepDungeonFloorItemUsePolicy.CanUsePomander(1, ProgressionItemPolicy.FrailtySlot));
            Require(!DeepDungeonFloorItemUsePolicy.CanUseStones(4, 7));
        });
        Check("pt-juniper-reserve-also-applies-to-silver-overflow", () =>
        {
            Require(RelicFarmingPolicy.TryCreate(4, SaveUse.Create, 21, 1, false,
                new(true, true, true, true), out var plan, out _));
            Require(!RelicOffenseItemPolicy.AllowsNormalJuniper(plan, 1));
            Require(RelicOffenseItemPolicy.AllowsNormalJuniper(plan, 2));
            Require(!RelicOffenseItemPolicy.AllowsNormalJuniper(plan, 0));
            Require(RelicOffenseItemPolicy.AllowsNormalJuniper(plan! with { RelicMaterial = false }, 1));
            Require(FarmingItemPolicy.For(plan!.Mode).AllowsBossSerenity(1, true, 0, false));
        });
        Check("pt-passage-incense-remains-available-on-each-ordinary-floor", () =>
        {
            for (int floor = 21; floor <= 29; floor++)
            {
                Require(FarmingItemPolicy.For(FarmingMode.Hoard).AllowsEntryIncense(true, 0));
                var available = new NaturalPassageAccelerationSnapshot(true, false, 1, false, 1, false, true, true, true, true);
                Require(NaturalPassageAccelerationPolicy.Decide(available) == NaturalPassageAccelerationAction.DispatchPoisonfruit);
                Require(NaturalPassageAccelerationPolicy.Decide(available with { PoisonfruitStock = 0 }) == NaturalPassageAccelerationAction.DispatchMazeroot);
                Require(NaturalPassageAccelerationPolicy.Decide(available with { PassageOpen = true }) == NaturalPassageAccelerationAction.None);
                Require(NaturalPassageAccelerationPolicy.Decide(available with { PoisonfruitAttemptedThisFloor = true }) == NaturalPassageAccelerationAction.None);
                Require(NaturalPassageAccelerationPolicy.Decide(available with { MazerootAttemptedOrAdopted = true }) == NaturalPassageAccelerationAction.None);
            }
        });
        return count;
    }
    private static FarmingPlan HohPlan()
    {
        Require(RelicFarmingPolicy.TryCreate(2, SaveUse.Create, 21, 1, false,
            new(true, true, true, true), out var plan, out _));
        return plan!;
    }
    private static RelicOffenseItemSnapshot Frailty(int floor, int count) =>
        new(2, floor, true, true, true, false, true, true, false, false, false, false, false, count, false);
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Relic offense item regression failed."); }
}
