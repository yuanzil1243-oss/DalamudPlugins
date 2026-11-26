using DeepDungeon.Fsd.Core;
using global::Dalamud.Bindings.ImGui;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private void DrawFreeBlindSearchOptions(bool running, bool free, bool hoard)
    {
        if (!free || !hoard) return;
        var preferences = _configuration.Farming.GetFreeExplorationPreferences();
        int max = _configuration.Farming.DungeonId == 1 ? 200 : 100;
        bool enabled = preferences.BlindSearchRangeEnabled;
        bool changed = false;
        ImGui.SameLine();
        ImGui.BeginDisabled(running);
        if (ImGui.Checkbox("只盲踩指定層數##freeBlindRange", ref enabled))
        { preferences.BlindSearchRangeEnabled = enabled; changed = true; }
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 28f);
            ImGui.TextUnformatted("範圍內盲踩並優先用感知；範圍外只在感知溢出時使用，已有準確點仍會去踩。層數在下次啟動生效。");
            ImGui.PopTextWrapPos(); ImGui.EndTooltip();
        }
        if (enabled)
        {
            int start = Math.Clamp(preferences.BlindSearchStartFloor, 1, max);
            int end = preferences.BlindSearchEndFloor == 0
                ? Math.Clamp(preferences.EndFloor, start, max) : Math.Clamp(preferences.BlindSearchEndFloor, start, max);
            ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("盲踩起始層##freeBlindStart", ref start))
            { start = Math.Clamp(start, 1, max); end = Math.Max(start, end); changed = true; }
            ImGui.SameLine(); ImGui.SetNextItemWidth(80);
            if (ImGui.InputInt("盲踩最終層##freeBlindEnd", ref end))
            { end = Math.Clamp(end, start, max); changed = true; }
            if (changed)
            { preferences.BlindSearchStartFloor = start; preferences.BlindSearchEndFloor = end; }
        }
        ImGui.EndDisabled();
        if (changed) _configuration.Save();
    }
}
