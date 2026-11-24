namespace DeepDungeon.Fsd.Runtime;

/// <summary>Optional output telemetry must never change a successful or failed control result.</summary>
public static class RotationControlDiagnostics
{
    public static void Record(FsdRotationProvider provider, Func<(string? Name, Version? Version)> readPlugin,
        Action<string?, string?> writeReplay, Action<Exception> reportError)
    {
        try
        {
            // AE is controlled through registered IPC, and Custom through commands.
            // Neither needs a loader manifest, installed-plugin lookup or version.
            var plugin = provider is FsdRotationProvider.AEAssist or FsdRotationProvider.Custom
                ? default : readPlugin();
            // Development loaders can be installed without a manifest version.
            writeReplay(plugin.Name, plugin.Version?.ToString());
        }
        catch (Exception error)
        {
            // This boundary encloses diagnostics only, never IPC or output control.
            try { reportError(error); }
            catch (Exception) { /* A diagnostic sink failure cannot stop the host either. */ }
        }
    }
}
