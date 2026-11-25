using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    // Both native-save entry and in-duty attachment build exactly this selected-mode plan.
    // StartFloor is the NEXT cycle's origin; the first source lives in FarmingSession.
    private bool TryCreateContinuationPlan(uint dungeon, int currentEntry, ulong character, uint job,
        out FarmingPlan? plan, out string error)
    {
        plan = null;
        error = "請選擇與目前存檔相同的地宮。";
        var settings = _configuration.Farming;
        if (settings.DungeonId != dungeon) return false;
        var mode = settings.RelicMaterial ? FarmingMode.Hoard
            : dungeon == 4 ? settings.Mode : FarmingMode.DeepProgression;
        var p = settings.GetPreferences(_configuration, mode);
        int cycles = p.KeepSave ? 1 : Math.Max(1, p.Cycles);
        bool infinite = !p.KeepSave && p.Infinite;
        var targets = new FarmingTargets(p.BandedEnabled == true, p.OpenGold, p.OpenSilver, p.OpenBronze);
        if (settings.RelicMaterial)
        {
            if (!RelicFarmingPolicy.TryCreate(dungeon, SaveUse.Create,
                AetherpoolExchangePolicy.GetDungeon(dungeon)?.StartFloor ?? 0,
                cycles, infinite, targets, out plan, out error)) return false;
        }
        else if (settings.FreeExploration)
        {
            var free = settings.GetFreeExplorationPreferences();
            int restart = TaskContinuationPolicy.HighestStartAtOrBelow(dungeon, free.EndFloor);
            if (!FreeExplorationPolicy.TryCreate(dungeon, restart, free.EndFloor, cycles, infinite,
                targets, free.AggressiveChestInteraction, false, out plan, out error)) return false;
        }
        else
        {
            bool ordinary = mode == FarmingMode.DeepProgression && settings.StartFromHighestFloor;
            int restart = ordinary ? OrdinaryDescentPolicy.HighestStartFloor(dungeon)
                : mode == FarmingMode.DeepProgression ? 1 : _fsfScenarioIndex == 0 ? 21 : 31;
            var use = mode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery ? SaveUse.Prepared : SaveUse.Create;
            if (!FarmingPlan.TryCreate(mode, use, restart, cycles, infinite, targets.Hoard,
                targets.Gold, targets.Silver, targets.Bronze, out plan, out error, dungeon, ordinary)) return false;
        }
        plan = TaskContinuationPolicy.WithRetention(ConfigureGripExchange(plan!), p.KeepSave) with
        {
            ExternalSave = true, ManagedContinuation = true, ContinuationStartFloor = currentEntry,
            ExternalSaveCharacter = character, ExternalSaveJob = job
        };
        return TaskContinuationPolicy.ValidateMode(plan, currentEntry, out error);
    }

    private FsdControlResult StartModeContinuation(FarmingPlan plan, SaveSlotSnapshot? source)
    {
        var session = new FarmingSession(plan, source?.Index ?? -1) { InitialGripExchangeChecked = true };
        if (source is { } selected)
            session.RestoreCheckpoint(selected, 0, plan.ExternalSaveCharacter,
                () => ExternalSavePolicy.MatchesCharacter(plan, Service.PlayerState.ContentId,
                    Service.LocalPlayer?.ClassJob.RowId ?? 0) ? null : "角色或職業變更；保檔停止。",
                reason => _farmingError = reason);
        else session.AttachCurrentDuty(plan.ContinuationStartFloor!.Value);
        string? key = plan.ShowsDetailedMap ? plan.StartFloor == 21
            ? DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key
            : DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key : null;
        try { _configuration.Save(); }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "[LocalExtensions.Continuation] Settings were not saved");
            return new(false, "無法保存任務設定，尚未啟動。請檢查設定目錄權限。");
        }
        bool started = TryStartOutsideDutyFsd(() => CreateLocalFarmingScenario(session),
            plan.TargetCycles, plan.Infinite, key, out var error);
        if (started) _farmingError = null;
        return new(started, started ? null : error);
    }
}
