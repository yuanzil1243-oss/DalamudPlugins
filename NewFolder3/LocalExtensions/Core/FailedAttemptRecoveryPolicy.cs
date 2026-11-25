namespace DeepDungeon.Fsd.Core;

public readonly record struct FailedResultFacts(bool Entered, bool SameAttempt, bool Transitioning,
    bool PlayerDead, bool ResultRecognized, bool Completed, bool Failed);

/// <summary>A native final-results panel ends revival waiting; counters are not evidence.</summary>
public static class FailedAttemptRecoveryPolicy
{
    public static bool ShouldObserveFailure(in FailedResultFacts f) =>
        f.Entered && f.SameAttempt && !f.Transitioning && f.PlayerDead &&
        f.ResultRecognized && !f.Completed && !f.Failed;

    public static bool TryResolve(IReadOnlyList<SaveSlotSnapshot> slots, int ownedSlot, int entryFloor,
        SaveSlotSnapshot? pinned, out SaveSlotSnapshot source, out string error)
    {
        source = default;
        error = "無法確認結算後的本任務存檔；保留全部存檔並停止。";
        if (slots.Count != 2 || slots.Select(s => s.Index).Distinct().Count() != 2 ||
            slots.Any(s => s.Index is < 0 or > 1) || ownedSlot is < 0 or > 1 ||
            entryFloor < 1 || entryFloor % 10 != 1) return false;
        if (OwnedSavePolicy.ValidateForDeletion(slots, ownedSlot, entryFloor, pinned) != null) return false;
        source = slots.Single(s => s.Index == ownedSlot);
        if (string.IsNullOrWhiteSpace(source.Identity) || source.RowFingerprint is not { Length: 64 } ||
            source.Enterable && source.StartFloor != entryFloor) return false;
        error = string.Empty;
        return true;
    }

    public static FarmingSessionDecision Reconcile(FarmingPlan plan, in FarmingSessionDecision decision,
        in SaveSlotSnapshot source)
    {
        if (!decision.RecoverFailure || !TaskContinuationPolicy.CanDeleteSave(plan) || !source.Enterable)
            return decision;
        return decision with { CountCycle = false, NextFloor = source.StartFloor,
            DeleteOwnedSave = false, RecoverFailure = true, Error = string.Empty };
    }
}
