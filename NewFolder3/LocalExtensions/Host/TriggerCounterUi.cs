using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class TriggerCounterUi
{
    internal static void Draw(FeatureTriggerCounters? counters, FeatureTriggerKind kind)
    {
        ImGui.SameLine();
        ImGui.TextDisabled($"触发次数：{counters?.Count(kind) ?? 0}");
        if (ImGui.IsItemHovered() && counters?.SaveError is { } error) ImGui.SetTooltip(error);
    }
}
