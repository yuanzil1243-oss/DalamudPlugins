namespace DeepDungeon.Fsd.Core;

public enum SaveHistoryFact { Unknown, Confirmed, Disqualified }

/// <summary>Current floorset/party/job never establish historical achievement eligibility.</summary>
public readonly record struct SaveHistoryEvidence(SaveHistoryFact StartedAtOne,
    SaveHistoryFact SoloThroughout, SaveHistoryFact ZeroPartyKo)
{
    public bool IsCandidate(uint dungeonId) => dungeonId == 1 &&
        StartedAtOne == SaveHistoryFact.Confirmed && SoloThroughout == SaveHistoryFact.Confirmed &&
        ZeroPartyKo == SaveHistoryFact.Confirmed;
}

public static class ExternalSavePolicy
{
    public static bool TryValidate(SaveSlotSnapshot selected, IReadOnlyList<SaveSlotSnapshot> fresh,
        uint dungeonId, out string error)
    {
        int maxFloor = dungeonId == 1 ? 200 : 100;
        if (dungeonId is not (1 or 2 or 4) || selected.Index is < 0 or > 1 ||
            selected.Empty || !selected.Enterable || selected.RowFingerprint is not { Length: 64 } ||
            string.IsNullOrWhiteSpace(selected.Identity))
        {
            error = "來源、身份或可入場狀態未確認；請重新讀取原生存檔列表。";
            return false;
        }
        if (!PreparedSavePolicy.TryResolve(fresh, null, false, selected.StartFloor,
                out var source, out error, selected.Index, maxFloor)) return false;
        if (source != selected)
        {
            error = "選中的存檔已變更；請重新讀取並選擇，不會改選其他檔案。";
            return false;
        }
        return true;
    }

    public static bool MatchesCharacter(FarmingPlan plan, ulong character, uint job) =>
        !plan.ExternalSave || character != 0 && character == plan.ExternalSaveCharacter &&
        job != 0 && job == plan.ExternalSaveJob;
}
