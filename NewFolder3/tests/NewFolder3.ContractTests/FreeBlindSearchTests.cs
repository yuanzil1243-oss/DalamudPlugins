using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace NewFolder3;

internal static class FreeBlindSearchTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  free-blind-" + name); }
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            int max = dungeon == 1 ? 200 : 100;
            Check("inclusive-range-exact-work-and-overflow-dungeon-" + dungeon, () =>
            {
                var plan = Plan(dungeon, true, 51, 59);
                var options = new RunOptions { BandedEnabled = true, OpenGold = true, OpenSilver = true, OpenBronze = true };
                for (int floor = 1; floor <= max; floor++)
                {
                    bool inside = floor is >= 51 and <= 59;
                    var work = FloorWorkPolicyBuilder.Build(plan, options, floor);
                    Require(work.SearchHoard && work.AllowBlindHoardSearch == inside);
                    Require(work.OpenGold && work.OpenSilver && work.OpenBronze);
                    Require(FreeBlindSearchPolicy.AllowsHoardTool(plan, floor, FloorInitPlanner.IntuitionPomanderSlotIndex, false) == inside);
                    Require(FreeBlindSearchPolicy.AllowsHoardTool(plan, floor, FloorInitPlanner.IntuitionPomanderSlotIndex, true));
                    Require(FreeBlindSearchPolicy.AllowsHoardTool(plan, floor, FloorInitPlanner.SightPomanderSlotIndex, true) == inside);
                    Require(HoardEvidencePolicy.Resolve(new() { BandedEnabled = work.SearchHoard,
                        ExactHoardObserved = true, FloorsetHoardOpportunity = FloorsetHoardOpportunity.Possible }) == HoardEvidenceState.IntuitionDirect);
                }
                var off = FloorWorkPolicyBuilder.Build(plan, new RunOptions { BandedEnabled = false }, 55);
                Require(!off.SearchHoard);
            });
            Check("legacy-free-exploration-default-keeps-all-floors-" + dungeon, () =>
            {
                var old = Plan(dungeon, false, 0, 0);
                for (int floor = 1; floor <= max; floor++)
                    Require(FreeBlindSearchPolicy.AllowsBlind(old, floor) &&
                        FreeBlindSearchPolicy.AllowsHoardTool(old, floor, FloorInitPlanner.IntuitionPomanderSlotIndex, false));
                var preferences = JsonSerializer.Deserialize<FreeExplorationPreferences>("{\"StartFloor\":1,\"EndFloor\":" + max + "}")!;
                Require(!preferences.BlindSearchRangeEnabled);
            });
            Check("settings-plan-and-pause-persistence-" + dungeon, () =>
            {
                var settings = new FarmingSettings { DungeonId = dungeon };
                var preferences = settings.GetFreeExplorationPreferences();
                preferences.BlindSearchRangeEnabled = true; preferences.BlindSearchStartFloor = 51; preferences.BlindSearchEndFloor = 59;
                var restored = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(settings))!.GetFreeExplorationPreferences();
                Require(restored.BlindSearchRangeEnabled && restored.BlindSearchStartFloor == 51 && restored.BlindSearchEndFloor == 59);
                var plan = JsonSerializer.Deserialize<FarmingPlan>(JsonSerializer.Serialize(Plan(dungeon, true, 51, 59)))!;
                Require(FreeBlindSearchPolicy.Restricted(plan) && FreeBlindSearchPolicy.Inside(plan, 55));
                Require(FloorsetPausePolicy.Supports(plan));
                Require(!FreeBlindSearchPolicy.AllowsBlind(plan, 50) && !FreeBlindSearchPolicy.AllowsBlind(plan, 60));
            });
            Check("ordinary-descent-and-necromancer-completion-unchanged-" + dungeon, () =>
            {
                foreach (bool ordinary in new[] { false, true })
                {
                    int start = ordinary ? OrdinaryDescentPolicy.HighestStartFloor(dungeon) : 1;
                    Require(FarmingPlan.TryCreate(FarmingMode.DeepProgression, SaveUse.Create, start, 3, true,
                        false, true, true, false, out var p, out _, dungeon, ordinary));
                    var irrelevant = p! with { FreeBlindRangeEnabled = true, FreeBlindStartFloor = 51, FreeBlindEndFloor = 59 };
                    for (int floor = 1; floor <= max; floor++)
                    {
                        Require(!FreeBlindSearchPolicy.Restricted(irrelevant) && FreeBlindSearchPolicy.AllowsBlind(irrelevant, floor));
                        Require(FreeBlindSearchPolicy.AllowsHoardTool(irrelevant, floor, FloorInitPlanner.IntuitionPomanderSlotIndex, false));
                        var before = FloorWorkPolicyBuilder.Build(p, new RunOptions(), floor);
                        var after = FloorWorkPolicyBuilder.Build(irrelevant, new RunOptions(), floor);
                        Require(before == after);
                    }
                    foreach (bool failed in new[] { false, true })
                    {
                        var result = new FarmingAttemptResult(max - 9, !failed, failed, false, false);
                        Require(FarmingSessionPolicy.Decide(p!, result) == FarmingSessionPolicy.Decide(irrelevant, result));
                    }
                    Require(RelicOffenseItemPolicy.Decide(p, new(dungeon, start, true, true, true,
                        true, false, true, false, true, false, true, false, 2, true, 2)) == RelicOffenseItem.None);
                }
            });
        }
        Check("invalid-start-end-and-unknown-dungeon-rejected", () =>
        {
            foreach (var (start, end) in new[] { (0, 20), (60, 59), (1, 101) })
                Require(!FreeExplorationPolicy.TryCreate(4, 1, 100, 1, false, new(true, true, true, true), false, false,
                    out _, out _, true, start, end));
            Require(!FreeExplorationPolicy.TryCreate(999, 1, 100, 1, false, new(true, true, true, true), false, false,
                out _, out _, true, 1, 50));
        });
        Check("single-floor-range-and-default-end", () =>
        {
            var plan = Plan(1, true, 56, 56);
            Require(FreeBlindSearchPolicy.AllowsBlind(plan, 56) && !FreeBlindSearchPolicy.AllowsBlind(plan, 55));
            var all = Plan(1, true, 1, 0);
            Require(all.FreeBlindEndFloor == 200 && FreeBlindSearchPolicy.AllowsBlind(all, 199));
        });
        Check("relic-mode-ignores-free-only-fields", () =>
        {
            Require(RelicFarmingPolicy.TryCreate(2, SaveUse.Create, 21, 1, false, new(true, true, true, true), out var relic, out _));
            var withFields = relic! with { FreeBlindRangeEnabled = true, FreeBlindStartFloor = 51, FreeBlindEndFloor = 59 };
            Require(FreeBlindSearchPolicy.AllowsBlind(withFields, 21));
        });
        return count;
    }
    private static FarmingPlan Plan(uint dungeon, bool enabled, int start, int end)
    {
        Require(FreeExplorationPolicy.TryCreate(dungeon, 1, dungeon == 1 ? 200 : 100, 2, false,
            new(true, true, true, true), false, false, out var plan, out _, enabled, start, end));
        return plan!;
    }
    private static void Require(bool condition)
    { if (!condition) throw new InvalidOperationException("Free blind-search range regression failed."); }
}
