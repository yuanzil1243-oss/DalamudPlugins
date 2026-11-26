using DeepDungeon.Fsd.Core;
using System.Text.Json.Serialization;

namespace DeepDungeon.Fsd.Runtime;

public sealed partial class FarmingSettings
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FreeExploration { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<uint, FreeExplorationPreferences>? FreeExplorationProfiles { get; set; }

    public FreeExplorationPreferences GetFreeExplorationPreferences()
    {
        FreeExplorationProfiles ??= new();
        if (!FreeExplorationProfiles.TryGetValue(DungeonId, out var preferences))
            FreeExplorationProfiles[DungeonId] = preferences = new()
            {
                StartFloor = FreeExplorationPolicy.NewSaveStarts(DungeonId).LastOrDefault(1),
                EndFloor = DungeonId == 1 ? 200 : 100,
                BandedEnabled = false, OpenGold = true, OpenSilver = true
            };
        return preferences;
    }
}

public sealed class FreeExplorationPreferences : FarmingPreferences
{
    public bool BlindSearchRangeEnabled { get; set; }
    public int BlindSearchStartFloor { get; set; } = 1;
    public int BlindSearchEndFloor { get; set; }
    public int StartFloor { get; set; } = 1;
    public int EndFloor { get; set; } = 100;
    public bool AggressiveChestInteraction { get; set; }
}
