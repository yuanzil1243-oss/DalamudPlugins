using DeepDungeon.Fsd.Runtime;
using global::Dalamud.IoC;
using global::Dalamud.Plugin.Services;

namespace DeepDungeon.Fsd.Dalamud;

internal sealed class AeTargetSelectorDtr
{
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;

    internal AeTargetSelectorState Read()
    {
        try
        {
            // Entries is read-only and includes user-hidden entries. Never call
            // Get/Remove or an OnClick handler belonging to another plugin.
            return AeTargetSelectorReadback.Read(DtrBar.Entries.Select(e => (e.Title, e.Text?.TextValue)));
        }
        catch { return AeTargetSelectorState.Unknown; }
    }
}
