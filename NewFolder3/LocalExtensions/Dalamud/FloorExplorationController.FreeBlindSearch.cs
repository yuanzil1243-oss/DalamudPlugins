using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private bool AllowsLocalAutomaticHoardTools => FreeBlindSearchPolicy.AllowsHoardTool(
        _ctx.FarmingPlan, Floor, FloorInitPlanner.IntuitionPomanderSlotIndex, overflow: false);

    private unsafe bool TryUseLocalFreeRangeIntuition(InstanceContentDeepDungeon* dd)
    {
        if (!FreeBlindSearchPolicy.Restricted(_ctx.FarmingPlan) || !AllowsLocalAutomaticHoardTools ||
            Kind != FloorRuntimeKind.Normal || !SnapshotRunOptions().BandedEnabled || !CanAttemptPomanderUse()) return false;
        var floor = _ctx.Knowledge.Floor;
        if (floor == null || floor.HoardAcquired || floor.ChatSaysHoard || floor.ChatSaysNoHoard || floor.InheritedNoHoardInferred ||
            floor.ExactHoardPosition.HasValue || floor.ObservedBandedPositions.Count > 0 ||
            floor.HasCurrentFloorIntuitionUse || _nativeIntuitionActive ||
            !FloorsetHoardDistributionPolicy.AllowsHoardPomander(DeepDungeonFloorsetTracker.GetCurrentOpportunity(Floor)))
            return false;
        return TryUsePomander(FloorInitPlanner.IntuitionPomanderSlotIndex, dd, "free blind range: eager intuition");
    }
}
