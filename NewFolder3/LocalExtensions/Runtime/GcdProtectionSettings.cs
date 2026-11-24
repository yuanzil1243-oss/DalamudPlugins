using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Runtime;

public sealed partial class FarmingSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool GcdStallProtection { get; set; }

    // Zero/missing preserves old checkpoint fingerprints and means the six-second default.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int GcdStallWaitSeconds { get; set; }
}
