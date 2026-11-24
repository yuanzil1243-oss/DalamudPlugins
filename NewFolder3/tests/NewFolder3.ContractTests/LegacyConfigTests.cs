using System.Text.Json.Nodes;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class LegacyConfigTests
{
    internal static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  legacy-config-" + name); }
        string Sample(int mode) => """
        {"Version":1,"unrelated":{"keep":"value"},"Fsd":{"Rotation":{"Provider":3},"Farming":{
        "DungeonId":4,"Mode":$MODE,"CheckpointEntry":{"StartFloor":31,"StopAfterFloor":50,"ResumeSlot":1},
        "HeavenOnHighEntry":{"StartFloor":21,"StopAfterFloor":30},"Preferences":{
        "Hoh:FreeExploration:Create":{"OpenGold":false,"OpenSilver":true,"OpenBronze":true,"Cycles":3,"Infinite":false},
        "Hoard:Create":{"Cycles":7}}}}}
        """.Replace("$MODE", mode.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (int mode in new[] { 0, 1, 2, 3, 6, 99 })
            Check("other-mode-byte-preserved-" + mode, () =>
            {
                string text = Sample(mode); var r = LegacyConfigurationCompatibility.Normalize(text);
                Assert(!r.Changed && r.Json == text);
            });
        Check("free-mode-4-migrated-with-profiles-and-original-fields", () =>
        {
            var r = LegacyConfigurationCompatibility.Normalize(Sample(4));
            var root = JsonNode.Parse(r.Json)!; var f = root["Fsd"]!["Farming"]!;
            Assert(r.Changed && r.LegacyMode == 4 && f["Mode"]!.GetValue<int>() == 3 && f["FreeExploration"]!.GetValue<bool>());
            Assert(f["FreeExplorationProfiles"]!["4"]!["StartFloor"]!.GetValue<int>() == 31);
            Assert(f["FreeExplorationProfiles"]!["2"]!["Cycles"]!.GetValue<int>() == 3);
            Assert(f["Preferences"]!["Hoard:Create"]!["Cycles"]!.GetValue<int>() == 7);
            Assert(f["CheckpointEntry"]!["ResumeSlot"]!.GetValue<int>() == 1);
            Assert(root["unrelated"]!["keep"]!.GetValue<string>() == "value" && root["Fsd"]!["Rotation"]!["Provider"]!.GetValue<int>() == 3);
            Assert(!LegacyConfigurationCompatibility.Normalize(r.Json).Changed);
        });
        Check("retired-final-boss-loads-without-automatic-start", () =>
        {
            var r = LegacyConfigurationCompatibility.Normalize(Sample(5));
            var f = JsonNode.Parse(r.Json)!["Fsd"]!["Farming"]!;
            Assert(r.Changed && f["Mode"]!.GetValue<int>() == 3 && !f["FreeExploration"]!.GetValue<bool>());
        });
        Check("existing-new-profiles-win", () =>
        {
            var raw = JsonNode.Parse(Sample(4))!;
            raw["Fsd"]!["Farming"]!["FreeExplorationProfiles"] = JsonNode.Parse("{\"4\":{\"StartFloor\":71,\"EndFloor\":80}}");
            var r = LegacyConfigurationCompatibility.Normalize(raw.ToJsonString());
            Assert(JsonNode.Parse(r.Json)!["Fsd"]!["Farming"]!["FreeExplorationProfiles"]!["4"]!["StartFloor"]!.GetValue<int>() == 71);
        });
        Check("exact-backup-idempotency-and-map-checkpoint-preservation", () =>
        {
            string dir = Path.Combine(Path.GetTempPath(), "nf3-compat-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(dir, "NewFolder3", "DetailedMapCatalogs"));
                string map = Path.Combine(dir, "NewFolder3", "DetailedMapCatalogs", "keep.txt"); File.WriteAllText(map, "map-checkpoint-sentinel");
                string path = Path.Combine(dir, "NewFolder3.json"); File.WriteAllText(path, Sample(4)); byte[] before = File.ReadAllBytes(path);
                var r = LegacyConfigurationCompatibility.PrepareFile(path);
                Assert(r.Changed && File.ReadAllBytes(r.BackupPath!).SequenceEqual(before));
                Assert(!LegacyConfigurationCompatibility.PrepareFile(path).Changed && File.ReadAllText(map) == "map-checkpoint-sentinel");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        });
        return count;
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Legacy configuration contract failed."); }
}
