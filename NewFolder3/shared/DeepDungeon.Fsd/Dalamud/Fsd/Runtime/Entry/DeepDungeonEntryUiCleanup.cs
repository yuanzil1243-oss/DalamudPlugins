using DeepDungeon.Fsd.Dalamud.GameState;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

internal enum EntryUiCleanupResult { Ready, Waiting, Failed }

/// <summary>Close only the entry parents, retrying after entry without touching pending modals.</summary>
internal sealed class DeepDungeonEntryUiCleanup(
    Func<string, bool>? isOpen = null, Action<string>? close = null, Func<DateTime>? utcNow = null)
{
    private readonly Func<string, bool> _isOpen = isOpen ?? DeepDungeonUi.IsAddonOpen;
    private readonly Action<string> _close = close ?? (name => DeepDungeonUi.TryCloseAddon(name));
    private readonly Func<DateTime> _utcNow = utcNow ?? (() => DateTime.UtcNow);
    private DateTime? _started;
    private DateTime _nextClose;

    public EntryUiCleanupResult Update(out string status, out bool closeRequested)
    {
        closeRequested = false;
        bool save = _isOpen("DeepDungeonSaveData"), menu = _isOpen("DeepDungeonMenu");
        if (!save && !menu)
        {
            Reset(); status = string.Empty;
            return EntryUiCleanupResult.Ready;
        }
        var now = _utcNow();
        _started ??= now;
        if (now - _started.Value > TimeSpan.FromSeconds(3))
        {
            status = "入場視窗未能關閉，停止自動化；請手動關閉存檔列表／入口視窗。";
            return EntryUiCleanupResult.Failed;
        }
        if (_isOpen("SelectString") || _isOpen("SelectYesno") || _isOpen("ContentsFinderConfirm"))
        {
            status = "等待入場確認視窗關閉後清理存檔列表。";
            return EntryUiCleanupResult.Waiting;
        }
        if (now >= _nextClose)
        {
            if (save) _close("DeepDungeonSaveData");
            if (menu) _close("DeepDungeonMenu");
            _nextClose = now.AddMilliseconds(300);
            closeRequested = true;
        }
        status = "已進入副本，正在關閉存檔列表／入口視窗。";
        return EntryUiCleanupResult.Waiting;
    }

    public void Reset() { _started = null; _nextClose = default; }
}
