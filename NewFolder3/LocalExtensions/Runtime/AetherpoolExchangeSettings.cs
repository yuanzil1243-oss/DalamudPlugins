using DeepDungeon.Fsd.Core;
using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Runtime;

public sealed partial class FarmingSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AetherpoolExchangeSettings? GripExchange { get; set; }
    // Feature flags never occupy retired persisted FarmingMode values 4/5.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool RelicMaterial { get; set; }
    public FarmingPreferences GetRelicMaterialPreferences()
    {
        string key = $"RelicMaterial:{DungeonId}:Create";
        if (!Preferences.TryGetValue(key, out var p))
            Preferences[key] = p = new() { BandedEnabled = false, OpenGold = true, OpenSilver = true };
        p.OpenSilver = true;
        return p;
    }
}
public sealed class AetherpoolExchangeSettings
{
    public bool EnabledForHoard { get; set; }
    public int Threshold { get; set; } = 50;
    public int BatchCount { get; set; } = 4;
    public GripExchangeOptions ToOptions() => new(AetherpoolExchangePolicy.NormalizeThreshold(Threshold),
        AetherpoolExchangePolicy.NormalizeCount(BatchCount));
}
