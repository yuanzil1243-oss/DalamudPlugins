using global::Dalamud.Bindings.ImGui;

namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication
{
    public void DrawTerminalRewardSettings()
    {
        bool enabled = _settings.Farming.AutoExitAfterTerminalReward;
        if (ImGui.Checkbox("下底領獎後自動退本循環##terminalRewardAutoExit", ref enabled))
        {
            _settings.Farming.AutoExitAfterTerminalReward = enabled;
            _settings.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("下底領獎後自動退本，按所選模式和循環次數重開。\n勾選「保留存檔」則退本保檔並停止。");
    }
}
