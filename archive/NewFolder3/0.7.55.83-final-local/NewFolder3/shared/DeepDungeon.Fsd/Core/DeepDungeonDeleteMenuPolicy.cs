namespace DeepDungeon.Fsd.Core;

/// <summary>Select the reset action by meaning; unlocked challenges can move its native row.</summary>
public static class DeepDungeonDeleteMenuPolicy
{
    public static int FindResetProgress(IReadOnlyList<string> labels)
    {
        int selected = -1;
        for (int i = 0; i < labels.Count; i++)
        {
            string label = Normalize(labels[i]);
            if (label is not ("重置攻略进度" or "重置进度" or "重置攻略進度" or "重置進度" or
                "resetprogress" or "resetyourprogress" or "攻略状況をリセットする" or "攻略進行度をリセットする")) continue;
            if (selected >= 0) return -1;
            selected = i;
        }
        return selected;
    }

    private static string Normalize(string? label) => string.IsNullOrWhiteSpace(label)
        ? string.Empty
        : string.Concat(label.Normalize(System.Text.NormalizationForm.FormKC)
            .Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c))).ToLowerInvariant();
}
