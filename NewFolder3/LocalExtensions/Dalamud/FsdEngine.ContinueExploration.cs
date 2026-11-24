using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private void DrawContinueExplorationButton()
    {
        var facts = CurrentDutyResumeScenario.ReadFacts(_dutyState, IsRunActive);
        if (!ContinueExplorationPolicy.IsVisible(facts.InDeepDungeonTerritory, facts.Running)) return;
        bool ready = ContinueExplorationPolicy.TryAttach(facts, out _, out var error);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("繼續探索##fsd_continue_current"))
        {
            var result = ContinueExploration();
            _farmingError = result.Ok ? null : result.Error;
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ready ? "直接接續目前深宮，不需要選擇存檔槽；本組正常出本後停止並保檔。" : error);
    }

    public FsdControlResult ContinueExploration()
    {
        _dutyState?.Update(Service.Framework);
        var facts = CurrentDutyResumeScenario.ReadFacts(_dutyState, IsRunActive);
        if (!ContinueExplorationPolicy.TryAttach(facts, out var groupStart, out var error))
            return new(false, error);
        if (ProgressionPauseLocked)
            return new(false, "已有暫停任務，請先繼續原任務或在副本外解除續跑鎖定。");
        var chests = _configuration.Farming.GetCurrentDutyChestPreferences(facts.DungeonId);
        try { _configuration.Save(); }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "[LocalExtensions.ContinueExploration] Could not save chest settings");
            return new(false, "無法保存接續寶箱設定，尚未啟動；請檢查設定目錄權限。");
        }
        // No entry controller, save-slot read, save creation or deletion is involved.
        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, groupStart, 1, false,
            false, chests.OpenGold, chests.OpenSilver, chests.OpenBronze)
        {
            DungeonId = facts.DungeonId, ExternalSave = true, CurrentDutyOnly = true,
            ExternalSaveCharacter = facts.Character, ExternalSaveJob = facts.Job
        };
        var scenario = new CurrentDutyResumeScenario(plan, groupStart);
        bool started = TryStartOutsideDutyFsd(() => scenario,
            1, false, null, out error);
        if (started) _localCurrentDutyResume = scenario;
        if (started) _farmingError = null;
        return new(started, started ? null : error);
    }

    private bool TrySetCurrentDutyChestTargets(bool gold, bool silver, bool bronze)
    {
        if (_ddHost is not { FsdActive: true, Context: { } context } ||
            _localCurrentDutyResume?.TrySetChestTargets(context, gold, silver, bronze) != true)
        {
            _farmingError = "目前接續任務無法修改寶箱選項。";
            return false;
        }
        return true;
    }

    private void SaveCurrentDutyChestPreferences()
    {
        try { _configuration.Save(); _farmingError = null; }
        catch (Exception ex)
        {
            Service.Log.Error(ex, "[LocalExtensions.ContinueExploration] Live chest choice could not be persisted");
            _farmingError = "寶箱選項已於本次接續生效，但保存失敗；重載前請檢查設定目錄權限。";
        }
    }
}
