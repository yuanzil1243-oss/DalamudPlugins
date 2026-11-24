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
        int start = _useExternalSave && _externalSelected is { } selected ? selected.StartFloor : preferences.StartFloor;
        bool changed = false;
        if (_useExternalSave)
            ImGui.TextWrapped(_externalSelected is { } source
                ? $"起始樓層：{source.StartFloor}（已選存檔；只續打一次並保檔停止）"
                : "請先讀取並選擇已有存檔，以確定起始樓層。");
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
        ImGui.TextWrapped("完整完成所選層段才計 1 輪；中間十層組及失敗不計完成輪數。新建起點須原生已解鎖；終點按十層組正常結算，終局獎勵沿用原版。");
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
        int cycles = _useExternalSave ? 1 : preferences.Cycles;
        bool infinite = !_useExternalSave && preferences.Infinite;
        if (!FreeExplorationPolicy.TryCreate(dungeon, _useExternalSave ? selected.StartFloor : preferences.StartFloor,
                preferences.EndFloor, cycles, infinite, new(preferences.BandedEnabled == true,
                    preferences.OpenGold, preferences.OpenSilver, preferences.OpenBronze),
                preferences.AggressiveChestInteraction, _useExternalSave, out var plan, out var error))
            return new(false, error);
        if (_useExternalSave)
            plan = plan! with { ExternalSaveCharacter = _externalCharacter, ExternalSaveJob = _externalJob };
        var session = new FarmingSession(plan!, _useExternalSave ? selected.Index : -1);
        if (_useExternalSave)
        {
            var pinnedPlan = plan!;
            session.RestoreCheckpoint(selected, 0, _externalCharacter,
                () => ExternalSavePolicy.MatchesCharacter(pinnedPlan, Service.PlayerState.ContentId,
                    Service.LocalPlayer?.ClassJob.RowId ?? 0) ? null : "入場期間角色或職業變更；已停止。",
                reason => _farmingError = reason);
        }
        bool started = TryStartOutsideDutyFsd(() => new FarmingScenario(session), cycles, infinite, null, out error);
        if (started) { _farmingError = null; _configuration.Save(); }
        return new(started, error);
    }
}
