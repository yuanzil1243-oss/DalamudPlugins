namespace DeepDungeon.Fsd.Core;

public sealed partial record FarmingPlan
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FreeExploration { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? EndFloor { get; init; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public bool FreeAggressiveChestInteraction { get; init; }
}

/// <summary>Range orchestration only. The upstream progression/combat/entry/reward controllers remain in charge.</summary>
public static class FreeExplorationPolicy
{
    public static int[] NewSaveStarts(uint dungeon) => dungeon switch
    {
        1 => [1, 51], 2 => [1, 21], 4 => [1, 21, 31, 51, 71], _ => []
    };

    public static bool TryCreate(uint dungeon, int start, int end, int cycles, bool infinite,
        FarmingTargets targets, bool aggressive, bool external, out FarmingPlan? plan, out string error,
        bool blindSearchRangeEnabled = false, int blindSearchStartFloor = 1, int blindSearchEndFloor = 0)
    {
        plan = null;
        int max = dungeon == 1 ? 200 : 100;
        if (blindSearchEndFloor == 0) blindSearchEndFloor = max;
        error = dungeon is not (1 or 2 or 4) ? "不支援此地宮。"
            : start < 1 || start > max - 9 || start % 10 != 1 ? "起點須為可入場的十層組起點。"
            : !external && !NewSaveStarts(dungeon).Contains(start) ? "此樓層不是原版可新建起點；其他起點須選擇對應已有存檔。"
            : end < start + 9 || end > max || end % 10 != 0 ? $"終點須為起點之後的十層組末，且不超過 {max} 層。"
            : cycles < 1 ? "循環次數必須至少為 1。"
            : blindSearchRangeEnabled && (blindSearchStartFloor < 1 || blindSearchEndFloor < blindSearchStartFloor || blindSearchEndFloor > max)
                ? $"盲踩層數須介於 1–{max} 層，且起始層不可高於最終層。"
            : external && (cycles != 1 || infinite) ? "已有存檔只續打一次並保檔停止，不能刪檔循環。"
            : string.Empty;
        if (error.Length > 0) return false;
        plan = new(FarmingMode.DeepProgression, SaveUse.Create, start, cycles, infinite,
            targets.Hoard, targets.Gold, targets.Silver, targets.Bronze)
        {
            DungeonId = dungeon, FreeExploration = true, EndFloor = end,
            FreeAggressiveChestInteraction = aggressive, ExternalSave = external,
            FreeBlindRangeEnabled = blindSearchRangeEnabled,
            FreeBlindStartFloor = blindSearchRangeEnabled ? blindSearchStartFloor : 0,
            FreeBlindEndFloor = blindSearchRangeEnabled ? blindSearchEndFloor : 0
        };
        return true;
    }

    public static FarmingSessionDecision Decide(FarmingPlan plan, in FarmingAttemptResult result)
    {
        bool owns = TaskContinuationPolicy.CanDeleteSave(plan);
        if (plan.EndFloor is not { } end || end < plan.StartFloor + 9 || end > plan.MaxFloor || end % 10 != 0 ||
            result.EntryFloor < TaskContinuationPolicy.MinimumFloor(plan) || result.EntryFloor + 9 > end || result.EntryFloor % 10 != 1)
            return new(false, result.EntryFloor, false, false, "自由探索層段不符；保留存檔並停止。");
        if (TaskContinuationPolicy.PreserveSave(plan) && (result.DutyFailed || result.Aborted))
            return new(false, result.EntryFloor, false, false, "已有存檔未正常完成；保檔停止，不自動重開。");
        if (result.Aborted)
            return new(false, plan.StartFloor, false, false, "自由探索已中止；未計完成輪數，保留存檔。");
        if (result.DutyFailed)
            return new(false, plan.StartFloor, owns, owns, string.Empty);
        if (!result.DutyCompleted)
            return new(false, result.EntryFloor, false, false, "未觀測到通關；保留存檔並停止。");
        return result.EntryFloor + 9 == end
            ? new(true, owns ? plan.StartFloor : result.EntryFloor, owns, false, string.Empty)
            : new(false, result.EntryFloor + 10, false, false, string.Empty);
    }
}
