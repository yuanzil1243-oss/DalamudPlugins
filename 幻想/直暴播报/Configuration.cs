using Dalamud.Configuration;

namespace CritChat;

public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public string Delimiter { get; set; } = " ";
    public string CombatSeparator { get; set; } = "──────────";

    public string DirectHitSuffix { get; set; } = "";
    public string CriticalSuffix { get; set; } = "！";
    public string DirectCriticalSuffix { get; set; } = "！！";

    // UIColor row IDs; zero means use the default chat color.
    public ushort DirectHitColor { get; set; }
    public ushort CriticalColor { get; set; }
    public ushort DirectCriticalColor { get; set; }
}

