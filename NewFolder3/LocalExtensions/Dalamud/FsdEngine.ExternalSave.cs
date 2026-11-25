using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Entry;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    // Deliberately session-local. Reloading never selects or starts a saved task.
    private bool _useExternalSave;
    private SaveSlotSnapshot[] _externalSlots = [];
    private SaveSlotSnapshot? _externalSelected;
    private uint _externalDungeon, _externalJob;
    private ulong _externalCharacter;

    private void DrawExternalSaveSelection(bool running)
    {
        if (_currentInDeepDungeon || _dutyState?.IsInDuty == true) return;
        ImGui.BeginDisabled(running);
        bool existing = _useExternalSave;
        if (ImGui.Checkbox("使用已有存檔續打", ref existing))
        {
            _useExternalSave = existing;
            _externalSelected = null;
            _externalSlots = [];
            _farmingError = null;
        }
        if (_useExternalSave)
        {
            string dungeonName = _configuration.Farming.DungeonId switch { 1 => "死者宮殿", 2 => "天之御柱", _ => "朝聖交錯路" };
            ImGui.TextWrapped($"請先打開挑戰「{dungeonName}」的存檔面板，再點擊「讀取原生存檔列表」讀取資料並選擇槽位。按所選模式續打；保檔與重開規則由下方選項決定。");
            if (ImGui.Button("讀取原生存檔列表"))
            {
                _externalSelected = null;
                _externalSlots = [];
                uint dungeon = _configuration.Farming.DungeonId;
                if (TryReadExternalSaveList(dungeon, out var slots, out var error))
                {
                    _externalSlots = slots.ToArray();
                    _externalDungeon = dungeon;
                    _externalCharacter = Service.PlayerState.ContentId;
                    _externalJob = Service.LocalPlayer?.ClassJob.RowId ?? 0;
                    _farmingError = null;
                }
                else _farmingError = error;
            }
            foreach (var slot in _externalSlots)
            {
                bool usable = !slot.Empty && slot.Enterable && slot.Index is 0 or 1;
                ImGui.BeginDisabled(!usable);
                bool selected = _externalSelected == slot;
                if (ImGui.RadioButton($"存檔 {slot.Index + 1}：{(slot.Empty ? "空檔" : slot.Progress)} — {(slot.Enterable ? "原生允許入場" : "原生禁止入場／已完成或失敗")}##existing{slot.Index}", selected))
                    _externalSelected = slot;
                ImGui.EndDisabled();
            }
            ImGui.TextWrapped("出現原生風險提示時，請閱讀並手動決定；插件不自動確認未知對話。");
        }
        ImGui.EndDisabled();
    }

    private bool TryReadExternalSaveList(uint dungeon, out IReadOnlyList<SaveSlotSnapshot> slots, out string error)
    {
        slots = [];
        error = "請在副本外、載入完成後，打開對應地宮的原生讀檔列表。";
        if (_currentInDeepDungeon || _dutyState?.IsInDuty == true || DutyState.IsClientTransitioning ||
            Service.LocalPlayer is not { IsDead: false } || Service.PlayerState.ContentId == 0) return false;
        try
        {
            if (!PtEntryText.TryForDungeon(dungeon, out var text)) return false;
            var ui = EntryUiReader.Read(text, 1);
            if (ui.Kind != EntryUiKind.ReadSaves || ui.Slots is not { Count: 2 } read) return false;
            slots = read;
            error = string.Empty;
            return true;
        }
        catch (Exception failure)
        {
            error = "無法可靠讀取原生存檔列表；未進行入場操作：" + failure.Message;
            Service.Log.Warning(failure, "[FSD.ExternalSave] Read failed");
            return false;
        }
    }

    private FsdControlResult StartExternalSave(FsdStartRequest request, uint dungeon)
    {
        if (request.StopAfterFloor.HasValue || request.DiagnosticStartFloor.HasValue || request.ResumeSlot.HasValue ||
            request.HoldOnFailure || request.HoldAfterClear)
            return new(false, "正式已有檔續打不能混用診斷或副本內接管參數。");
        if (_externalSelected is not { } selected || _externalDungeon != dungeon ||
            _externalCharacter != Service.PlayerState.ContentId ||
            _externalJob == 0 || _externalJob != Service.LocalPlayer?.ClassJob.RowId)
            return new(false, "地宮、角色、職業或選檔已變更；請重新讀取並選擇存檔。");
        if (dungeon is 1 or 2 && _externalJob is not (23 or 31 or 38))
            return new(false, "保留原作者支援範圍：死宮／天宮須使用機工士、吟遊詩人或舞者。");
        if (!TryReadExternalSaveList(dungeon, out var fresh, out var error) ||
            !ExternalSavePolicy.TryValidate(selected, fresh, dungeon, out error)) return new(false, error);
        if (!TryCreateContinuationPlan(dungeon, selected.StartFloor, _externalCharacter, _externalJob,
                out var plan, out error)) return new(false, error);
        return StartModeContinuation(plan!, selected);
    }

    private void ValidateExternalSaveCharacter()
    {
        if (!IsRunActive || _ddHost?.Context?.FarmingPlan is not { ExternalSave: true } plan ||
            DutyState.IsClientTransitioning || Service.LocalPlayer == null) return;
        if (!ExternalSavePolicy.MatchesCharacter(plan, Service.PlayerState.ContentId,
                Service.LocalPlayer.ClassJob.RowId))
        {
            _farmingError = "已有檔續打的角色或職業已變更；停止並保留存檔。";
            _ddHost.StopFsd("external-save-identity-changed");
        }
    }
}
