using Dalamud.Configuration;
using System.Collections.Generic;

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
    // Packed as 0xAABBGGRR so this persists as a plain JSON number.
    public uint DirectHitCustomColor { get; set; } = 0xFFFFFFFF;
    public uint CriticalCustomColor { get; set; } = 0xFFFFFFFF;
    public uint DirectCriticalCustomColor { get; set; } = 0xFFFFFFFF;
    public bool DirectHitUseCustomColor { get; set; }
    public bool CriticalUseCustomColor { get; set; }
    public bool DirectCriticalUseCustomColor { get; set; }

    public bool CountdownAnnouncementEnabled { get; set; } = true;
    public string CountdownAnnouncementText { get; set; } = "倒计时最后1秒";
    public int CountdownAnnouncementIcon { get; set; }

    public List<OutputPart> DirectHitTemplate { get; set; } = new();
    public List<OutputPart> CriticalTemplate { get; set; } = new();
    public List<OutputPart> DirectCriticalTemplate { get; set; } = new();
    public List<OutputPart> CombatSeparatorTemplate { get; set; } = new();
    public List<OutputPart> CountdownTemplate { get; set; } = new();
}

public sealed class OutputPart
{
    public string Kind { get; set; } = "Text";
    public string Value { get; set; } = string.Empty;
    public int IconId { get; set; }
    public uint Color { get; set; } = 0xFFFFFFFF;
}
