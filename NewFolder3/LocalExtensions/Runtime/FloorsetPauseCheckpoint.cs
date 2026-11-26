using System.Security.Cryptography;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public enum FloorsetResumeKind { ContinueSave, RestartCycle, ReusePrepared }

public sealed record FloorsetPauseCheckpoint(ulong Character, uint Job, FarmingPlan Plan,
    FloorsetResumeKind Kind, SaveSlotSnapshot? Slot, int CompletedCycles, int Failures)
{
    public bool IsValid()
    {
        if (Character == 0 || Job == 0 || Plan == null || !Enum.IsDefined(Kind) ||
            !FloorsetPausePolicy.Supports(Plan) || CompletedCycles < 0 || Failures < 0 ||
            Plan.TargetCycles < 1 || !Plan.Infinite && CompletedCycles >= Plan.TargetCycles ||
            Plan.ExternalSave && (Plan.ExternalSaveCharacter != Character || Plan.ExternalSaveJob != Job))
            return false;
        if (Kind == FloorsetResumeKind.RestartCycle)
            return Slot == null && !Plan.ReusesSave && !TaskContinuationPolicy.PreserveSave(Plan);
        if (Slot is not { Index: 0 or 1, Empty: false, Enterable: true, RowFingerprint.Length: 64 } slot ||
            slot.StartFloor < 1 || slot.StartFloor > Plan.TerminalEntryFloor || slot.StartFloor % 10 != 1 ||
            string.IsNullOrWhiteSpace(slot.Identity)) return false;
        return Kind == FloorsetResumeKind.ReusePrepared
            ? Plan.ReusesSave && slot.StartFloor <= 21
            : Plan.Mode == FarmingMode.DeepProgression && !Plan.ReusesSave &&
                slot.StartFloor > TaskContinuationPolicy.MinimumFloor(Plan) &&
                (!Plan.FreeExploration || slot.StartFloor <= Plan.EndFloor);
    }
}

public static class FloorsetPausePolicy
{
    public static bool Supports(FarmingPlan plan)
    {
        if (!Enum.IsDefined(plan.Mode) || !Enum.IsDefined(plan.SaveUse) || plan.DungeonId is not (1 or 2 or 4) ||
            plan.CurrentDutyOnly || plan.HoldAfterClear || plan.HoldOnFailure || plan.StopAfterFloor.HasValue ||
            plan.TargetCycles < 1 || plan.DungeonId != 4 && plan.Mode != FarmingMode.DeepProgression && !plan.RelicMaterial)
            return false;
        if (plan.ExternalSave && !plan.ManagedContinuation && (plan.ReusesSave || plan.Infinite || plan.TargetCycles != 1)) return false;
        if (!TaskContinuationPolicy.IsValidManagedPlan(plan)) return false;
        if (plan.KeepSave && (plan.Infinite || plan.TargetCycles != 1)) return false;
        if (plan.RelicMaterial) return RelicFarmingPolicy.IsValid(plan);
        if (plan.FreeExploration)
            return plan.Mode == FarmingMode.DeepProgression && plan.SaveUse == SaveUse.Create &&
                FreeExplorationPolicy.TryCreate(plan.DungeonId, plan.StartFloor, plan.EndFloor ?? 0,
                    plan.TargetCycles, plan.Infinite, new(plan.BandedEnabled, plan.OpenGold, plan.OpenSilver, plan.OpenBronze),
                    plan.FreeAggressiveChestInteraction, plan.ExternalSave && !plan.ManagedContinuation, out _, out _,
                    plan.FreeBlindRangeEnabled, plan.FreeBlindStartFloor, plan.FreeBlindEndFloor);
        if (!(plan.ManagedContinuation && plan.Mode == FarmingMode.DeepProgression && !plan.BandedEnabled) &&
            !FarmingTargetPolicy.IsValid(plan.Mode, new(plan.BandedEnabled, plan.OpenGold, plan.OpenSilver, plan.OpenBronze)))
            return false;
        if (plan.Mode == FarmingMode.DeepProgression)
            return plan.SaveUse == SaveUse.Create && plan.StartFloor >= 1 &&
                plan.StartFloor <= plan.TerminalEntryFloor && plan.StartFloor % 10 == 1 &&
                (plan.ExternalSave || plan.OrdinaryDescent
                    ? plan.ExternalSave || plan.StartFloor == OrdinaryDescentPolicy.HighestStartFloor(plan.DungeonId)
                    : plan.StartFloor == 1);
        return plan.Mode == FarmingMode.Hoard
            ? plan.SaveUse == SaveUse.Create && plan.StartFloor is 21 or 31
            : plan.SaveUse == SaveUse.Prepared && plan.StartFloor == 0;
    }

    public static bool HasNextCycle(FarmingPlan plan, int completed) =>
        !TaskContinuationPolicy.PreserveSave(plan) && (plan.Infinite || completed < plan.TargetCycles);
}

internal sealed record LocalPauseRequest(int CompletedCycles,
    Func<FloorsetResumeKind, SaveSlotSnapshot?, int, int, string?> Persist);

internal sealed partial class FarmingSession
{
    internal LocalPauseRequest? LocalPause { get; set; }
    internal void RestoreLocalFailureCount(int failures) => Failures = Math.Max(0, failures);

    internal bool HasPendingFloorsetPause => LocalPause != null || PersistPause != null;

    internal bool TryCancelPendingFloorsetPause()
    {
        if (!HasPendingFloorsetPause) return false;
        LocalPause = null;
        PersistPause = null;
        return true;
    }
}

/// <summary>Moves only a byte-verified local task record; never touches any game save.</summary>
public static class PauseCheckpointArchive
{
    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static string Archive(string path, string expectedHash)
    {
        byte[] current = File.ReadAllBytes(path);
        if (Hash(current) != expectedHash)
            throw new IOException("暫停紀錄已被其他操作變更，請重新載入後再解除。");
        string directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "FsdPauseArchive");
        Directory.CreateDirectory(directory);
        string backup = Path.Combine(directory,
            Path.GetFileNameWithoutExtension(path) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") +
            "-" + Guid.NewGuid().ToString("N") + ".json");
        File.Move(path, backup, overwrite: false);
        return backup;
    }
}
