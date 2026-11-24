using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

internal static class LocalRunOptions
{
    internal static void Apply(RunContext context)
    {
        if (context.FarmingPlan is { FreeExploration: true } plan)
            context.RunOptions.Update(o => o.LocalAggressiveChestInteraction = plan.FreeAggressiveChestInteraction);
    }
}
