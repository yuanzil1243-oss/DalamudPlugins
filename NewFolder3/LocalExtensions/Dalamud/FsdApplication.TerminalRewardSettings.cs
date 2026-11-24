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
            ImGui.SetTooltip("預設關閉，關閉時保留原版領獎後停留行為。開啟後須由原版確認終局獎勵已領取，才繼續退本；本任務新建存檔經核驗後刪除，按 FSD 的循環次數／無限循環設定重開。已有存檔及「繼續探索」只退本、保檔停止；診斷保留現場不受影響。");
    }
}
