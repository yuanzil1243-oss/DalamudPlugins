namespace DeepDungeon.Fsd.Core;

/// <summary>Current-duty attachment needs live duty facts, never a save-slot identity.</summary>
public readonly record struct ContinueExplorationFacts(
    bool InDeepDungeonTerritory, bool Running, bool InDuty, bool ObservationValid,
    bool Transitioning, bool PlayerAlive, ulong Character, uint Job,
    uint DungeonId, uint TerritoryDungeonId, int Floor, int RangeStart, int RangeEnd,
    bool FloorClassified);

public static class ContinueExplorationPolicy
{
    public static bool IsVisible(bool inDeepDungeonTerritory, bool running) =>
        inDeepDungeonTerritory && !running;

    public static bool TryAttach(in ContinueExplorationFacts f, out int groupStart, out string error)
    {
        groupStart = 0;
        int maxFloor = f.DungeonId == 1 ? 200 : 100;
        error = f.Running ? "FSD 已在執行。"
            : !f.InDeepDungeonTerritory || !f.InDuty ? "請進入深宮副本後再繼續探索。"
            : f.Transitioning ? "請等待載入或過場結束。"
            : !f.PlayerAlive || f.Character == 0 || f.Job == 0 ? "請等待角色存活且資料載入完成。"
            : !f.ObservationValid || !f.FloorClassified ? "等待可靠的地宮與樓層資料。"
            : f.DungeonId is not (1 or 2 or 4) ? "此地宮尚未支援 FSD。"
            : f.DungeonId != f.TerritoryDungeonId || f.Floor < 1 || f.Floor > maxFloor ||
                f.RangeStart < 1 || f.RangeEnd > maxFloor || f.RangeEnd - f.RangeStart != 9 ||
                f.RangeStart % 10 != 1 || f.Floor < f.RangeStart || f.Floor > f.RangeEnd
                ? "目前區域與地宮樓層不符，等待有效觀測。"
            : f.DungeonId is 1 or 2 && f.Job is not (23 or 31 or 38)
                ? "保留原作者支援範圍：死宮／天宮須使用機工士、吟遊詩人或舞者。"
            : string.Empty;
        if (error.Length != 0) return false;
        groupStart = (f.Floor - 1) / 10 * 10 + 1;
        return true;
    }
}

public static class OrdinaryDescentPolicy
{
    // Same new-save entry floors as the upstream DungeonCatalog / PtEntryText.
    public static int HighestStartFloor(uint dungeonId) => dungeonId switch
    {
        1 => 51,
        2 => 21,
        4 => 71,
        _ => 0
    };

    public static bool IsValidStart(uint dungeonId, FarmingMode mode, SaveUse saveUse, int floor) =>
        mode == FarmingMode.DeepProgression && saveUse == SaveUse.Create &&
        HighestStartFloor(dungeonId) is > 0 and var highest && floor == highest;
}
