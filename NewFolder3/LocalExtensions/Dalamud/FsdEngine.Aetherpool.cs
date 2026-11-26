using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using global::Dalamud.Bindings.ImGui;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private FarmingScenario CreateLocalFarmingScenario(FarmingSession session) =>
        new FarmingScenario(session).WithGripTracker(_aetherpoolTracker);
    private void DrawGripExchangeSettings(bool running, FarmingMode mode)
    {
        if (mode != FarmingMode.Hoard) return;
        var settings = _configuration.Farming;
        var stored = settings.GripExchange;
        bool relic = settings.RelicMaterial;
        bool enabled = relic ? !settings.RelicStrengthOnly : stored?.EnabledForHoard == true;
        ImGui.BeginDisabled(running);
        if (relic)
        {
            bool only = settings.RelicStrengthOnly;
            if (ImGui.Checkbox("不兌換聚魔柄（只刷強化）##relicStrengthOnly", ref only))
            { settings.RelicStrengthOnly = only; enabled = !only; _configuration.Save(); }
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("只刷強化，不前往 NPC 兌換；仍按所選層段和次數循環。");
        }
        if (!relic && ImGui.Checkbox("出本後自動製作聚魔柄##gripAuto", ref enabled))
        { settings.GripExchange ??= new(); settings.GripExchange.EnabledForHoard = enabled; _configuration.Save(); }
        if (!relic && _fsfScenarioIndex != 0)
            ImGui.TextWrapped("自動製作聚魔柄目前只接入朝聖 21–30；31–40 仍按原挖寶流程執行。");
        if (enabled)
        {
            var options = stored?.ToOptions() ?? new GripExchangeOptions();
            int threshold = options.Threshold, count = options.BatchCount;
            if (ImGui.InputInt("武器與防具強化門檻##gripThreshold", ref threshold))
            { settings.GripExchange ??= new(); settings.GripExchange.Threshold = AetherpoolExchangePolicy.NormalizeThreshold(threshold); _configuration.Save(); }
            if (ImGui.InputInt("每批聚魔柄數量##gripBatch", ref count))
            { settings.GripExchange ??= new(); settings.GripExchange.BatchCount = AetherpoolExchangePolicy.NormalizeCount(count); _configuration.Save(); }
            ImGui.TextWrapped("完成所選層段後最多兌換一批；保留再入場所需強化。強化未知時先正常跑一輪取得讀數。兌換結果不明時停止，不重複提交。");
        }
        else if (relic) ImGui.TextWrapped("只刷強化，跳過啟動前和出本後的聚魔柄兌換。");
        if (relic && settings.DungeonId == 2)
            ImGui.TextWrapped("魔石：普通層溢出時使用；30層 Boss 戰依冷卻逐顆用完。");
        ImGui.EndDisabled();
        if (_aetherpoolTracker.TryGet(settings.DungeonId, out var state))
            ImGui.TextWrapped($"聚魔強化：武器 +{state.Weapon?.ToString() ?? "?"}／防具 +{state.Armor?.ToString() ?? "?"}（{state.Source}）");
        else ImGui.TextWrapped("聚魔強化：尚無本角色的有效讀數。");
        if (enabled) ImGui.TextWrapped("本外自動前往兌換 NPC 沿用 vnavmesh；無導航時需靠近該 NPC。入場解鎖仍由遊戲原生判定。");
    }
    private FsdControlResult StartRelicFarming(FsdStartRequest request, uint dungeon)
    {
        if (request.StopAfterFloor.HasValue || request.DiagnosticStartFloor.HasValue || request.ResumeSlot.HasValue ||
            request.HoldOnFailure || request.HoldAfterClear || request.Scenario == FsdStartScenario.ControlledSurvey)
            return new(false, "聚魔柄循環不能混用診斷、續用槽位或保留現場參數。");
        if (dungeon is 1 or 2 && Service.LocalPlayer?.ClassJob.RowId is not (23 or 31 or 38))
            return new(false, "保留原作者職業支援範圍：死宮／天宮須使用機工士、吟遊詩人或舞者。");
        var p = _configuration.Farming.GetRelicMaterialPreferences();
        if (!RelicFarmingPolicy.TryCreate(dungeon, SaveUse.Create,
                _configuration.Farming.GetRelicStartFloor(), p.Cycles, p.Infinite,
                new(p.BandedEnabled == true, p.OpenGold, p.OpenSilver, p.OpenBronze), out var plan, out var error))
            return new(false, error);
        plan = TaskContinuationPolicy.WithRetention(ConfigureGripExchange(plan!), p.KeepSave);
        var session = new FarmingSession(plan);
        string? key = DetailedMapUsagePolicy.ScenarioKey(plan);
        bool started = TryStartOutsideDutyFsd(() => CreateLocalFarmingScenario(session), plan.TargetCycles, plan.Infinite, key, out error);
        if (started) { _farmingError = null; _configuration.Save(); }
        return new(started, error);
    }
    private FarmingPlan ConfigureGripExchange(FarmingPlan plan)
    {
        return _configuration.Farming.ConfigureGripExchange(plan);
    }

    private void DrawRelicPresetSelection()
    {
        var settings = _configuration.Farming;
        if (settings.DungeonId == 1)
        {
            string[] labels = ["51–60 層", "1–50 層"];
            int preset = settings.RelicPotd1To50 ? 1 : 0;
            ImGui.SetNextItemWidth(180);
            if (ImGui.Combo("強化層段##relicPreset", ref preset, labels, labels.Length))
            { settings.RelicPotd1To50 = preset == 1; _configuration.Save(); _farmingError = null; }
        }
        int start = settings.GetRelicStartFloor();
        int end = settings.DungeonId == 1 && start == 1 ? 50 : start + 9;
        ImGui.TextWrapped($"{start}–{end} 層為一輪，到達終點才計次。中途出本接續原檔；失敗後能續打則續打，不能續打才清理本任務檔重開。");
    }
}
