using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private PassageNavigationRecovery? _passageRecovery;
    private bool _passageRecoveryFailureHandled;
    private DateTime? _passageUnknownAt;
    private DateTime? _passageActorWaitAt;

    private PassageNavigationRecovery PassageRecovery
    {
        get
        {
            if (_passageRecovery != null) return _passageRecovery;
            var geometry = new CombatPositionNavigation();
            return _passageRecovery = new(() => geometry.Ready,
                moveHelper.VNav.SimpleMove.PathfindInProgress,
                point => geometry.GroundPoint(point, .2f, 1.6f),
                geometry.FindPath, geometry.Move, _navDriver.Cancel, moveHelper.VNav.Path.Stop, RecordReplayEvent);
        }
    }

    internal void SuspendPassageRecovery()
    { _passageRecovery?.Suspend(); _passageUnknownAt = _passageActorWaitAt = null; }

    private void FailPassageRecovery(string cause = "bounded-path-recovery-exhausted")
    {
        if (_passageRecoveryFailureHandled) return;
        _passageRecoveryFailureHandled = true;
        RecordReplayEvent("passage-navigation-failed", new { floor = Floor, generation = Generation, cause, status = _status });
        const string reason = "傳送門尋路恢復已達上限；本次不計成功";
        _ctx.MarkDutyFailed("passage-navigation-exhausted");
        _navDriver.Cancel();
        if (_ctx.FarmingPlan?.IsProgression == true)
        {
            _ctx.RunOptions.Update(o => o.LeaveMode = DeepDungeon.Fsd.Runtime.LeaveMode.Immediate);
            _ctx.StatusLine = reason + "；將退本並走既有失敗重開流程。";
        }
        else { _ctx.StatusIsError = true; _ctx.StatusLine = reason + "；已停止，請人工檢查。"; }
        _status = _ctx.StatusLine;
    }
}
