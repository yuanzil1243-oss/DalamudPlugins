using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Core;

public sealed partial record FarmingPlan
{
    // Explicit new-task consent. Old external-save/checkpoint records retain their protection.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ManagedContinuation { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool KeepSave { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ContinuationStartFloor { get; init; }
}

/// <summary>One authority for save retention, mode attachment and next-cycle origin.</summary>
public static class TaskContinuationPolicy
{
    public static bool PreserveSave(FarmingPlan p) => p.KeepSave || p.ExternalSave && !p.ManagedContinuation;
    public static bool CanDeleteSave(FarmingPlan p) => !PreserveSave(p) && !p.ReusesSave && p.SaveUse == SaveUse.Create;
    public static int MinimumFloor(FarmingPlan p) => p.ManagedContinuation
        ? Math.Min(p.StartFloor, p.ContinuationStartFloor ?? p.StartFloor) : p.StartFloor;
    public static int HighestStartAtOrBelow(uint dungeon, int end) =>
        FreeExplorationPolicy.NewSaveStarts(dungeon).Where(x => x + 9 <= end).DefaultIfEmpty(0).Max();

    public static FarmingPlan WithRetention(FarmingPlan p, bool keep) => p with
    { KeepSave = keep, TargetCycles = keep ? 1 : p.TargetCycles, Infinite = !keep && p.Infinite };

    public static bool IsValidManagedPlan(FarmingPlan p)
    {
        if (!p.ManagedContinuation) return true;
        if (!p.ExternalSave || p.ExternalSaveCharacter == 0 || p.ExternalSaveJob == 0 ||
            p.ContinuationStartFloor is not { } entry || !ValidateMode(p, entry, out _)) return false;
        if (p.RelicMaterial) return RelicFarmingPolicy.IsValid(p);
        if (p.FreeExploration)
            return p.Mode == FarmingMode.DeepProgression && p.SaveUse == SaveUse.Create &&
                p.EndFloor is { } end && end <= p.MaxFloor && end % 10 == 0 &&
                p.StartFloor == HighestStartAtOrBelow(p.DungeonId, end);
        if (p.Mode == FarmingMode.DeepProgression)
            return p.SaveUse == SaveUse.Create && p.StartFloor ==
                (p.OrdinaryDescent ? OrdinaryDescentPolicy.HighestStartFloor(p.DungeonId) : 1);
        return p.Mode == FarmingMode.Hoard
            ? p.DungeonId == 4 && p.SaveUse == SaveUse.Create && p.StartFloor is 21 or 31
            : p.DungeonId == 4 && p.ReusesSave && p.SaveUse == SaveUse.Prepared && p.StartFloor == 0;
    }

    public static bool ValidateMode(FarmingPlan p, int entry, out string error)
    {
        error = p.DungeonId is not (1 or 2 or 4) || entry < 1 || entry > p.TerminalEntryFloor || entry % 10 != 1
            ? "目前地宮或樓層無法續打。"
            : p.CurrentDutyOnly || p.StopAfterFloor.HasValue || p.HoldOnFailure || p.HoldAfterClear
                ? "此任務不能混用診斷或單組接管設定。"
            : p.RelicMaterial && entry != p.StartFloor
                ? $"聚魔柄模式只能在 {p.StartFloor}–{p.StartFloor + 9} 層繼續。"
            : p.FreeExploration && (p.EndFloor is not { } end || end < entry + 9)
                ? "自由探索的結束樓層不能低於目前十層組。"
            : !p.RelicMaterial && !p.FreeExploration && p.Mode == FarmingMode.Hoard && entry != p.StartFloor
                ? $"挖寶模式只能在所選的 {p.StartFloor}–{p.StartFloor + 9} 層繼續。"
            : p.ReusesSave && entry > 21
                ? "此採集模式只能在 1–30 層繼續。"
            : string.Empty;
        return error.Length == 0;
    }

    public static FarmingSessionDecision ApplyRetention(FarmingPlan p, in FarmingAttemptResult r,
        FarmingSessionDecision decision)
    {
        if (!PreserveSave(p)) return decision;
        if (r.DutyFailed || r.Aborted)
            return new(false, r.EntryFloor, false, false, "本輪未完成；保留存檔並停止。") { StopAfterAttempt = true };
        return decision with { DeleteOwnedSave = false, RecoverFailure = false,
            StopAfterAttempt = decision.CountCycle || decision.StopAfterAttempt };
    }
}

/// <summary>Resolve an unselected in-duty source only from a fresh post-exit native list.
/// Multiple compatible rows are ambiguous; never fall back to the first slot.</summary>
public static class ContinuationSavePolicy
{
    public static bool TryResolve(IReadOnlyList<SaveSlotSnapshot> slots, int entry, int max,
        out SaveSlotSnapshot source, out string error)
    {
        source = default;
        error = "無法唯一確認本次續打的存檔；保留全部存檔並停止。";
        if (slots.Count != 2 || slots.Select(s => s.Index).Distinct().Count() != 2 ||
            slots.Any(s => s.Index is < 0 or > 1) || entry < 1 || entry % 10 != 1 || entry > max - 9)
            return false;
        var candidates = slots.Where(s => !s.Empty && s.StartFloor >= entry && s.StartFloor <= Math.Min(max, entry + 10) &&
            !string.IsNullOrWhiteSpace(s.Identity) && s.RowFingerprint is { Length: 64 }).ToArray();
        if (candidates.Length != 1) return false;
        source = candidates[0];
        error = string.Empty;
        return true;
    }
}
