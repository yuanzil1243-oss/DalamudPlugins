using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Plugin.Services;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

/// <summary>Fixed-party practice only. No save-slot or delete-save operation exists in this flow.</summary>
internal sealed class FinalBossEntryFlow
{
    private RunContext? _context;
    private NavigationHelper? _navigation;
    private DateTime _started, _nextAction, _lastAdvance;
    private DateTime _menuObserved;
    private bool _selectedChallenge, _selectedParty, _partyConfirmationSent;
    private string _dutyName = string.Empty;
    public void Prepare(RunContext context)
    {
        _context = context;
        _navigation = new NavigationHelper(context.Navigator);
        _started = DateTime.UtcNow;
        _dutyName = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>()?
            .GetRow(FinalBossPracticePolicy.ContentFinderId).Name.ExtractText() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(_dutyName)) Fail("无法读取尾王挑战名称，停止入场。");
    }

    public unsafe bool Update(IFramework framework)
    {
        var ctx = _context!;
        if (ctx.StatusIsError) return false;
        var now = DateTime.UtcNow;
        if (FinalBossPracticePolicy.IsPractice(Service.ClientState.TerritoryType))
        { _navigation?.Cancel(); return !ctx.Duty.IsTransitioning; }
        if (Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51]) return false;
        if (Service.Condition[ConditionFlag.BoundByDuty]) { Fail("尾王模式检测到其他副本，停止入场。"); return false; }
        if (now - _started > TimeSpan.FromSeconds(90)) { Fail("尾王练习入场超时；请检查解锁状态、固定队伍和确认窗口。"); return false; }
        if (now < _nextAction) return false;
        _nextAction = now.AddMilliseconds(400);
        if (DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
        { DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData"); Fail("尾王模式意外出现存档面板，已停止；不会操作存档。"); return false; }
        // The fixed-party selection opens SelectYesno before the duty confirmation.
        // Handle this modal before its still-visible parent menus, and only for our own selection.
        if (DeepDungeonUi.TryGetSelectYesNo(out var yesno))
        {
            if (!DeepDungeonUi.TryGetConfirmationPromptText(yesno, out var prompt, out var readError) ||
                string.IsNullOrWhiteSpace(prompt))
            {
                ctx.StatusLine = "尾王练习：等待固定队伍确认文字加载。";
                if (now - _lastAdvance > TimeSpan.FromSeconds(12))
                    Fail($"尾王固定队伍确认文字未能读取，已停止：{readError}");
                return false;
            }
            if (!FinalBossPracticePolicy.CanConfirmFixedPartyEntry(_selectedChallenge, _selectedParty, prompt))
            {
                ctx.RecordDiagnostic?.Invoke("final-boss-entry-confirmation-rejected", new {
                    prompt, _selectedChallenge, _selectedParty, _partyConfirmationSent });
                Fail($"尾王练习确认提示与本次固定队伍选择不符，已停止：{prompt}");
                return false;
            }
            if (_partyConfirmationSent)
            {
                ctx.StatusLine = "尾王练习：固定队伍确认已发送，等待窗口关闭。";
                if (now - _lastAdvance > TimeSpan.FromSeconds(12))
                    Fail("尾王固定队伍确认后窗口未关闭，已停止供检查。");
                return false;
            }
            if (DeepDungeonUi.Fire(yesno, 0))
            {
                _partyConfirmationSent = true;
                _lastAdvance = now;
                ctx.StatusLine = "尾王练习：已确认固定队伍进入，等待入场。";
                ctx.RecordDiagnostic?.Invoke("final-boss-fixed-party-confirmed", new { prompt });
            }
            else if (now - _lastAdvance > TimeSpan.FromSeconds(12))
                Fail("尾王固定队伍确认未能发送，已停止供检查。");
            return false;
        }
        if (DeepDungeonUi.IsAddonOpen("ContentsFinderConfirm"))
        {
            if (!_selectedParty || !DeepDungeonUi.ConfirmWindowMatchesFinalBoss(_dutyName))
            { ctx.StatusLine = "等待核对尾王练习的入场确认，不确认其他副本。"; return false; }
            DeepDungeonUi.ClickCommenceButton();
            _lastAdvance = now;
            ctx.StatusLine = "尾王练习：已确认入场，等待加载。";
            return false;
        }
        if (DeepDungeonUi.TryGetSelectString(out var select))
        {
            if (!_selectedChallenge || !DeepDungeonUi.TryReadSelectStringOptions(out var options)) return false;
            if (!FinalBossPracticePolicy.TrySelectFixedParty(options, out int index))
            { Fail("未能唯一识别固定队伍入口，停止；不会改选随机组队或其他选项。"); return false; }
            if (_selectedParty) return false;
            if (DeepDungeonUi.Fire(select, index))
            {
                _selectedParty = true;
                _lastAdvance = now;
                ctx.RecordDiagnostic?.Invoke("final-boss-fixed-party-selected", new { index, options });
            }
            return false;
        }
        if (DeepDungeonUi.IsAddonOpen("DeepDungeonMenu"))
        {
            if (_selectedChallenge) return false;
            if (_menuObserved == default) _menuObserved = now;
            if (!DeepDungeonUi.TrySelectFinalBossChallenge(_dutyName, out var options, out var error))
            {
                if (now - _menuObserved < TimeSpan.FromSeconds(2))
                { ctx.StatusLine = "等待尾王入口菜单加载完成。"; return false; }
                Fail(error); ctx.RecordDiagnostic?.Invoke("final-boss-entry-menu-rejected", new { options, error }); return false;
            }
            _selectedChallenge = true;
            _lastAdvance = now;
            ctx.RecordDiagnostic?.Invoke("final-boss-entry-menu-selected", new { dutyName = _dutyName, options });
            return false;
        }
        if (DeepDungeonUi.TryGetTalk(out var talk))
        { DeepDungeonUi.Fire(talk, 0); _lastAdvance = now; return false; }
        if (_selectedChallenge)
        {
            ctx.StatusLine = "等待尾王练习入口后续窗口。";
            if (now - _lastAdvance > TimeSpan.FromSeconds(12)) Fail("尾王入口未继续推进，已停止供检查。");
            return false;
        }
        var npc = NpcInteractionGuard.FindByBaseId(DungeonCatalog.PilgrimsTraverse.NpcDataId);
        var player = Service.LocalPlayer;
        if (npc == null || player == null) { ctx.StatusLine = "尾王模式：等待朝圣交错路入口NPC。"; return false; }
        if (Vector3.Distance(player.Position, npc.Position) > NpcInteractionGuard.MaxInteractDistance)
        {
            _navigation!.Navigate(npc.Position, player.Position, NpcInteractionGuard.MaxInteractDistance - 0.4f);
            ctx.StatusLine = "尾王模式：前往入口NPC。";
            return false;
        }
        _navigation?.Cancel();
        NpcInteractionGuard.TryInteract(DungeonCatalog.PilgrimsTraverse.NpcDataId, "尾王练习", out var status);
        ctx.StatusLine = status;
        _nextAction = now.AddSeconds(2);
        return false;
    }

    private void Fail(string error) { if (_context != null) { _context.StatusLine = error; _context.StatusIsError = true; } }
    public void Reset() => _navigation?.Cancel();
}
