using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private bool TrySkipOptionalHoard(string reason)
    {
        if (_executor?.CanSkipOptionalHoard(reason) != true) return false;
        // Cancel through the objective ledger before pruning its route. A pending chest
        // confirmation may refuse preemption and must settle before we try again.
        if (HasActiveSearchExecution() && !StopActiveSearchExecution(FloorObjectiveKind.ActivatePassage)) return false;
        if (!_executor.TrySkipOptionalHoard(reason)) return false;
        ClearBandedRevealExpectation(reason);
        RequestPlanRefresh(reason);
        return true;
    }

    private bool FreeHoardEnabled => FreeExplorationHoardPolicy.Enabled(_ctx.FarmingPlan?.Mode,
        _ctx.RunOptions.Current.BandedEnabled);
    private bool FreeHoardNoHoard => FreeHoardEnabled && _chatWatchers?.ChatSaysNoHoard == true;
    private bool FreeHoardAdvance => FreeHoardNoHoard || FreeHoardEnabled && _executor?.OptionalHoardSkipReason != null;
    private bool AllowHoardDetour => !FreeHoardAdvance && FreeExplorationHoardPolicy.AllowsHighFloorDetour(_ctx.FarmingPlan?.Mode,
        _ctx.RunOptions.Current.BandedEnabled, Floor, _ctx.Duty.TimerRemainingSeconds);
    private FloorsetHoardOpportunity HoardOpportunity(byte floor) => FreeHoardEnabled
        ? FloorsetHoardOpportunity.Possible : DeepDungeon.Fsd.Dalamud.GameState.DeepDungeonFloorsetTracker.GetCurrentOpportunity(floor);
    private bool AllowNewHoardIntuition(int floor) => FreeExplorationHoardPolicy.AllowsNewIntuition(_ctx.FarmingPlan?.Mode, floor);
    private IReadOnlyList<RoomPlanEntry> FilterPassageExplorationRoute(IReadOnlyList<RoomPlanEntry> entries) =>
        FreeHoardAdvance ? Array.Empty<RoomPlanEntry>() :
        FreeExplorationHoardPolicy.FilterRoute(entries, _passageRouteScope, AllowHoardDetour);
}
