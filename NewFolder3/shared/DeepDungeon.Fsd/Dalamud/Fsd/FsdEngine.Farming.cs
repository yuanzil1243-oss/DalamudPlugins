using global::Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private string? _farmingError;
    private bool _pauseAfterFloorsetClear;
    private static readonly string[] FarmingModeLabels =
        ["挖寶藏", "強化／陶片", "寶藏發現成就", "死靈術士挑戰", "自由探索", "尾王模式"];
    private static readonly string[] FarmingModeWithSurveyLabels =
        ["挖寶藏", "強化／陶片", "寶藏發現成就", "死靈術士挑戰", "自由探索", "尾王模式", "受控採集（研究）"];
    private static readonly string[] HoardFloorsetLabels = ["21–30 層", "31–40 層"];
    private static readonly string[] FarmingDungeonLabels = ["朝聖交錯路", "天之御柱"];

    private bool IsFixedFloorsetHoard => _configuration.Farming.DungeonId == 4 && _fsfScenarioIndex != 2 &&
        _configuration.Farming.Mode == FarmingMode.Hoard &&
        _configuration.Farming.GetPreferences(_configuration).BandedEnabled == true;

    private string? GetSelectedDetailedMapScenarioKey() => _configuration.Farming.DungeonId == 4 && (IsFixedFloorsetHoard || _fsfScenarioIndex == 2)
        ? DetailedMapCatalogManager.GetScenarioKey(_fsfScenarioIndex) : null;

    private void DrawFarmingSelection(bool running)
    {
        ImGui.BeginDisabled(running);
        var settings = _configuration.Farming;
        int dungeon = settings.DungeonId == 2 ? 1 : 0;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("地宮##fsdDungeon", ref dungeon, FarmingDungeonLabels, FarmingDungeonLabels.Length))
        {
            settings.DungeonId = dungeon == 1 ? 2u : 4u;
            if (settings.DungeonId == 2) { settings.Mode = FarmingMode.FreeExploration; _fsfScenarioIndex = 1; }
            _configuration.NecromancerFsdScenarioIndex = _fsfScenarioIndex;
            _configuration.Save(); _farmingError = null;
        }
        if (settings.DungeonId == 2)
        {
            settings.Mode = FarmingMode.FreeExploration;
            _fsfScenarioIndex = 1;
            ImGui.TextWrapped("天宮本地版：自由探索；新建1／21層、接續已有存檔、每十層接續。Boss先交由現有BMR機制，實戰後再校準。");
            DrawCheckpointEntrySelection();
            ImGui.EndDisabled();
            return;
        }
        int surveyMode = FarmingModeLabels.Length;
        int mode = _fsfScenarioIndex == 2 ? surveyMode : (int)settings.Mode;
        var labels = _detailedMapHostOptions.SupportsControlledPtSurvey ? FarmingModeWithSurveyLabels : FarmingModeLabels;
        ImGui.SetNextItemWidth(260);
        if (ImGui.Combo("模式##fsdMode", ref mode, labels, labels.Length))
        {
            if (mode == surveyMode) _fsfScenarioIndex = 2;
            else
            {
                settings.Mode = (FarmingMode)mode;
                if (settings.Mode == FarmingMode.FreeExploration && !settings.ActiveCheckpointEntry.Enabled)
                    settings.ActiveCheckpointEntry.Enabled = true;
                if (_fsfScenarioIndex == 2) _fsfScenarioIndex = 1;
            }
            _configuration.NecromancerFsdScenarioIndex = _fsfScenarioIndex;
            _configuration.Save();
            _farmingError = null;
        }
        if (_fsfScenarioIndex != 2)
        {
            if (settings.Mode == FarmingMode.Hoard)
            {
                int floor = _fsfScenarioIndex;
                ImGui.SetNextItemWidth(180);
                if (ImGui.Combo("起始層段##fsdFloorset", ref floor, HoardFloorsetLabels, 2))
                {
                    _fsfScenarioIndex = floor;
                    _configuration.NecromancerFsdScenarioIndex = floor;
                    _configuration.Save();
                }
            }
            else if (settings.Mode == FarmingMode.Aetherpool)
                ImGui.TextWrapped("準備1份較多跳層松杜香的任意起點層存檔");
            else if (settings.Mode == FarmingMode.HoardDiscovery)
                ImGui.TextWrapped("準備1份有至少1個感知寶藏的任意起點層存檔");
            else if (settings.Mode == FarmingMode.FreeExploration)
                DrawCheckpointEntrySelection();
            else if (settings.Mode == FarmingMode.FinalBoss)
                ImGui.TextWrapped("独立挑战「卓异的悲寂歼灭战」（第99圣路练习）。从入口NPC选择固定队伍进入；不新建、不读取、不删除存档，也不计作登顶。须先解锁该挑战。失败后停止供检查。");
        }
        ImGui.EndDisabled();
    }

    private void DrawCheckpointEntrySelection()
    {
        var entry = _configuration.Farming.ActiveCheckpointEntry;
        bool hoh = _configuration.Farming.DungeonId == 2;
        bool enabled = hoh || entry.Enabled;
        bool changed = false;
        if (!hoh) changed = ImGui.Checkbox("指定存檔起點／停止層數（測試）##fsd_checkpoint", ref enabled);
        entry.Enabled = enabled;
        if (enabled)
        {
            bool resumeExisting = entry.ResumeSlot.HasValue;
            int sourceMode = resumeExisting ? 1 : 0;
            string[] sourceModes = ["新建存檔", "繼續已有存檔"];
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("存檔方式##fsd_checkpoint_source", ref sourceMode, sourceModes, sourceModes.Length))
            {
                entry.ResumeSlot = sourceMode == 1 ? 0 : null;
                if (sourceMode == 0 && !(hoh ? HohLocalPolicy.IsNewSaveStart(entry.StartFloor) : PilgrimsTraverseCheckpointPolicy.IsNewSaveStart(entry.StartFloor)))
                    entry.StartFloor = hoh ? 21 : 31;
                changed = true;
            }

            if (entry.ResumeSlot is { } slot)
            {
                int slotIndex = Math.Clamp(slot, 0, 1);
                string[] slotLabels = ["第 1 槽", "第 2 槽"];
                ImGui.SetNextItemWidth(180);
                if (ImGui.Combo("存檔槽位##fsd_resume_slot", ref slotIndex, slotLabels, slotLabels.Length))
                {
                    entry.ResumeSlot = slotIndex;
                    changed = true;
                }
            }

            if (!entry.ResumeSlot.HasValue)
            {
            int[] starts = hoh ? [1, 21] : PilgrimsTraverseCheckpointPolicy.NewSaveStarts.ToArray();
            string[] startLabels = starts.Select(f => $"第 {f} 層").ToArray();
            int startIndex = Array.IndexOf(starts, entry.StartFloor);
            if (startIndex < 0) { startIndex = Array.IndexOf(starts, 31); if (startIndex < 0) startIndex = 0; entry.StartFloor = starts[startIndex]; changed = true; }
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo(entry.ResumeSlot.HasValue ? "已有存檔預期起點##fsd_checkpoint_start" : "新建存檔起點##fsd_checkpoint_start", ref startIndex, startLabels, startLabels.Length))
            {
                entry.StartFloor = starts[startIndex];
                changed = true;
            }
            }
            int[] stops = Enumerable.Range(1, 10).Select(n => n * 10).Where(f => entry.ResumeSlot.HasValue ||
                PilgrimsTraverseCheckpointPolicy.IsStopFloor(entry.StartFloor, f)).ToArray();
            int stopIndex = Array.IndexOf(stops, entry.StopAfterFloor);
            if (stopIndex < 0) { stopIndex = 0; entry.StopAfterFloor = stops[0]; changed = true; }
            string[] stopLabels = stops.Select(f => $"通關第 {f} 層後停止").ToArray();
            ImGui.SetNextItemWidth(240);
            if (ImGui.Combo("結束層數##fsd_checkpoint_stop", ref stopIndex, stopLabels, stopLabels.Length))
            { entry.StopAfterFloor = stops[stopIndex]; changed = true; }
            string sourceDescription = entry.ResumeSlot is { } selectedSlot
                ? $"继续第 {selectedSlot + 1} 槽已有存档（起点自动读取）"
                : $"新建第 {entry.StartFloor} 层存档";
            ImGui.TextWrapped($"目前設定：{sourceDescription}，通關第 {entry.StopAfterFloor} 層後停止。請點擊上方「啟動 FSD」開始。");
            if (entry.ResumeSlot.HasValue)
                ImGui.TextWrapped(hoh ? "天宮續用所選槽位；失敗後刪除該槽，從1或21層重開，不能直接從高層新建。" : "续用时自动读取所选槽位的存档起点；攻略失败后自动删除该槽存档并从失败层组起点重试。");
            ImGui.TextWrapped(hoh ? "天宮新建僅支援1／21層，高層須續用已有存檔。首輪建議21–30層驗收入場、戰鬥與退場。"
                : "自由探索使用指定起點與停止層，適合測試高層；死靈術士挑戰固定從第 1 層開始，失敗後重開，不使用測試起點。");
        }
        var preferences = _configuration.Farming.GetPreferences(_configuration);
        bool preserveSave = preferences.PreserveSaveOnCompletion;
        if (ImGui.Checkbox("完成后保留存档##fsd_free_keep_save", ref preserveSave))
        { preferences.PreserveSaveOnCompletion = preserveSave; changed = true; }
        ImGui.TextWrapped(preserveSave
            ? "通关所选结束层后保留本次存档并停止；失败仍按原流程删档重试。"
            : "默认：通关所选结束层后，验证删除本次任务存档再停止；中途接续保留存档，其他槽位不受影响。");
        if (changed) _configuration.Save();
    }

    private void DrawFarmingOptions()
    {
        ImGui.Separator();
        ImGui.Text("FSD 選項");
        ImGui.Indent();
        bool running = _ddHost?.FsdActive == true;
        bool research = _fsfScenarioIndex == 2;
        var mode = _configuration.Farming.Mode;
        var preferences = research ? null : _configuration.Farming.GetPreferences(_configuration);
        var options = running ? _ddHost?.RunOptionsProvider?.Current : null;
        bool hoard = options?.BandedEnabled ?? preferences?.BandedEnabled == true;
        bool gold = options?.OpenGold ?? preferences?.OpenGold ?? false;
        bool silver = options?.OpenSilver ?? preferences?.OpenSilver ?? false;
        bool bronze = options?.OpenBronze ?? preferences?.OpenBronze ?? false;
        bool changed;
        ImGui.BeginDisabled(running || research || _configuration.Farming.DungeonId == 2 || FarmingTargetPolicy.LocksHoard(mode));
        changed = ImGui.Checkbox("探索埋藏的寶藏", ref hoard);
        ImGui.EndDisabled();
        if (!research && _configuration.Farming.DungeonId == 4 && mode == FarmingMode.FreeExploration)
            ImGui.TextWrapped("挖寶預設關閉：優先使用感知系統提示，有寶藏後尋找指示點，無寶藏則正常開門前進；無感知時使用歷史候選點。普通層組第8、9層不新用感知，91–98層可用且時間充裕時允許為寶藏繞路。普通補給箱仍沿去門路線。");
        ImGui.BeginDisabled(research || FarmingTargetPolicy.LocksGold(mode));
        changed |= ImGui.Checkbox("開啟金寶箱", ref gold);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(research || FarmingTargetPolicy.LocksSilverBronze(mode) ||
            mode == FarmingMode.Aetherpool && silver && !bronze);
        changed |= ImGui.Checkbox("開啟銀寶箱", ref silver);
        ImGui.EndDisabled();
        ImGui.BeginDisabled(research || FarmingTargetPolicy.LocksSilverBronze(mode) ||
            mode == FarmingMode.Aetherpool && bronze && !silver);
        changed |= ImGui.Checkbox("開啟銅寶箱", ref bronze);
        ImGui.EndDisabled();
        if (changed && preferences != null)
        {
            preferences.BandedEnabled = hoard;
            preferences.OpenGold = gold; preferences.OpenSilver = silver; preferences.OpenBronze = bronze;
            _ddHost?.SetFarmingTargets(hoard, gold, silver, bronze);
            _configuration.Save();
            _farmingError = null;
        }
        ImGui.BeginDisabled(research || mode is FarmingMode.HoardDiscovery or FarmingMode.FinalBoss);
        bool aggressiveChestInteraction = _configuration.AggressiveChestInteraction;
        if (ImGui.Checkbox("我開箱會卡住很久##aggressiveChestInteraction", ref aggressiveChestInteraction))
        {
            _configuration.AggressiveChestInteraction = aggressiveChestInteraction;
            _configuration.Save();
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 32f);
            ImGui.TextUnformatted("啟用該項目會增加開箱的力度, 如幀數較低或戰鬥時很難成功開箱, 請啟用以增加對輸出或其他外掛搶佔成功率. 若沒有遇到這個問題, 請不要啟用");
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
        ImGui.EndDisabled();
        ImGui.Spacing();
        DrawCycleOptions(preferences);
        if (!research && mode is (FarmingMode.Hoard or FarmingMode.DeepProgression or FarmingMode.FreeExploration))
        {
            bool paused = _ddHost?.FarmingPaused == true;
            bool pauseAfterClear = running ? _ddHost!.PauseAfterFloorsetClear : _pauseAfterFloorsetClear;
            ImGui.BeginDisabled(paused);
            if (ImGui.Checkbox("本組十層通關後暫停##fsf_pause_after_clear", ref pauseAfterClear))
            {
                if (running) _ddHost!.SetPauseAfterFloorsetClear(pauseAfterClear);
                else _pauseAfterFloorsetClear = pauseAfterClear;
            }
            ImGui.EndDisabled();
            if (paused)
                ImGui.TextWrapped(_ddHost!.CurrentStatus);
            else if (pauseAfterClear)
                ImGui.TextWrapped("本次生效：通關退本並完成本輪必要收尾後暫停；需要重開的一輪先刪檔，中途接續則保留原檔。手動繼續後不會每組自動暫停。");
        }
        if (!research && _configuration.Farming.DungeonId == 4 && mode is (FarmingMode.DeepProgression or FarmingMode.FreeExploration))
        {
            int enemyLimit = _configuration.Combat.HighFloorEnemyLimit;
            ImGui.SetNextItemWidth(110);
            if (ImGui.InputInt("91–98層附近接戰上限##fsd_high_floor_mobs", ref enemyLimit))
            {
                _configuration.Combat.HighFloorEnemyLimit = Math.Clamp(enemyLimit, 1, 6);
                _configuration.Save();
            }
        }
        if (!research && _configuration.Farming.DungeonId == 2 && mode == FarmingMode.FreeExploration)
        {
            ImGui.TextDisabled("天宮81–89、91–99層主動多拉，固定上限6隻，不等待湊滿；91–99不帶怪跨房。");
            ImGui.TextDisabled("94–97優先使用清圖道具開門；需要拉怪時避開人馬及附近目標，誤拉人馬後停止補拉並優先處理。");
        }
        if (running)
        {
            ImGui.TextDisabled($"完成循環：{_ddHost!.CompletedLoops}/{(_ddHost.Infinite ? "∞" : _ddHost.TargetLoops)}");
            if (mode == FarmingMode.DeepProgression && !research)
                ImGui.TextDisabled($"失敗重試：{_ddHost.FarmingFailures}");
            if (mode == FarmingMode.HoardDiscovery && !research)
                ImGui.TextDisabled($"寶藏發現：{_ddHost.HoardDiscoveries}");
            if (_ddHost.Context?.FarmingPlan?.SaveUse == SaveUse.Prepared)
                ImGui.TextDisabled(_ddHost.PreparedSaveDescription);
        }
        ImGui.Unindent();
        string? error = _farmingError ?? (_ddHost?.ExplorationErrorPaused == true ? _ddHost.CurrentStatus :
            _ddHost is { FsdActive: false, LastStatusIsError: true } ? _ddHost.LastStatus : null);
        if (!string.IsNullOrEmpty(error))
        {
            ImGui.PushStyleColor(ImGuiCol.Text, new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f));
            ImGui.TextWrapped(error);
            ImGui.PopStyleColor();
        }
    }

    private void DrawCycleOptions(FarmingPreferences? preferences)
    {
        ImGui.Text("結束 FSD...");
        bool infinite = _ddHost?.FsdActive == true ? _ddHost.Infinite : preferences?.Infinite ?? _configuration.NecromancerFsdLoopInfinite;
        int cycles = _ddHost?.FsdActive == true ? _ddHost.TargetLoops : preferences?.Cycles ?? _configuration.NecromancerFsdLoopCount;
        bool changed = ImGui.Checkbox("無限循環##fsf_inf", ref infinite);
        ImGui.SameLine();
        ImGui.BeginDisabled(infinite);
        ImGui.SetNextItemWidth(90);
        changed |= ImGui.InputInt("循環次數##fsf_loops", ref cycles);
        ImGui.EndDisabled();
        if (changed)
        {
            cycles = Math.Max(1, cycles);
            if (preferences != null) { preferences.Cycles = cycles; preferences.Infinite = infinite; }
            else { _configuration.NecromancerFsdLoopCount = cycles; _configuration.NecromancerFsdLoopInfinite = infinite; }
            _configuration.Save();
            _ddHost?.SetCycleTargets(cycles, infinite);
        }
    }

    public FsdControlResult StartSelectedFarming(FsdStartRequest request)
    {
        var scenario = request.Scenario ?? (_configuration.NecromancerFsdScenarioIndex == 2
            ? FsdStartScenario.ControlledSurvey : FsdStartScenario.Farming);
        if (scenario == FsdStartScenario.ControlledSurvey && _configuration.Farming.DungeonId != 4)
            return new(false, "受控採集只支援妖宮。");
        if (scenario == FsdStartScenario.ControlledSurvey)
        {
            if (request.DiagnosticStartFloor.HasValue || request.ResumeSlot.HasValue)
                return new FsdControlResult(false, "Diagnostic checkpoints are unavailable for controlled survey capture.");
            return StartControlledPilgrimsTraverseCapture(
                Math.Max(1, _configuration.NecromancerFsdLoopCount), _configuration.NecromancerFsdLoopInfinite,
                "start-controlled-pt-capture");
        }
        var settings = _configuration.Farming;
        if (settings.DungeonId == 2) settings.Mode = FarmingMode.FreeExploration;
        var p = settings.GetPreferences(_configuration);
        bool prepared = settings.Mode is FarmingMode.Aetherpool or FarmingMode.HoardDiscovery;
        int defaultStartFloor = settings.Mode switch
        {
            FarmingMode.DeepProgression => 1,
            FarmingMode.FinalBoss => 99,
            FarmingMode.FreeExploration => settings.ActiveCheckpointEntry.ResumeSlot.HasValue ? 1 : settings.ActiveCheckpointEntry.StartFloor,
            FarmingMode.Hoard when _fsfScenarioIndex == 0 => 21,
            _ => 31
        };
        var farming = new FsdFarmingRequest(settings.Mode, settings.Mode == FarmingMode.FinalBoss ? SaveUse.None : prepared ? SaveUse.Prepared : SaveUse.Create,
            defaultStartFloor,
            p.Cycles, p.Infinite, new FarmingTargets(p.BandedEnabled == true, p.OpenGold, p.OpenSilver, p.OpenBronze), request)
            { PreserveSaveOnCompletion = p.PreserveSaveOnCompletion, DungeonId = settings.DungeonId };
        bool started = TryStartFarming(farming, out string error);
        return new FsdControlResult(started, error);
    }

    public bool TryStartFarming(FsdFarmingRequest request, out string error)
    {
        var (mode, saveUse, startFloor, cycles, infinite, targets, checkpointOptions) = request;
        var (hoard, gold, silver, bronze) = targets;
        var (stopAfterFloor, diagnosticStartFloor, resumeSlot, _) = checkpointOptions ?? new FsdStartRequest();
        if (stopAfterFloor.HasValue && (mode != FarmingMode.FreeExploration ||
            stopAfterFloor.Value is < 10 or > 100 || stopAfterFloor.Value % 10 != 0))
        {
            error = "測試停止層數只支援自由探索模式，且必須為 10 至 100 的十層邊界。";
            return false;
        }
        if (!FarmingPlan.TryCreate(mode, saveUse, startFloor, cycles, infinite, hoard, gold, silver, bronze,
                out var plan, out error, request.DungeonId)) return false;
        if (mode == FarmingMode.FinalBoss && !_configuration.BossMechanics.UsesBmrBossHandling())
        {
            error = "尾王模式需要選擇 Bossmod Reborn，以協調99層機制避讓與主動取色。";
            return false;
        }
        if (resumeSlot.HasValue && (mode != FarmingMode.FreeExploration ||
            !stopAfterFloor.HasValue || resumeSlot is < 0 or > 1))
        {
            error = "续用已有存档只支持自由探索，请选择有效槽位和结束层。";
            return false;
        }
        if (diagnosticStartFloor is { } checkpoint && (mode != FarmingMode.FreeExploration ||
            resumeSlot.HasValue || !(request.DungeonId == 2 ? HohLocalPolicy.IsNewSaveStart(checkpoint) : PilgrimsTraverseCheckpointPolicy.IsNewSaveStart(checkpoint)) ||
            !stopAfterFloor.HasValue || stopAfterFloor.Value < checkpoint + 9))
        {
            error = "自由探索新建测试支持1／21／31／51／71层，结束层须在新建起点组末或之后。";
            return false;
        }
        plan = plan! with { StopAfterFloor = stopAfterFloor, StartFloor = diagnosticStartFloor ?? plan.StartFloor,
            UsesDiagnosticCheckpoints = diagnosticStartFloor.HasValue,
            PreserveExternalSave = mode == FarmingMode.FreeExploration && resumeSlot.HasValue,
            DeleteExternalSaveOnFailure = mode == FarmingMode.FreeExploration && resumeSlot.HasValue,
            PreserveSaveOnCompletion = mode == FarmingMode.FreeExploration && request.PreserveSaveOnCompletion };
        bool resumeCurrentDuty = _dutyState?.IsInDuty == true;
        if (resumeCurrentDuty && mode != FarmingMode.FinalBoss)
        {
            var duty = _dutyState!;
            if (!HohCurrentDutyResumePolicy.Allows(plan, resumeSlot, duty.DungeonId, duty.Floor,
                duty.IsTransitioning, duty.StateReadFailed, Service.LocalPlayer is { IsDead: false, CurrentHp: > 0 },
                duty.TimerRemainingSeconds))
            {
                error = "副本内接续仅支持状态稳定的天宫自由探索；请选择原存档槽位和有效结束层。";
                return false;
            }
            // Bind to this floorset rather than treating a mid-floor restart as a new floor-one entry.
            // The selected external save is preserved on failure; recovery must never delete an unverified slot.
            plan = plan with { StartFloor = (duty.Floor - 1) / 10 * 10 + 1, DeleteExternalSaveOnFailure = false };
        }
        if (mode == FarmingMode.FinalBoss &&
            (resumeCurrentDuty || Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]) &&
            !FinalBossPracticePolicy.IsPractice(Service.ClientState.TerritoryType))
        {
            error = "尾王模式只能在副本外啟動，或接續卓異的悲寂殲滅戰練習場；不接管其他副本。";
            return false;
        }
        var session = new FarmingSession(plan, resumeSlot ?? -1, resumeCurrentDuty);
        session.SetPauseAfterFloorsetClear(_pauseAfterFloorsetClear);
        string? key = request.DungeonId == 4 && plan!.ShowsDetailedMap ? startFloor == 21
            ? DetailedMapScenarioCatalog.PilgrimsTraverse21To30.Key
            : DetailedMapScenarioCatalog.PilgrimsTraverse31To40.Key : null;
        bool started = TryStartOutsideDutyFsd(() => mode == FarmingMode.FinalBoss
            ? new FinalBossScenario(plan) : new FarmingScenario(session), cycles, infinite, key, out error);
        if (started)
        {
            _pauseAfterFloorsetClear = false;
            _farmingError = null;
            _configuration.Farming.DungeonId = request.DungeonId;
            _configuration.Farming.Mode = mode;
            _fsfScenarioIndex = mode == FarmingMode.Hoard && startFloor == 21 ? 0 : 1;
            _configuration.NecromancerFsdScenarioIndex = _fsfScenarioIndex;
            var p = _configuration.Farming.GetPreferences(_configuration);
            p.BandedEnabled = hoard; p.OpenGold = gold; p.OpenSilver = silver; p.OpenBronze = bronze;
            p.Cycles = cycles; p.Infinite = infinite;
            _configuration.Save();
        }
        return started;
    }
}
