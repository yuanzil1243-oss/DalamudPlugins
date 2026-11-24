using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class LocalConfigurationUpgrade
{
    internal static void Prepare(IDalamudPluginInterface pluginInterface, IPluginLog log)
    {
        string file = Path.TrimEndingDirectorySeparator(pluginInterface.GetPluginConfigDirectory()) + ".json";
        var result = LegacyConfigurationCompatibility.PrepareFile(file);
        if (result.Changed)
            log.Warning($"[LocalExtensions.Compatibility] Migrated retired local mode {result.LegacyMode}; original preserved at {result.BackupPath}. No save, checkpoint or map data was removed. Select the desired mode before starting.");
    }
}
