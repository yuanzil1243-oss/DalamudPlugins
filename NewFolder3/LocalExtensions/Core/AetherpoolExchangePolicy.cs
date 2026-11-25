// Adapted from the user-supplied NewFolder3_7.96.3 AetherpoolExchangePolicy.
// Existing project GPL-3.0 licensing and upstream attribution are retained.
using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Core;

public sealed record GripExchangeOptions(int Threshold = 50, int BatchCount = 4)
{
    public bool IsValid => Threshold is >= 15 and <= 99 && BatchCount is >= 1 and <= 9;
}

public sealed partial record FarmingPlan
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GripExchangeOptions? GripExchange { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RelicMaterial { get; init; }
}

public sealed record GripExchangeDungeon(uint DungeonId, int StartFloor, int Reserve,
    uint NpcId, uint ItemId, string NpcName, string GripName);

public static class AetherpoolExchangePolicy
{
    public const int CostPerGrip = 10;
    // Conservative reserves from the supplied implementation, not an unlock guarantee.
    public static GripExchangeDungeon? GetDungeon(uint id) => id switch
    {
        1 => new(1, 51, 14, 1017338, 23165, "艾·烏納·科特羅", "聚魔柄"),
        2 => new(2, 21, 14, 1025848, 23166, "海賊眾的資材負責人", "天之聚魔柄"),
        4 => new(4, 21, 5, 1054944, 47098, "奧瑟·衛德", "光耀聚魔柄"),
        _ => null
    };
    public static int NormalizeThreshold(int value) => value <= 0 ? 50 : Math.Clamp(value, 15, 99);
    public static int NormalizeCount(int value) => value <= 0 ? 4 : Math.Clamp(value, 1, 9);
    public static bool ShouldRunPostExitExchange(bool enabled, bool alreadyChecked, bool pauseRequested,
        bool exchangeActive, in FarmingAttemptResult result) =>
        enabled && !alreadyChecked && result.DutyCompleted && !result.DutyFailed && !result.Aborted &&
        (exchangeActive || !pauseRequested);
    public static int ComputePlannedExchanges(int? weapon, int? armor, GripExchangeOptions options, int reserve)
    {
        if (!options.IsValid || weapon is not (>= 0 and <= 99) || armor is not (>= 0 and <= 99) || reserve < 0)
            return 0;
        int minimum = Math.Min(weapon.Value, armor.Value);
        return minimum < options.Threshold ? 0 : Math.Min(options.BatchCount, Math.Max(0, (minimum - reserve) / CostPerGrip));
    }
    public static bool IsCraftConfirmation(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        bool grip = text.Contains("聚魔柄", StringComparison.Ordinal) || text.Contains("魔器柄", StringComparison.Ordinal) ||
            text.Contains("aetherpool grip", StringComparison.OrdinalIgnoreCase);
        bool craft = new[] { "制作", "製作", "打造", "兑换", "兌換", "交换", "交換", "forge", "create", "exchange" }
            .Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        bool forbidden = new[] { "删除", "刪除", "放弃", "放棄", "带出", "帶出", "abandon", "delete" }
            .Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase));
        return grip && craft && !forbidden;
    }
    public static bool IsCraftOption(string text) => IsCraftConfirmation(text);
    public static bool IsCancelOption(string text) => text.Trim() is "取消" or "キャンセル" ||
        text.Trim().Equals("Cancel", StringComparison.OrdinalIgnoreCase) ||
        text.Trim().Equals("Nothing.", StringComparison.OrdinalIgnoreCase) ||
        text.Trim().Equals("Nothing", StringComparison.OrdinalIgnoreCase);
}

public static class RelicFarmingPolicy
{
    public static bool TryCreate(uint dungeonId, SaveUse saveUse, int startFloor, int cycles, bool infinite,
        FarmingTargets targets, out FarmingPlan? plan, out string error)
    {
        plan = new FarmingPlan(FarmingMode.Hoard, saveUse, startFloor, cycles, infinite,
            targets.Hoard, targets.Gold, targets.Silver, targets.Bronze)
            { DungeonId = dungeonId, RelicMaterial = true };
        error = IsValid(plan) ? "" : "聚魔柄循環只支援自建死宮 51–60、天宮／朝聖 21–30，並須開啟銀箱。";
        if (error.Length == 0) return true;
        plan = null; return false;
    }
    public static bool IsValid(FarmingPlan p) => p.RelicMaterial && p.Mode == FarmingMode.Hoard &&
        AetherpoolExchangePolicy.GetDungeon(p.DungeonId) is { } dungeon && p.StartFloor == dungeon.StartFloor &&
        p.SaveUse == SaveUse.Create && !p.ExternalSave && !p.CurrentDutyOnly && !p.FreeExploration &&
        !p.OrdinaryDescent && p.StopAfterFloor == null && p.TargetCycles > 0 && p.OpenSilver &&
        (p.GripExchange == null || p.GripExchange.IsValid);
    public static FarmingSessionDecision Decide(FarmingPlan p, in FarmingAttemptResult r)
    {
        if (!IsValid(p) || r.EntryFloor != p.StartFloor)
            return new(false, r.EntryFloor, false, false, "聚魔柄任務來源或層段不符；保檔停止。");
        if (r.Aborted || p.HoldOnFailure && r.DutyFailed)
            return new(false, p.StartFloor, false, false, "聚魔柄任務已中止；保檔停止。");
        if (r.DutyFailed) return new(false, p.StartFloor, true, true, "");
        if (!r.DutyCompleted) return new(false, r.EntryFloor, false, false, "未確認通關；保檔停止。");
        return new(true, p.StartFloor, true, false, "");
    }
}
