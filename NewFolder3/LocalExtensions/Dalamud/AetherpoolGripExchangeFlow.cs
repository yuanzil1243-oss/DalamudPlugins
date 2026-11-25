// Exchange flow adapted to the .2205 host from the supplied OseWydExchangeFlow.
// Navigation, save ownership and dungeon combat remain owned by the existing host.
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Runtime;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using global::Dalamud.Plugin.Services;
using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal sealed class AetherpoolGripExchangeFlow : IDisposable
{
    private readonly GripExchangeDungeon _dungeon;
    private readonly AetherpoolTracker _tracker;
    private readonly int _target;
    private readonly GripCraftReceipt _receipt = new();
    private RunContext? _ctx;
    private NavigationHelper? _navigation;
    private ulong _character;
    private uint _job;
    private bool _interacted, _done;
    private DateTime _started, _lastProgress, _nextAction, _noUiSince;
    private Vector3 _lastPosition;
    private string _uiKey = "";
    private DateTime _uiSince;
    internal int CraftedCount { get; private set; }
    internal AetherpoolGripExchangeFlow(GripExchangeDungeon dungeon, int count, AetherpoolTracker tracker)
    { _dungeon = dungeon; _target = count; _tracker = tracker; }
    internal void Prepare(RunContext context)
    {
        _ctx = context; _navigation = new(context.Navigator);
        _character = Service.PlayerState.ContentId; _job = Service.LocalPlayer?.ClassJob.RowId ?? 0;
        _started = _lastProgress = DateTime.UtcNow;
        _lastPosition = Service.LocalPlayer?.Position ?? default;
        context.RecordDiagnostic?.Invoke("grip-exchange-start", new { dungeon = _dungeon.DungeonId, count = _target });
    }
    internal unsafe bool Update(IFramework framework)
    {
        if (_done || _ctx == null) return true;
        var ctx = _ctx;
        DateTime now = DateTime.UtcNow;
        if (_character == 0 || _job == 0 || Service.PlayerState.ContentId != _character ||
            Service.LocalPlayer?.ClassJob.RowId != _job) return Fail("角色或職業已變更。");
        if (ctx.Duty.IsInDuty || ctx.Duty.IsTransitioning || DutyState.IsClientTransitioning) return false;
        if (now - _started > TimeSpan.FromSeconds(90) || now - _lastProgress > TimeSpan.FromSeconds(18))
            return Fail("對話或移動超時。");
        if (!DeepDungeonLootTracker.TryGetItemCount(_dungeon.ItemId, out int inventory, out var inventoryError))
            return Fail("無法讀取背包：" + inventoryError);
        if (!_receipt.Observe(inventory, now, out bool crafted, out string receiptError)) return Fail(receiptError);
        if (crafted)
        {
            if (!_tracker.RecordConfirmedCraft(_dungeon.DungeonId)) return Fail("製作已成功，但強化快取無法安全扣減。");
            CraftedCount++; _lastProgress = now;
            ctx.RecordDiagnostic?.Invoke("grip-crafted", new { dungeon = _dungeon.DungeonId, CraftedCount, inventory });
            _nextAction = now.AddMilliseconds(350);
            return false;
        }
        if (now < _nextAction) return false;
        bool yes = DeepDungeonUi.TryGetSelectYesNo(out var yesno);
        bool select = DeepDungeonUi.TryGetSelectString(out var menu);
        bool talkVisible = DeepDungeonUi.TryGetTalk(out var talk);
        string ui = yes ? "yes" : talkVisible ? "talk" : select ? "menu" : "none";
        if (_uiKey != ui) { _uiKey = ui; _uiSince = now; }
        bool stable = now - _uiSince >= TimeSpan.FromMilliseconds(220);
        if (ui != "none")
        {
            _navigation?.Cancel(); _noUiSince = default;
            if (!_interacted) return Fail("發現不屬於本次 NPC 互動的對話，請先關閉。");
            if (Service.TargetManager.Target is { } target && target.BaseId != _dungeon.NpcId)
                return Fail("對話目標已變更。");
            if (!stable) return false;
        }
        if (yes)
        {
            if (!_receipt.Pending) return Fail("出現非預期確認視窗。");
            if (_receipt.ConfirmationSent) return false; // Never resubmit an unacknowledged irreversible callback.
            if (!DeepDungeonUi.TryGetConfirmationPromptText(yesno, out string prompt, out _) ||
                !AetherpoolExchangePolicy.IsCraftConfirmation(prompt)) return Fail("無法核驗製作確認文字。");
            if (!_receipt.ConfirmOnce() || !DeepDungeonUi.Fire(yesno, 0)) return Fail("無法送出製作確認。");
            _lastProgress = now; _nextAction = now.AddMilliseconds(350); return false;
        }
        if (talkVisible)
        {
            if (!DeepDungeonUi.Fire(talk, 0)) return Fail("無法推進 NPC 對話。");
            _nextAction = now.AddMilliseconds(350); _lastProgress = now; return false;
        }
        if (_receipt.Pending) return false;
        if (select)
        {
            if (!TryReadMenu(out var options)) return Fail("無法讀取 NPC 選單。");
            if (CraftedCount >= _target)
            {
                int cancel = Array.FindIndex(options, AetherpoolExchangePolicy.IsCancelOption);
                if (cancel < 0 || !DeepDungeonUi.Fire(menu, cancel)) return Fail("未找到可核驗的取消選項。");
                _nextAction = now.AddMilliseconds(350); _lastProgress = now; return false;
            }
            int[] matches = options.Select((text, index) => (text, index))
                .Where(x => AetherpoolExchangePolicy.IsCraftOption(x.text)).Select(x => x.index).ToArray();
            if (matches.Length != 1) return Fail("製作聚魔柄選項缺失或不唯一。");
            // Recheck current, character-bound enhancement immediately before every craft.
            if (!_tracker.TryGet(_dungeon.DungeonId, out var state) || state.Weapon < _dungeon.Reserve + 10 ||
                state.Armor < _dungeon.Reserve + 10 || state.Weapon == null || state.Armor == null)
                return Fail("強化值不足、已過期或未知；保留再次入場所需強化。");
            _receipt.Begin(inventory, now);
            if (!DeepDungeonUi.Fire(menu, matches[0])) return Fail("無法選擇製作聚魔柄。");
            _nextAction = now.AddMilliseconds(350); _lastProgress = now; return false;
        }
        if (CraftedCount >= _target)
        {
            if (_noUiSince == default) _noUiSince = now;
            if (now - _noUiSince < TimeSpan.FromMilliseconds(400) ||
                Service.Condition[ConditionFlag.OccupiedInQuestEvent] || Service.Condition[ConditionFlag.OccupiedInEvent]) return false;
            _done = true; _navigation?.Cancel();
            ctx.StatusLine = $"已製作 {CraftedCount} 個{_dungeon.GripName}。"; return true;
        }
        var npc = NpcInteractionGuard.FindByBaseId(_dungeon.NpcId);
        var player = Service.LocalPlayer;
        if (npc == null || player == null) { ctx.StatusLine = $"等待{_dungeon.NpcName}載入。"; return false; }
        float distance = Vector3.Distance(player.Position, npc.Position);
        ctx.StatusLine = $"{_dungeon.NpcName}：製作{_dungeon.GripName} {CraftedCount}/{_target}，距離 {distance:F1}m";
        if (distance > NpcInteractionGuard.MaxInteractDistance)
        {
            if (!moveHelper.VNav.NavmeshReady()) return Fail("本外前往 NPC 需要可用的 vnavmesh；請移到 NPC 附近後再啟動。");
            var navigation = _navigation!.Navigate(npc.Position, player.Position, NpcInteractionGuard.InteractionRange);
            if (navigation is NavigationState.Failed or NavigationState.StuckGiveUp) return Fail("前往兌換 NPC 的導航失敗。");
            if (Vector3.DistanceSquared(_lastPosition, player.Position) > .25f)
            { _lastPosition = player.Position; _lastProgress = now; }
            return false;
        }
        _navigation?.Cancel();
        if (NpcInteractionGuard.TryInteract(_dungeon.NpcId, _dungeon.NpcName, out var status))
        { _interacted = true; _lastProgress = now; }
        ctx.StatusLine = status; _nextAction = now.AddMilliseconds(1200); return false;
    }
    private static unsafe bool TryReadMenu(out string[] options)
    {
        options = [];
        if (!DeepDungeonUi.TryGetSelectString(out var addon)) return false;
        int count = ((AddonSelectString*)addon)->PopupMenu.PopupMenu.EntryCount;
        if (count is < 1 or > 32 || addon->AtkValues == null || addon->AtkValuesCount < count + 7) return false;
        options = new string[count];
        for (int i = 0; i < count; i++)
        {
            ref var value = ref addon->AtkValues[i + 7];
            options[i] = value.Type is (AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString) && value.String.HasValue
                ? Encoding.UTF8.GetString(MemoryMarshal.CreateReadOnlySpanFromNullTerminated(value.String)) : "";
        }
        return true;
    }
    private bool Fail(string message)
    {
        _done = true; _navigation?.Cancel();
        if (_ctx != null)
        {
            _ctx.StatusIsError = true; _ctx.StatusLine = "聚魔柄兌換停止：" + message;
            _ctx.RecordDiagnostic?.Invoke("grip-exchange-stopped", new { message, CraftedCount });
        }
        return true;
    }
    public void Dispose() { _done = true; _navigation?.Cancel(); _navigation = null; _ctx = null; }
}
