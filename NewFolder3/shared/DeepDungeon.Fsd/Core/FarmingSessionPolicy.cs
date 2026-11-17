namespace DeepDungeon.Fsd.Core;

public readonly record struct FarmingAttemptResult(int EntryFloor, bool DutyCompleted,
    bool DutyFailed, bool HarvestCompleted, bool Aborted);

public readonly record struct FarmingSessionDecision(bool CountCycle, int NextFloor,
    bool DeleteOwnedSave, bool RecoverFailure, string Error);

public static class FarmingSessionPolicy
{
    public static int NearestCheckpoint(int floor) =>
        floor >= 71 ? 71 : floor >= 51 ? 51 : floor >= 31 ? 31 : floor >= 21 ? 21 : 1;

    public static bool ReachedStopBoundary(FarmingPlan plan, in FarmingAttemptResult result) =>
        plan.IsProgression && plan.StopAfterFloor is { } limit && limit < 100 &&
        result.DutyCompleted && !result.DutyFailed && !result.Aborted && result.EntryFloor + 9 >= limit;

    public static FarmingSessionDecision Decide(FarmingPlan plan, in FarmingAttemptResult result)
    {
        if (plan.Mode == FarmingMode.FinalBoss)
        {
            if (plan.SaveUse != SaveUse.None || plan.StartFloor != 99)
                return new(false, plan.StartFloor, false, false, "尾王模式僅接受無存檔的獨立99層練習；已停止。");
            bool completed = result.EntryFloor == 99 && result.DutyCompleted && !result.DutyFailed && !result.Aborted;
            return new(completed, 99, false, false, completed ? string.Empty : "尾王測試未通關，不計次、不操作存檔。");
        }
        if (plan.SaveUse == SaveUse.None)
            return new(false, result.EntryFloor, false, false, "只有獨立尾王模式可以不使用存檔；已停止，不計完成次數。");
        bool ownsSave = plan.SaveUse == SaveUse.Create;
        if (result.Aborted)
            return new(false, plan.StartFloor, ownsSave && !plan.PreserveExternalSave, false, "本次入本已中止，未計完成次數。");
        if (result.DutyFailed)
            return plan.IsProgression
                ? new(false, plan.Mode == FarmingMode.FreeExploration ? NearestCheckpoint(result.EntryFloor) : 1,
                    ownsSave && (!plan.PreserveExternalSave || plan.DeleteExternalSaveOnFailure), true, string.Empty)
                : new(false, plan.StartFloor, ownsSave, false, "本次攻略失敗，未計完成次數。");
        if (plan.ReusesSave)
            return new(result.HarvestCompleted, 0, false, false,
                result.HarvestCompleted ? string.Empty : "採集未正常收尾，未計完成次數。");
        if (!result.DutyCompleted)
            return new(false, plan.StartFloor, ownsSave && !plan.PreserveExternalSave, false, "本次未通關，未計完成次數。");
        if (plan.IsProgression)
        {
            if (result.EntryFloor is < 1 or > 91 || result.EntryFloor % 10 != 1)
                return new(false, result.EntryFloor, false, false, "攻略起點無效，停止接續。");
            return result.EntryFloor == 91
                ? new(true, plan.Mode == FarmingMode.FreeExploration ? plan.StartFloor : 1,
                    ownsSave && !plan.PreserveExternalSave, false, string.Empty)
                : new(false, result.EntryFloor + 10, false, false, string.Empty);
        }
        return new(true, plan.StartFloor, ownsSave && !plan.PreserveExternalSave, false, string.Empty);
    }
}
