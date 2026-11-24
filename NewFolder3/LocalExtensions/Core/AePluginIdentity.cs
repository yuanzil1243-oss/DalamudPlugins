namespace DeepDungeon.Fsd.Core;

/// <summary>Plugin identities only; IPC endpoints retain the AEAssist prefix in V3.</summary>
public static class AePluginIdentity
{
    public static bool Matches(string? internalName) =>
        string.Equals(internalName, "AEAssist", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(internalName, "AEAssistV3", StringComparison.OrdinalIgnoreCase);
}
