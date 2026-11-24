using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using global::Dalamud.Bindings.ImGui;
using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private string _localPausePath = string.Empty;
    private FloorsetPauseCheckpoint? _localPauseCheckpoint;
    private string? _localPauseHash, _localPauseIssue, _localPauseNotice;
    private bool _localPauseFilePresent;
    private LocalPauseRequest? _localPauseRequest;
    private CurrentDutyResumeScenario? _localCurrentDutyResume;
    private bool LocalFloorsetPauseLocked => _localPauseFilePresent || _localPauseRequest != null;

    private void LoadLocalFloorsetPause(string directory)
    {
        _localPausePath = Path.Combine(directory, "FsdFloorsetPause.json");
        try
        {
            byte[] bytes = File.ReadAllBytes(_localPausePath);
            _localPauseFilePresent = true;
            _localPauseHash = PauseCheckpointArchive.Hash(bytes);
            var saved = JsonSerializer.Deserialize<FloorsetPauseCheckpoint>(bytes);
            if (saved?.IsValid() != true) throw new InvalidDataException("暫停紀錄內容無效。");
            _localPauseCheckpoint = saved;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex)
        {
            _localPauseFilePresent = File.Exists(_localPausePath);
            _localPauseIssue = "無法載入本組暫停紀錄：" + ex.Message;
            Service.Log.Error(ex, "[LocalExtensions.FloorsetPause] Checkpoint load failed");
        }
    }

    private void UpdateLocalPauseLifecycle()
    {
        if (!IsRunActive) { _localPauseRequest = null; _localCurrentDutyResume = null; }
    }

    private void DrawLocalFloorsetPauseControls(bool running, bool inDuty)
    {
        var farming = _ddHost?.ActiveFarming;
        bool canRequest = running && !ProgressionPauseLocked && !_ddHost!.CurrentStatusIsError &&
            (farming != null && FloorsetPausePolicy.Supports(farming.Session.Plan) ||
             _localCurrentDutyResume is { PauseAfterGroup: false });
        if (canRequest)
        {
            ImGui.SameLine();
            if (ImGui.Button("本組十層出本後暫停##localFloorsetPause")) RequestLocalFloorsetPause();
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("本組按原模式完成並出本後暫停。挖寶等模式仍完成原定清理；深層攻略驗證下一組存檔後暫停；採集模式在原定出本點暫停。有後續任務才保存續跑紀錄。異常退出保檔停止，不自動重開。");
        }
        if (ProgressionPauseLocked && !running)
        {
            ImGui.SameLine();
            ImGui.BeginDisabled(inDuty || !CanReleasePauseLock(out _));
            if (ImGui.Button("解除續跑鎖定（保留存檔）##localReleasePause")) ReleaseLocalPauseLock();
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip("僅在副本外且自動化已停止時可用。歸檔本地續跑紀錄，取消原任務的自動續跑／刪檔權限；遊戲存檔及設定不刪除。解除後可改選其他存檔，原檔仍可手動重新選取。");
        }
    }

    private void DrawLocalPauseStatus()
    {
        if (_localPauseCheckpoint is { } checkpoint)
            ImGui.TextWrapped($"本組暫停紀錄 · {checkpoint.Plan.DisplayName} · " +
                (checkpoint.Slot is { } slot ? $"存檔 {slot.Index + 1}，第 {slot.StartFloor} 層" : "繼續時開始下一輪") +
                $" · 已完成 {checkpoint.CompletedCycles} 輪");
        else if (_localPauseRequest != null)
            ImGui.TextWrapped("已要求本組出本並完成原定收尾後暫停。");
        else if (IsRunActive && _ddHost?.Context?.FarmingPlan?.CurrentDutyOnly == true)
            ImGui.TextWrapped("繼續探索原本即在本組出本後保檔停止，不建立續跑鎖定。");
        if (_localPauseIssue != null) ImGui.TextWrapped(_localPauseIssue);
        if (_localPauseNotice != null) ImGui.TextWrapped(_localPauseNotice);
    }

    private void RequestLocalFloorsetPause()
    {
        if (IsRunActive && _localCurrentDutyResume is { } current)
        {
            current.PauseAfterGroup = true;
            _localPauseNotice = "已要求本組出本後保檔停止；目前副本續接不建立跨組續跑鎖定。";
            return;
        }
        if (ProgressionPauseLocked || _ddHost is not { FsdActive: true, ActiveFarming: { } farming } host) return;
        var session = farming.Session;
        ulong character = Service.PlayerState.ContentId;
        uint job = Service.LocalPlayer?.ClassJob.RowId ?? 0;
        if (!FloorsetPausePolicy.Supports(session.Plan) || character == 0 || job == 0)
        { _farmingError = "目前任務或角色狀態無法要求本組後暫停。"; return; }
        var plan = session.Plan with { TargetCycles = host.TargetLoops, Infinite = host.Infinite,
            BandedEnabled = session.BandedEnabled, OpenGold = session.OpenGold,
            OpenSilver = session.OpenSilver, OpenBronze = session.OpenBronze };
        _localPauseRequest = new(host.CompletedLoops, (kind, slot, completed, failures) =>
            PersistLocalFloorsetPause(new(character, job, plan, kind, slot, completed, failures)));
        session.LocalPause = _localPauseRequest;
        _localPauseNotice = null; _farmingError = null;
        Service.Log.Info($"[LocalExtensions.FloorsetPause] Requested: mode={plan.Mode}, dungeon={plan.DungeonId}");
    }

    private string? PersistLocalFloorsetPause(FloorsetPauseCheckpoint checkpoint)
    {
        if (!checkpoint.IsValid() || Service.PlayerState.ContentId != checkpoint.Character ||
            Service.LocalPlayer?.ClassJob.RowId != checkpoint.Job)
            return "暫停續跑資料或角色／職業無法驗證；保留存檔並停止，未寫入續跑紀錄。";
        // Keep the in-memory record even on disk failure; never silently continue another attempt.
        _localPauseCheckpoint = checkpoint; _localPauseFilePresent = true;
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
            Directory.CreateDirectory(Path.GetDirectoryName(_localPausePath)!);
            string temporary = _localPausePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, _localPausePath, false); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            _localPauseHash = PauseCheckpointArchive.Hash(bytes); _localPauseIssue = null;
            return null;
        }
        catch (Exception ex)
        {
            _localPauseIssue = "寫入暫停紀錄失敗；已停止，請先保留現場並檢查日誌。";
            Service.Log.Error(ex, "[LocalExtensions.FloorsetPause] Persist failed");
            return _localPauseIssue;
        }
    }

    private FsdControlResult ResumeLocalFloorsetPause()
    {
        if (_localPauseCheckpoint is not { } checkpoint || !checkpoint.IsValid())
            return new(false, _localPauseIssue ?? "暫停紀錄不可用，可在副本外解除續跑鎖定。");
        if (!CanReleasePauseLock(out string error)) return new(false, error);
        if (_checkpoint != null) return new(false, "同時存在兩份暫停任務，請先解除續跑鎖定並重新選檔。");
        if (Service.LocalPlayer?.ClassJob.RowId != checkpoint.Job)
            return new(false, "請切回暫停時的職業再繼續。");
        if (checkpoint.Plan.DungeonId is 1 or 2 && checkpoint.Job is not (23 or 31 or 38))
            return new(false, "保留原作者職業支援範圍：死宮／天宮須使用機工士、吟遊詩人或舞者。");
        var session = new FarmingSession(checkpoint.Plan);
        session.RestoreLocalFailureCount(checkpoint.Failures);
        if (checkpoint.Slot is { } slot)
            session.RestoreCheckpoint(slot, checkpoint.Failures, checkpoint.Character,
                ConsumeLocalPauseCheckpoint, reason => _localPauseIssue = reason);
        string? mapKey = checkpoint.Plan.ShowsDetailedMap ? checkpoint.Plan.StartFloor == 21
            ? DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key
            : DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key : null;
        bool started = TryStartOutsideDutyFsd(() => new FarmingScenario(session),
            checkpoint.Plan.TargetCycles, checkpoint.Plan.Infinite, mapKey, out error,
            resumePausedProgression: true, completedLoops: checkpoint.CompletedCycles);
        // A fresh cycle has no previous save to pin. Consume before the first framework entry tick.
        if (started && checkpoint.Kind == FloorsetResumeKind.RestartCycle && ConsumeLocalPauseCheckpoint() is { } consumeError)
        { _ddHost?.StopFsd("local-pause-consume-failed"); return new(false, consumeError); }
        return new(started, started ? null : error);
    }

    private string? ConsumeLocalPauseCheckpoint()
    {
        try
        {
            if (_localPauseCheckpoint is not { } checkpoint ||
                Service.PlayerState.ContentId != checkpoint.Character || Service.LocalPlayer?.ClassJob.RowId != checkpoint.Job)
                return "繼續期間角色或職業變更，停止並保留存檔。";
            if (File.Exists(_localPausePath))
            {
                if (_localPauseHash == null) return "暫停檔案狀態不明，未消耗續跑紀錄。";
                PauseCheckpointArchive.Archive(_localPausePath, _localPauseHash);
            }
            else if (_localPauseHash != null) return "暫停檔案已被其他操作移除，停止並重新核對。";
            _localPauseCheckpoint = null; _localPauseFilePresent = false; _localPauseHash = null;
            _localPauseRequest = null; _localPauseIssue = null;
            return null;
        }
        catch (Exception ex)
        { Service.Log.Error(ex, "[LocalExtensions.FloorsetPause] Consume failed"); return "無法歸檔續跑紀錄，FSD 已停止。"; }
    }

    private bool CanReleasePauseLock(out string error)
    {
        error = "請在副本外、載入完成且所有 FSD／入場／退本／刪檔操作停止後再操作。";
        if (IsRunActive || _ddHost?.AssistModeActive == true || _currentInDeepDungeon || _dutyState?.IsInDuty == true ||
            DutyState.IsClientTransitioning || Service.Condition[ConditionFlag.InCombat] ||
            Service.LocalPlayer is not { IsDead: false } || _bridgeLeaveDutyContext != null ||
            _bridgeDeleteSaveContext != null || _bridgeDeleteSaveFlow != null) return false;
        ulong expected = _localPauseCheckpoint?.Character ?? _checkpoint?.Character ?? 0;
        if (expected != 0 && Service.PlayerState.ContentId != expected)
        { error = "請使用暫停任務時的角色操作。"; return false; }
        error = string.Empty;
        return true;
    }

    private void ReleaseLocalPauseLock()
    {
        if (!CanReleasePauseLock(out string error)) { _farmingError = error; return; }
        try
        {
            // Validate both records before moving either. A changed file never gets discarded.
            if (File.Exists(_localPausePath) && (_localPauseHash == null ||
                PauseCheckpointArchive.Hash(File.ReadAllBytes(_localPausePath)) != _localPauseHash))
                throw new IOException("本組暫停檔案已變更，請重新載入後核對。");
            string? originalHash = null;
            if (File.Exists(_checkpointPath))
            {
                byte[] bytes = File.ReadAllBytes(_checkpointPath);
                if (_checkpoint == null || JsonSerializer.Deserialize<ProgressionCheckpoint>(bytes) != _checkpoint)
                    throw new IOException("原版暫停檔案已變更，請重新載入後核對。");
                originalHash = PauseCheckpointArchive.Hash(bytes);
            }
            if (File.Exists(_localPausePath)) PauseCheckpointArchive.Archive(_localPausePath, _localPauseHash!);
            if (originalHash != null) PauseCheckpointArchive.Archive(_checkpointPath, originalHash);
            _localPauseCheckpoint = null; _localPauseFilePresent = false; _localPauseHash = null;
            _localPauseRequest = null; _localPauseIssue = null; _checkpoint = null; _checkpointIssue = null;
            // Discard stale UI snapshots so the next task must explicitly select a fresh native save list.
            _externalSelected = null; _externalSlots = []; _farmingError = null;
            _localPauseNotice = "已解除續跑鎖定，原遊戲存檔保留；可重新選擇其他存檔或模式。";
            Service.Log.Info("[LocalExtensions.FloorsetPause] Resume lock released; task records archived, game saves untouched.");
        }
        catch (Exception ex)
        { _farmingError = "解除失敗，未放開續跑鎖定：" + ex.Message; Service.Log.Error(ex, "[LocalExtensions.FloorsetPause] Release failed"); }
    }
}
