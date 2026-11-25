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
            ImGui.SetTooltip("選中敵人後，超過設定秒數沒打 GCD（戰技／魔法），就嘗試補打一招。\n不限場景；暫停或停手後仍生效，要完全停手請關閉。");
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
