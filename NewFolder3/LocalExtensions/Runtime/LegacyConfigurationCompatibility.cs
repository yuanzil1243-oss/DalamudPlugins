using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DeepDungeon.Fsd.Runtime;

/// <summary>Translate only the two enum values written by the retired local branch.</summary>
public static class LegacyConfigurationCompatibility
{
    public sealed record Result(string Json, bool Changed, int? LegacyMode, string? BackupPath = null);

    public static Result Normalize(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Configuration must be an object.");
        if (root["Fsd"] is not JsonObject fsd || fsd["Farming"] is not JsonObject farming ||
            farming["Mode"] is not JsonValue value) return new(json, false, null);
        int mode = value.TryGetValue<int>(out int number) ? number :
            value.TryGetValue<string>(out var name) ? name switch { "FreeExploration" => 4, "FinalBoss" => 5, _ => -1 } : -1;
        if (mode is not (4 or 5)) return new(json, false, null);
        farming["Mode"] = 3; // Upstream DeepProgression; never extend the persisted enum again.
        if (mode == 4)
        {
            farming["FreeExploration"] = true;
            farming["StartFromHighestFloor"] = false;
            if (farming["FreeExplorationProfiles"] is not JsonObject profiles)
                farming["FreeExplorationProfiles"] = profiles = new();
            foreach (uint dungeon in new uint[] { 2, 4 })
            {
                string key = dungeon.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (profiles[key] != null) continue; // Existing extension preferences always win.
                string preferenceKey = dungeon == 2 ? "Hoh:FreeExploration:Create" : "FreeExploration:Create";
                var source = farming["Preferences"]?[preferenceKey] as JsonObject;
                bool selected = farming["DungeonId"]?.GetValue<uint>() == dungeon;
                if (!selected && source == null) continue;
                var entry = farming[dungeon == 2 ? "HeavenOnHighEntry" : "CheckpointEntry"] as JsonObject;
                var profile = source?.DeepClone() as JsonObject ?? new JsonObject
                {
                    ["BandedEnabled"] = false, ["OpenGold"] = true, ["OpenSilver"] = true,
                    ["OpenBronze"] = false, ["Cycles"] = 1, ["Infinite"] = false
                };
                profile.Remove("$type");
                profile["StartFloor"] = entry?["StartFloor"]?.DeepClone() ?? JsonValue.Create(dungeon == 2 ? 21 : 31);
                profile["EndFloor"] = entry?["StopAfterFloor"]?.DeepClone() ?? JsonValue.Create(dungeon == 2 ? 30 : 50);
                profile["AggressiveChestInteraction"] = fsd["AggressiveChestInteraction"]?.DeepClone() ?? JsonValue.Create(false);
                profiles[key] = profile;
            }
        }
        else
        {
            farming["FreeExploration"] = false;
            // The retired final-boss mode has no upstream equivalent. Load safely and require a manual start.
        }
        return new(root.ToJsonString(new() { WriteIndented = true }) + "\n", true, mode);
    }

    public static Result PrepareFile(string path)
    {
        if (!File.Exists(path)) return new(string.Empty, false, null);
        byte[] original = File.ReadAllBytes(path);
        var result = Normalize(Encoding.UTF8.GetString(original).TrimStart('\uFEFF'));
        if (!result.Changed) return result;
        string hash = Convert.ToHexString(SHA256.HashData(original)).ToLowerInvariant();
        string directory = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path), "LocalCompatibilityBackups");
        Directory.CreateDirectory(directory);
        string backup = Path.Combine(directory, "configuration-" + hash + ".json");
        if (!File.Exists(backup)) File.WriteAllBytes(backup, original);
        if (!File.ReadAllBytes(backup).AsSpan().SequenceEqual(original))
            throw new IOException("Legacy configuration backup did not verify; no configuration changed.");
        string temporary = path + ".compat-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, result.Json, new UTF8Encoding(false));
            if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(original))
                throw new IOException("Configuration changed during migration; retry after unloading the other version.");
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return result with { BackupPath = backup };
    }
}
