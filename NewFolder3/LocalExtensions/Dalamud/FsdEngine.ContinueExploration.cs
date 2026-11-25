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
        bool ready = ContinueExplorationPolicy.TryAttach(facts, out int group, out var error);
        if (ready) ready = TryCreateContinuationPlan(facts.DungeonId, group, facts.Character, facts.Job,
            out _, out error);
        ImGui.SameLine();
        ImGui.BeginDisabled(!ready);
        if (ImGui.Button("繼續探索##fsd_continue_current"))
        {
            var result = ContinueExploration();
            _farmingError = result.Ok ? null : result.Error;
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(ready ? "按所選模式接續目前地宮，不用選槽位；完成後按保檔與循環設定處理。" : error);
    }

    public FsdControlResult ContinueExploration()
    {
        _dutyState?.Update(Service.Framework);
        var facts = CurrentDutyResumeScenario.ReadFacts(_dutyState, IsRunActive);
        if (!ContinueExplorationPolicy.TryAttach(facts, out var groupStart, out var error))
            return new(false, error);
        if (ProgressionPauseLocked)
            return new(false, "已有暫停任務，請先繼續原任務或在副本外解除續跑鎖定。");
        if (!TryCreateContinuationPlan(facts.DungeonId, groupStart, facts.Character, facts.Job,
                out var plan, out error)) return new(false, error);
        return StartModeContinuation(plan!, null);
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
