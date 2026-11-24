using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Runtime;

public sealed partial class FarmingSettings
{
    // Old configurations and pause fingerprints keep their default representation.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool AutoExitAfterTerminalReward { get; set; }
}
