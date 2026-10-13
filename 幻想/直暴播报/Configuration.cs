using System.Numerics;
using Dalamud.Configuration;

namespace CritChat;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 2;

    public string Delimiter { get; set; } = " ";
    public int DelimiterIcon { get; set; }

    public string CombatSeparator { get; set; } = "──────────";
    public int CombatSeparatorIcon { get; set; }

    public string DirectHitSuffix { get; set; } = "";
    public int DirectHitSuffixIcon { get; set; }
    public string CriticalSuffix { get; set; } = "！";
    public int CriticalSuffixIcon { get; set; }
    public string DirectCriticalSuffix { get; set; } = "！！";
    public int DirectCriticalSuffixIcon { get; set; }

    // Keep the palette IDs so existing configurations continue to work until a custom color is selected.
    public ushort DirectHitColor { get; set; } = 45;
    public ushort CriticalColor { get; set; }
    public ushort DirectCriticalColor { get; set; }
    public Vector4 DirectHitCustomColor { get; set; } = Vector4.One;
    public Vector4 CriticalCustomColor { get; set; } = Vector4.One;
    public Vector4 DirectCriticalCustomColor { get; set; } = Vector4.One;
    public bool DirectHitUseCustomColor { get; set; }
    public bool CriticalUseCustomColor { get; set; }
    public bool DirectCriticalUseCustomColor { get; set; }

    public bool CountdownAnnouncementEnabled { get; set; } = true;
    public string CountdownAnnouncementText { get; set; } = "倒计时最后1秒";
    public int CountdownAnnouncementIcon { get; set; }
}
