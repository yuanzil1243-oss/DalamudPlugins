using global::Dalamud.Bindings.ImGui;

namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication
{
    public void DrawTerminalRewardSettings()
    {
        bool enabled = _settings.Farming.AutoExitAfterTerminalReward;
        if (ImGui.Checkbox("下底領獎後自動退本刪檔循環##terminalRewardAutoExit", ref enabled))
        {
            _settings.Farming.AutoExitAfterTerminalReward = enabled;
            _settings.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("領完下底獎勵後，自動退本、刪除本任務新建的存檔，按設定次數重開。\n已有存檔和「繼續探索」只退本，保留存檔。");
    }
}
