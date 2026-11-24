using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Runtime;

public sealed partial class FarmingSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool GcdStallProtection { get; set; }
}
