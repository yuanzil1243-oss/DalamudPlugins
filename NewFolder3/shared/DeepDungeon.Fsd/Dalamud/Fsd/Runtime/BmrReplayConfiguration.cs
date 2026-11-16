namespace DeepDungeon.Fsd.Dalamud.Runtime;

internal static class BmrReplayConfiguration
{
    internal static bool IsEnabled(IReadOnlyList<string>? response) =>
        response is { Count: 1 } && bool.TryParse(response[0], out bool enabled) && enabled;
}
