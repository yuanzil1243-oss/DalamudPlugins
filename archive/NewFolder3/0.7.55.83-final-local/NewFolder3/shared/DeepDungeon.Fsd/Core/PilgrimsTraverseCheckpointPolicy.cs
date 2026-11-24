using System.Text.RegularExpressions;

namespace DeepDungeon.Fsd.Core;

public enum CheckpointMenuResult { NotFloorset, Selected, Missing, Ambiguous }

public static class PilgrimsTraverseCheckpointPolicy
{
    public static IReadOnlyList<int> NewSaveStarts { get; } = Array.AsReadOnly(new[] { 1, 21, 31, 51, 71 });
    public static bool IsNewSaveStart(int floor) => NewSaveStarts.Contains(floor);
    public static bool IsStopFloor(int start, int stop) => IsNewSaveStart(start) &&
        stop >= start + 9 && stop <= 100 && stop % 10 == 0;

    // Read the first complete number: floor 1 must never match floor 21 or 31.
    // Only recognize floor labels (or a bare checkpoint number), not unrelated dialog numbers.
    public static CheckpointMenuResult SelectOption(IReadOnlyList<string> options, int start, out int index)
    {
        index = -1;
        bool recognized = false;
        for (int i = 0; i < options.Count; ++i)
        {
            string text = options[i].Normalize(System.Text.NormalizationForm.FormKC);
            var match = Regex.Match(text, @"\d+");
            if (!match.Success || !int.TryParse(match.Value, out int floor) || floor is < 1 or > 100) continue;
            if (!Regex.IsMatch(text, @"圣路|聖路|層|层|階|floor", RegexOptions.IgnoreCase) &&
                (!IsNewSaveStart(floor) || text.Trim() != match.Value)) continue;
            recognized = true;
            if (floor != start) continue;
            if (index >= 0) { index = -1; return CheckpointMenuResult.Ambiguous; }
            index = i;
        }
        return index >= 0 ? CheckpointMenuResult.Selected : recognized ? CheckpointMenuResult.Missing : CheckpointMenuResult.NotFloorset;
    }
}
