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
        // No entry controller, save-slot read, save creation or deletion is involved.
        var plan = new FarmingPlan(FarmingMode.DeepProgression, SaveUse.Create, groupStart, 1, false,
            false, true, true, false)
        {
            DungeonId = facts.DungeonId, ExternalSave = true, CurrentDutyOnly = true,
            ExternalSaveCharacter = facts.Character, ExternalSaveJob = facts.Job
        };
        bool started = TryStartOutsideDutyFsd(() => new CurrentDutyResumeScenario(plan, groupStart),
            1, false, null, out error);
        if (started) _farmingError = null;
        return new(started, started ? null : error);
    }
}
