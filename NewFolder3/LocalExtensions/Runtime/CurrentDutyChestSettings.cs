using System.Text.Json.Serialization;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public sealed partial class FarmingSettings
{
    // Keep old settings/checkpoint fingerprints unchanged until this feature is used.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<uint, FarmingPreferences>? CurrentDutyChestProfiles { get; set; }

    public FarmingPreferences GetCurrentDutyChestPreferences(uint dungeonId)
    {
        if (dungeonId is not (1 or 2 or 4))
            throw new ArgumentOutOfRangeException(nameof(dungeonId));
        CurrentDutyChestProfiles ??= new();
        if (CurrentDutyChestProfiles.TryGetValue(dungeonId, out var saved) && saved != null)
            return saved;

        // Only the selected dungeon has a trustworthy link to the selected mode.
        // Read without GetPreferences(): that API constrains and mutates upstream profiles.
        FarmingPreferences? source = null;
        if (DungeonId == dungeonId)
        {
            if (FreeExploration)
            {
                if (FreeExplorationProfiles != null && FreeExplorationProfiles.TryGetValue(dungeonId, out var free))
                    source = free;
            }
            else
            {
                var mode = dungeonId == 4 ? Mode : FarmingMode.DeepProgression;
                string key = mode == FarmingMode.DeepProgression && StartFromHighestFloor ? "OrdinaryDescent:Create" : mode switch
                {
                    FarmingMode.Hoard => "Hoard:Create",
                    FarmingMode.Aetherpool => "Aetherpool:Prepared",
                    FarmingMode.HoardDiscovery => "HoardDiscovery:Prepared",
                    _ => "DeepProgression:Create"
                };
                Preferences.TryGetValue(key, out source);
            }
        }
        // These are the previous resume defaults, used only without a reliable stored choice.
        var value = new FarmingPreferences
        {
            BandedEnabled = false,
            OpenGold = source?.OpenGold ?? true,
            OpenSilver = source?.OpenSilver ?? true,
            OpenBronze = source?.OpenBronze ?? false
        };
        CurrentDutyChestProfiles[dungeonId] = value;
        return value;
    }
}

public static class CurrentDutyChestPolicy
{
    public static bool TryApply(FarmingPlan? plan, IRunOptionsProvider options, in FarmingTargets targets)
    {
        if (plan is not { CurrentDutyOnly: true, ExternalSave: true, DungeonId: 1 or 2 or 4 } || targets.Hoard)
            return false;
        // Capture the value, not the in parameter, for RunOptionsProvider's copy-on-write callback.
        var requested = targets;
        options.Update(o =>
        {
            o.OpenGold = requested.Gold;
            o.OpenSilver = requested.Silver;
            o.OpenBronze = requested.Bronze;
        });
        return true;
    }
}
