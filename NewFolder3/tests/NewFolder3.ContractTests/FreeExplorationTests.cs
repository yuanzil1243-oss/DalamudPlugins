using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace NewFolder3;

internal static class FreeExplorationTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(string name, Action action) { action(); Console.WriteLine("  ok  free-" + name); passed++; }
        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            int max = dungeon == 1 ? 200 : 100;
            foreach (int start in FreeExplorationPolicy.NewSaveStarts(dungeon))
                Check($"all-boundaries-{dungeon}-{start}", () =>
                {
                    for (int end = start + 9; end <= max; end += 10)
                    {
                        Assert(FreeExplorationPolicy.TryCreate(dungeon, start, end, 3, false,
                            new(true, true, true, true), true, false, out var plan, out _));
                        Assert(plan!.FreeExploration && plan.Mode == FarmingMode.DeepProgression &&
                            plan.StopAfterFloor == null && !plan.StopsForTerminalReview);
                        for (int floor = start; floor <= end - 9; floor += 10)
                        {
                            var result = FarmingSessionPolicy.Decide(plan, new(floor, true, false, false, false));
                            Assert(result.Error == "" && !result.RecoverFailure);
                            Assert(result.CountCycle == (floor + 9 == end));
                            Assert(result.DeleteOwnedSave == result.CountCycle);
                            Assert(result.NextFloor == (result.CountCycle ? start : floor + 10));
                            Assert(!FarmingSessionPolicy.ReachedStopBoundary(plan, new(floor, true, false, false, false)));
                            var failure = FarmingSessionPolicy.Decide(plan, new(floor, true, true, false, false));
                            Assert(!failure.CountCycle && failure.RecoverFailure && failure.NextFloor == start);
                            var abort = FarmingSessionPolicy.Decide(plan, new(floor, true, false, false, true));
                            Assert(!abort.CountCycle && !abort.DeleteOwnedSave && abort.Error.Length > 0);
                        }
                    }
                });
            Check($"reject-invalid-ranges-{dungeon}", () =>
            {
                for (int start = 0; start <= max + 1; start++)
                    for (int end = 0; end <= max + 1; end++)
                    {
                        bool expected = FreeExplorationPolicy.NewSaveStarts(dungeon).Contains(start) &&
                            end >= start + 9 && end <= max && end % 10 == 0;
                        Assert(FreeExplorationPolicy.TryCreate(dungeon, start, end, 1, false,
                            default, false, false, out _, out _) == expected);
                    }
            });
            Check($"external-range-no-deletion-{dungeon}", () =>
            {
                for (int start = 1; start <= max - 9; start += 10)
                {
                    Assert(FreeExplorationPolicy.TryCreate(dungeon, start, max, 1, false,
                        default, false, true, out var plan, out _));
                    for (int floor = start; floor <= max - 9; floor += 10)
                        for (int flags = 0; flags < 16; flags++)
                        {
                            var decision = FarmingSessionPolicy.Decide(plan!, new(floor,
                                (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0));
                            Assert(!decision.DeleteOwnedSave && !decision.RecoverFailure);
                        }
                    Assert(!FreeExplorationPolicy.TryCreate(dungeon, start, max, 2, false,
                        default, false, true, out _, out _));
                    Assert(!FreeExplorationPolicy.TryCreate(dungeon, start, max, 1, true,
                        default, false, true, out _, out _));
                }
            });
        }
        Check("pt21-50-three-full-cycles", () =>
        {
            Assert(FreeExplorationPolicy.TryCreate(4, 21, 50, 3, false, default, false, false, out var plan, out _));
            var session = new FarmingSession(plan!);
            int cycles = 0;
            for (int groups = 0; groups < 9; groups++)
            {
                var decision = FarmingSessionPolicy.Decide(plan!, new(session.NextFloor, true, false, false, false));
                session.CompleteAttempt(decision, 0);
                if (decision.CountCycle) cycles++;
                Assert(cycles == (groups + 1) / 3);
            }
            Assert(cycles == 3 && session.NextFloor == 21);
        });
        Check("all-target-combinations-reach-floor-policy", () =>
        {
            for (int flags = 0; flags < 16; flags++)
            {
                FarmingTargets targets = new((flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0);
                Assert(FreeExplorationPolicy.TryCreate(4, 21, 50, 1, true, targets, false, false, out var plan, out _));
                var session = new FarmingSession(plan!);
                Assert(session.TrySetTargets(targets));
                var policy = FloorWorkPolicyBuilder.Build(plan, new RunOptions { BandedEnabled = session.BandedEnabled,
                    OpenGold = session.OpenGold, OpenSilver = session.OpenSilver, OpenBronze = session.OpenBronze });
                Assert(policy.SearchHoard == targets.Hoard && policy.OpenGold == targets.Gold &&
                    policy.OpenSilver == targets.Silver && policy.OpenBronze == targets.Bronze);
                Assert(!policy.HarvestChests && !policy.DiscoveryOnly);
                var original = plan! with { FreeExploration = false };
                Assert(!FloorWorkPolicyBuilder.Build(original, new RunOptions { BandedEnabled = true }).SearchHoard);
            }
        });
        Check("separate-dungeon-mode-and-aggressive-preferences", () =>
        {
            var settings = new FsdSettings();
            settings.Farming.Mode = FarmingMode.DeepProgression;
            var original = settings.Farming.GetPreferences(settings); original.Cycles = 7;
            settings.Farming.FreeExploration = true;
            var free = settings.Farming.GetFreeExplorationPreferences();
            free.Cycles = 3; free.OpenBronze = true; free.AggressiveChestInteraction = true;
            Assert(ReferenceEquals(free, settings.Farming.GetPreferences(settings)));
            Assert(!settings.AggressiveChestInteraction);
            settings.Farming.DungeonId = 1;
            Assert(settings.Farming.GetFreeExplorationPreferences().Cycles == 1);
            var restored = JsonSerializer.Deserialize<FsdSettings>(JsonSerializer.Serialize(settings))!;
            restored.Farming.DungeonId = 4;
            Assert(restored.Farming.GetFreeExplorationPreferences().Cycles == 3 &&
                restored.Farming.GetFreeExplorationPreferences().AggressiveChestInteraction);
            restored.Farming.FreeExploration = false;
            Assert(restored.Farming.GetPreferences(restored).Cycles == 7);
            var provider = new RunOptionsProvider(new RunOptions { LocalAggressiveChestInteraction = true });
            Assert(provider.Current.Copy().LocalAggressiveChestInteraction == true);
            provider.Set(new RunOptions());
            Assert(provider.Current.LocalAggressiveChestInteraction == null);
        });
        Check("reject-invalid-job-independent-inputs", () =>
        {
            Assert(!FreeExplorationPolicy.TryCreate(3, 1, 100, 1, false, default, false, false, out _, out _));
            Assert(!FreeExplorationPolicy.TryCreate(4, 21, 30, 0, true, default, false, false, out _, out _));
        });
        return passed;
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Free exploration contract failed."); }
}
