using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private void DrawFreeExplorationSelection(bool running)
    {
        var settings = _configuration.Farming;
        var preferences = settings.GetFreeExplorationPreferences();
        DrawExternalSaveSelection(running);
        ImGui.BeginDisabled(running);
        bool inDuty = _currentInDeepDungeon || _dutyState?.IsInDuty == true;
        bool continuation = inDuty || _useExternalSave;
        int start = inDuty && _dutyState is { Floor: > 0 } duty ? (duty.Floor - 1) / 10 * 10 + 1
            : _useExternalSave && _externalSelected is { } selected ? selected.StartFloor : preferences.StartFloor;
        bool changed = false;
        if (continuation)
            ImGui.TextWrapped(inDuty ? "起點沿用目前進度；只需選擇結束樓層。"
                : _externalSelected is { } source ? $"起點沿用存檔進度：{source.StartFloor} 層。"
                : "請先讀取並選擇已有存檔。");
        else
        {
            int[] starts = FreeExplorationPolicy.NewSaveStarts(settings.DungeonId);
            int index = Math.Max(0, Array.IndexOf(starts, start));
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("起始樓層##freeStart", ref index, starts.Select(x => $"{x} 層").ToArray(), starts.Length))
            {
                preferences.StartFloor = start = starts[index]; changed = true;
            }
        }
        int max = settings.DungeonId == 1 ? 200 : 100;
        if (start >= 1 && start <= max - 9 && start % 10 == 1)
        {
            int[] ends = Enumerable.Range(0, (max - start + 1) / 10).Select(x => start + 9 + x * 10).ToArray();
            int index = Math.Max(0, Array.IndexOf(ends, preferences.EndFloor));
            if (!ends.Contains(preferences.EndFloor)) { preferences.EndFloor = ends[index]; changed = true; }
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("結束樓層##freeEnd", ref index, ends.Select(x => $"{x} 層").ToArray(), ends.Length))
            { preferences.EndFloor = ends[index]; changed = true; }
        }
        ImGui.TextWrapped("到達結束樓層才計 1 輪。續打後重開時，從不超過終點的最高可新建起點開始；起點須已解鎖。");
        ImGui.EndDisabled();
        if (changed) _configuration.Save();
    }

    private bool TryResolveSelectedExternalSave(uint dungeon, out SaveSlotSnapshot selected, out string error)
    {
        selected = _externalSelected.GetValueOrDefault();
        error = "地宮、角色、職業或選檔已變更；請重新讀取並選擇存檔。";
        if (_externalSelected == null || _externalDungeon != dungeon ||
            _externalCharacter != Service.PlayerState.ContentId || _externalJob == 0 ||
            _externalJob != Service.LocalPlayer?.ClassJob.RowId) return false;
        return TryReadExternalSaveList(dungeon, out var fresh, out error) &&
            ExternalSavePolicy.TryValidate(selected, fresh, dungeon, out error);
    }

    private FsdControlResult StartFreeExploration(FsdStartRequest request, uint dungeon)
    {
        if (request.StopAfterFloor.HasValue || request.DiagnosticStartFloor.HasValue || request.ResumeSlot.HasValue ||
            request.HoldOnFailure || request.HoldAfterClear || request.Scenario == FsdStartScenario.ControlledSurvey)
            return new(false, "自由探索不能混用診斷起點、停止層或保留現場參數。");
        if (dungeon is 1 or 2 && Service.LocalPlayer?.ClassJob.RowId is not (23 or 31 or 38))
            return new(false, "保留原作者職業支援範圍：死宮／天宮須使用機工士、吟遊詩人或舞者。");
        var preferences = _configuration.Farming.GetFreeExplorationPreferences();
        SaveSlotSnapshot selected = default;
        if (_useExternalSave && !TryResolveSelectedExternalSave(dungeon, out selected, out var selectionError))
            return new(false, selectionError);
        if (_useExternalSave)
        {
            if (!TryCreateContinuationPlan(dungeon, selected.StartFloor, _externalCharacter, _externalJob,
                    out var continuedPlan, out var continuedError)) return new(false, continuedError);
            return StartModeContinuation(continuedPlan!, selected);
        }
        int cycles = preferences.KeepSave ? 1 : preferences.Cycles;
        bool infinite = !preferences.KeepSave && preferences.Infinite;
        if (!FreeExplorationPolicy.TryCreate(dungeon, preferences.StartFloor,
                preferences.EndFloor, cycles, infinite, new(preferences.BandedEnabled == true,
                    preferences.OpenGold, preferences.OpenSilver, preferences.OpenBronze),
                preferences.AggressiveChestInteraction, false, out var plan, out var error,
                preferences.BlindSearchRangeEnabled, preferences.BlindSearchStartFloor, preferences.BlindSearchEndFloor))
            return new(false, error);
        plan = TaskContinuationPolicy.WithRetention(plan!, preferences.KeepSave);
        var session = new FarmingSession(plan);
        bool started = TryStartOutsideDutyFsd(() => CreateLocalFarmingScenario(session), cycles, infinite, null, out error);
        if (started) { _farmingError = null; _configuration.Save(); }
        return new(started, error);
    }
}
