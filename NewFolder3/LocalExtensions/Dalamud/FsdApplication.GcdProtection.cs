using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication
{
    public void DrawGcdProtectionSettings()
    {
        bool enabled = _settings.Farming.GcdStallProtection;
        if (ImGui.Checkbox("GCD 停滯保護##localGcdProtection", ref enabled))
        {
            _settings.Farming.GcdStallProtection = enabled;
            _settings.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("僅地宮內，選中敵人超過設定秒數沒打 GCD 時嘗試補一招。\n不需啟動 FSD；完全停手請關閉。");
        ImGui.SameLine();
        ImGui.TextDisabled($"触发次数：{_module.TriggerCounters?.Count(FeatureTriggerKind.GcdProtection) ?? 0}");
        if (ImGui.IsItemHovered() && _module.TriggerCounters?.SaveError is { } error) ImGui.SetTooltip(error);
        int seconds = GcdStallWatchdog.NormalizeWaitSeconds(_settings.Farming.GcdStallWaitSeconds);
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("等待秒數##localGcdWaitSeconds", ref seconds))
        {
            _settings.Farming.GcdStallWaitSeconds = Math.Clamp(seconds,
                GcdStallWatchdog.MinWaitSeconds, GcdStallWatchdog.MaxWaitSeconds);
            _settings.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("多久沒打 GCD 才補打一招。可設 1–300 秒，預設 6 秒。");
    }
}
