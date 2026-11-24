using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication
{
    public void DrawGcdProtectionSettings()
    {
        bool enabled = _settings.Farming.GcdStallProtection;
        if (ImGui.Checkbox("GCD 停滯保護（測試）##localGcdProtection", ref enabled))
        {
            _settings.Farming.GcdStallProtection = enabled;
            _settings.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("預設關閉。選中同一可攻擊敵人後，超過設定秒數沒有 GCD，就嘗試一次本職基礎單體 GCD。不限場景、FSD 運行狀態或輸出插件，AE／自訂指令也適用；暫停探索、停手或機制故障不會關閉此保護，完全停手請關閉本開關。仍須角色、技能、射程及視線實際可用；不切目標、不移動。同次停滯只嘗試一次，觀察到 GCD 冷卻後重新計時。");
        int seconds = GcdStallWatchdog.NormalizeWaitSeconds(_settings.Farming.GcdStallWaitSeconds);
        ImGui.SetNextItemWidth(120);
        if (ImGui.InputInt("等待秒數##localGcdWaitSeconds", ref seconds))
        {
            _settings.Farming.GcdStallWaitSeconds = Math.Clamp(seconds,
                GcdStallWatchdog.MinWaitSeconds, GcdStallWatchdog.MaxWaitSeconds);
            _settings.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("1–300 秒，預設 6 秒。能力技不會重置 GCD 等待時間；修改秒數、換目標或職業時重新計時。讀條、冷卻、技能排隊或動畫鎖結束且技能可用後才補打。");
    }
}
