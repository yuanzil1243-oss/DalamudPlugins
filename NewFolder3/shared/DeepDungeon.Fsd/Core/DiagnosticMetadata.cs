namespace DeepDungeon.Fsd.Core;

public static class DiagnosticMetadata
{
    // Metadata supplied by other plugins must never interrupt accepted control requests.
    public static string? Read(Func<string?> getter)
    {
        try { return getter(); }
        catch { return null; }
    }
}
