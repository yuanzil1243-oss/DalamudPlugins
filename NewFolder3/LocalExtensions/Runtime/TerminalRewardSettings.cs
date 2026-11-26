using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Runtime;

public sealed partial class FarmingSettings
{
    // Legacy compatibility only: preserved for old config/pause fingerprints, no longer an enable switch.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoExitAfterTerminalReward { get; set; }
}
