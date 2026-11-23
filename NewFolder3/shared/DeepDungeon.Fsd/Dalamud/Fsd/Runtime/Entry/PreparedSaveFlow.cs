using System.Numerics;
using DeepDungeon.Fsd.Core;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Scenarios;
using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal sealed class PreparedSaveFlow
{
    private static readonly TimeSpan RetryInterval = TimeSpan.FromMilliseconds(350);
    private readonly PreparedSaveBinding _session;
    private readonly DungeonData _dungeon;
    private RunContext? _context;
    private NavigationHelper? _navigation;
    private DateTime _nextActionAt;
    private readonly PreparedEntryRetryState _retry = new();
    private string _stage = string.Empty;
    private DateTime _lastStatusLogUtc;
    private readonly bool _verifyOnly;
    private DateTime _started;
    public int SourceFloor => _session.Pinned?.StartFloor ?? 0;
    public int SourceSlot => _session.Pinned?.Index ?? -1;
    private bool _clickedOccupiedSlot;
    private bool _waitingForNavigation;

    public PreparedSaveFlow(PreparedSaveBinding session, bool verifyOnly = false, DungeonData? dungeon = null)
    {
        _session = session;
        _dungeon = dungeon ?? DungeonCatalog.PilgrimsTraverse;
        _verifyOnly = verifyOnly;
    }

    public void Prepare(RunContext context)
    {
        _context = context;
        _navigation = new NavigationHelper(context.Navigator);
        _nextActionAt = DateTime.MinValue;
        _started = DateTime.UtcNow;
        _retry.Reset();
        _stage = string.Empty;
        _lastStatusLogUtc = DateTime.MinValue;
        _clickedOccupiedSlot = false;
        _waitingForNavigation = false;
        SetStatus("prepare", "PT：讀取綁定來源存檔。");
    }

    public unsafe bool Update(IFramework framework)
    {
        var context = _context;
        if (context == null)
            return false;

        if (context.Duty.IsInDuty)
            return true;
        if (context.StatusIsError)
            return true;
        if (context.Duty.IsTransitioning || Service.LocalPlayer == null ||
            Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
        {
            SetStatus("transition", "PT：等待過場完成後接續存檔。");
            return false;
        }
        if (DateTime.UtcNow - _started > TimeSpan.FromMinutes(2))
            return Fail("來源存檔流程逾時；已停止。" );
        if (DateTime.UtcNow < _nextActionAt)
            return false;

        if (DeepDungeonUi.TryGetSelectYesNo(out var yesno))
        {
            DeepDungeonUi.TryGetConfirmationPromptText(yesno, out string prompt, out string promptError);
            RecordDiagnostic("prepared-entry-unexpected-prompt", new { prompt, promptError });
            return Fail("來源存檔流程遇到非本次操作的確認視窗；已停止。");
        }
        if (DeepDungeonUi.TryGetSelectString(out _))
        {
            DeepDungeonUi.TryReadSelectStringOptions(out var options);
            RecordDiagnostic("prepared-entry-unexpected-selection", new { options });
            return Fail("來源存檔流程進入了新建／選擇起點視窗；已停止。");
        }

        if (DeepDungeonUi.TryGetAddon("ContentsFinderConfirm", out _))
        {
            if (_verifyOnly || !_clickedOccupiedSlot) return Fail("出現不屬於本次存檔入場的任務確認視窗；停止。");
            if (_retry.CommenceDispatched)
            {
                SetStatus("await-duty", "PT：已確認來源存檔入場，等待副本載入。");
                Delay();
                return false;
            }
            if (DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
            {
                SetStatus("close-save-panel", "PT：關閉存檔列表後確認入場。");
                DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
                Delay();
                return false;
            }

            if (!DeepDungeonUi.ClickCommenceButton())
                return Fail("無法確認來源存檔的任務入場；已停止。");
            _retry.RecordCommence();
            SetStatus("await-duty", "PT：確認來源存檔入場，等待副本載入。");
            Delay();
            return false;
        }

        if (DeepDungeonUi.IsAddonOpen("DeepDungeonSaveData"))
        {
            if (_clickedOccupiedSlot)
            {
                SetStatus("await-application", "PT：等待來源存檔入場。");
                Delay();
                return false;
            }

            if (!DeepDungeonUi.TryReadSaveSlots(out var slots, out string readError))
                return WaitForCompleteList(readError);
            SetStatus("validate-save", "PT：校驗綁定存檔與預期起點。");
            if (!_session.Observe(slots, out var source, out string sourceError))
                return Fail(sourceError);
            SetStatus("validate-save", _session.Description);
            context.PreparedSaveDescription = _session.Description;
            if (_verifyOnly)
            {
                DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
                DeepDungeonUi.TryCloseAddon("DeepDungeonMenu");
                return true;
            }
            // Re-read the complete list immediately before the only selection callback.
            if (!DeepDungeonUi.TryReadSaveSlots(out slots, out readError)) return WaitForCompleteList(readError);
            if (!_session.Observe(slots, out source, out sourceError)) return Fail(sourceError);
            if (!context.SaveSlots.TrySelectSlot(source.Index))
                return Fail("無法選取唯一來源存檔。");

            _clickedOccupiedSlot = true;
            SetStatus("save-selected", $"PT：已選取綁定的存檔 {source.Index + 1}，起點 {source.StartFloor} 層。");
            Delay();
            return false;
        }

        // Selecting the occupied slot submits the duty application. The save panel may
        // disappear before DutyState observes the new territory; do not fall through
        // to Talk/NPC interaction and resubmit callbacks during that handoff.
        if (_clickedOccupiedSlot)
        {
            SetStatus("await-application", "PT：等待來源存檔入場。");
            Delay();
            return false;
        }

        if (DeepDungeonUi.IsAddonOpen("DeepDungeonMenu"))
        {
            var menuAction = _retry.DecideMenu(DateTime.UtcNow);
            if (menuAction == PreparedEntryMenuAction.Stop)
                return Fail("開啟存檔列表已重試三次仍無回應；已停止並保留存檔。");
            if (menuAction == PreparedEntryMenuAction.Request)
            {
                if (!DeepDungeonUi.EnterDeepDungeonViaAgent())
                    return Fail("無法開啟存檔列表；已停止。");
                _retry.RecordMenuRequest(DateTime.UtcNow);
                SetStatus("open-save-list", $"PT：開啟來源存檔列表（{_retry.MenuRequests}/3）。");
            }
            Delay();
            return false;
        }

        if (DeepDungeonUi.TryGetTalk(out var talk))
        {
            SetStatus("advance-talk", "PT：推進入口 NPC 對話。");
            if (!DeepDungeonUi.Fire(talk, 0))
                return Fail("無法推進入口 NPC 對話；已停止。");
            Delay();
            return false;
        }

        var npc = NpcInteractionGuard.FindByBaseId(_dungeon.NpcDataId);
        var player = Service.LocalPlayer;
        if (npc == null || player == null)
        {
            SetStatus("wait-npc", "PT：等待入口 NPC。");
            Delay();
            return false;
        }

        float distance = Vector3.Distance(player.Position, npc.Position);
        if (distance > NpcInteractionGuard.MaxInteractDistance)
        {
            if (!moveHelper.VNav.NavmeshReady())
            {
                SetStatus("wait-navmesh", "PT：等待入口地圖導航載入。");
                if (!_waitingForNavigation)
                    Service.Log.Info("[PreparedSaveFlow] Waiting for overworld navmesh before NPC navigation");
                _waitingForNavigation = true;
                Delay();
                return false;
            }
            if (_waitingForNavigation)
            {
                Service.Log.Info("[PreparedSaveFlow] Overworld navmesh ready; resuming NPC navigation");
                _waitingForNavigation = false;
            }
            var state = _navigation?.Navigate(
                npc.Position,
                player.Position,
                NpcInteractionGuard.MaxInteractDistance - 0.4f) ?? NavigationState.Failed;
            if (state is NavigationState.Failed or NavigationState.StuckGiveUp)
                return Fail("前往入口 NPC 的導航失敗；已停止。");
            SetStatus("navigate-npc", $"PT：前往入口 NPC（{distance:F1}m）。");
            Delay();
            return false;
        }

        _navigation?.Cancel();
        if (!_retry.CanInteractNpc(DateTime.UtcNow))
        {
            SetStatus("await-npc-response", "PT：等待入口 NPC 回應。");
            Delay();
            return false;
        }
        if (!NpcInteractionGuard.TryInteract(
                _dungeon.NpcDataId,
                _dungeon.Name,
                out string status))
        {
            SetStatus("interact-npc", status);
            Delay();
            return false;
        }

        _retry.RecordNpcInteraction(DateTime.UtcNow);
        SetStatus("interact-npc", status);
        Delay();
        return false;
    }

    public void Reset()
    {
        try { _navigation?.Cancel(); } catch { }
        _navigation = null;
        _context = null;
    }

    private bool Fail(string reason)
    {
        if (_context != null)
        {
            _context.StatusLine = $"PT續檔 [{_stage}]：{reason}";
            _context.StatusIsError = true;
        }
        RecordDiagnostic("prepared-entry-error", new { stage = _stage, reason });
        try { _navigation?.Cancel(); } catch { }
        DeepDungeonUi.TryCloseAddon("SelectYesno");
        return true;
    }

    private bool WaitForCompleteList(string reason)
    {
        SetStatus("wait-save-list", "等待完整存檔列表：" + reason);
        Delay();
        return false;
    }

    private void Delay()
    {
        _nextActionAt = DateTime.UtcNow.Add(RetryInterval);
    }

    private void SetStatus(string stage, string status)
    {
        if (_context != null) _context.StatusLine = status;
        if (_stage == stage && DateTime.UtcNow - _lastStatusLogUtc < TimeSpan.FromSeconds(5)) return;
        _stage = stage;
        _lastStatusLogUtc = DateTime.UtcNow;
        RecordDiagnostic("prepared-entry-status", new { stage, status });
    }

    private void RecordDiagnostic(string eventType, object details)
    {
        var data = new
        {
            details, expectedFloor = _session.ExpectedFloor, requiredSlot = _session.RequiredSlot,
            sourceFloor = SourceFloor, sourceSlot = SourceSlot, verifyOnly = _verifyOnly,
            selected = _clickedOccupiedSlot, menuRequests = _retry.MenuRequests,
            commenceDispatched = _retry.CommenceDispatched,
            elapsedSeconds = (DateTime.UtcNow - _started).TotalSeconds
        };
        try { _context?.RecordDiagnostic?.Invoke(eventType, data); } catch { }
        try { Service.Log.Info($"[PreparedSaveFlow] {eventType}: {System.Text.Json.JsonSerializer.Serialize(data)}"); } catch { }
    }
}
