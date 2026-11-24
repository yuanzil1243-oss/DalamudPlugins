using global::Dalamud.Bindings.ImGui;

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
            ImGui.SetTooltip("預設關閉。FSD 選定同一可攻擊目標，在射程／視線內且連續 6 秒允許輸出、沒有 GCD 進展時，只嘗試一次本職基礎單體 GCD。讀條、冷卻、排隊、動畫鎖、停手及機制故障不強攻。僅支援能讀回 On 狀態的 RSR、Wrath、Prome；AE 的 /aestop 無可靠讀回，AE／自訂／INH3 不補打。請在日誌核對請求與後續 GCD，不能據此視為 Boss 模組已修復。");
    }
}
