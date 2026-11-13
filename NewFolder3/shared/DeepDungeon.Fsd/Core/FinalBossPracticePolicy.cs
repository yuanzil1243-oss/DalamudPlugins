using System.Text;

namespace DeepDungeon.Fsd.Core;

public static class FinalBossPracticePolicy
{
    public const uint TerritoryId = 1333;
    public const uint ContentFinderId = 1065;
    public const string FixedPartyEntryPrompt = "确定以“固定队伍”进入吗？";
    public static bool IsPractice(uint territory) => territory == TerritoryId;

    public static bool CanConfirmFixedPartyEntry(bool challengeSelected, bool fixedPartySelected, string prompt) =>
        challengeSelected && fixedPartySelected &&
        Normalize(prompt) == Normalize(FixedPartyEntryPrompt);

    public static bool MatchesChallenge(string text, string dutyName)
    {
        string value = Normalize(text), expected = Normalize(dutyName);
        return expected.Length > 0 && value.Contains(expected, StringComparison.Ordinal);
    }

    public static bool TrySelectFixedParty(IReadOnlyList<string> options, out int index)
    {
        index = -1;
        for (int i = 0; i < options.Count; i++)
        {
            string text = Normalize(options[i]);
            bool fixedParty = text is "固定队伍进入" or "固定隊伍進入" or "enterwithafixedparty" or "enterwithafixedparty." or "固定パーティで突入";
            if (!fixedParty) continue;
            if (index != -1) { index = -1; return false; }
            index = i;
        }
        return index >= 0;
    }

    private static string Normalize(string text) => string.Concat((text ?? string.Empty).Normalize(NormalizationForm.FormKC)
        .Where(c => !char.IsWhiteSpace(c) && c is not '“' and not '”' and not '"' and not '「' and not '」')).ToLowerInvariant();
}
