using System.IO;
using System.Text.Json;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private DateTime _nextReplayCheck;
    private string _replayStatus = "啟動任務後檢查 BMR 自動回放";
    private string? _lastReplayStatus;
    private Task<RunLogExportResult>? _runLogExportTask;

    private void UpdateReplaySupport()
    {
        if (_runLogExportTask?.IsCompleted == true)
        {
            try
            {
                var result = _runLogExportTask.GetAwaiter().GetResult();
                _runLogExportNotice = $"日誌置於 {result.ArchivePath.Replace('\\', '/')}；BMR 回放 {result.ReplayCount} 份。{result.ReplayStatus}";
                _runLogExportFailed = false;
            }
            catch (Exception ex)
            { _runLogExportNotice = "導出失敗：" + ex.Message; _runLogExportFailed = true; Service.Log.Error($"[FSD] Export failed: {ex}"); }
            _runLogExportTask = null;
        }
        if (!_configuration.AutoRecordBmrReplays || _ddHost?.FsdActive != true || DateTime.UtcNow < _nextReplayCheck) return;
        _nextReplayCheck = DateTime.UtcNow.AddSeconds(10);
        try
        {
            // Use BMR's supported configuration API. No reflection, config-file writes,
            // chat fallback, replay-off commands or changes to anonymization/retention.
            var config = Service.PluginInterface.GetIpcSubscriber<List<string>, bool, List<string>>("BossMod.Configuration");
            if (!config.HasFunction) _replayStatus = "BMR 設定接口不可用，未確認錄製；請檢查 BMR 是否載入。";
            else
            {
                var current = config.InvokeFunc(["ReplayManagementConfig", "AutoRecord"], false);
                if (!BmrReplayConfiguration.IsEnabled(current))
                    config.InvokeFunc(["ReplayManagementConfig", "AutoRecord", "true"], true);
                var verified = config.InvokeFunc(["ReplayManagementConfig", "AutoRecord"], false);
                _replayStatus = BmrReplayConfiguration.IsEnabled(verified)
                    ? "已確認 BMR 自動回放開啟；實際回放以檔案為準。"
                    : "無法確認 BMR 自動回放，請手動檢查 BMR 的回放設定。";
            }
        }
        catch (Exception ex) { _replayStatus = "BMR 回放檢查失敗：" + ex.Message; }
        if (_replayStatus != _lastReplayStatus)
        {
            _lastReplayStatus = _replayStatus;
            Service.Log.Info("[FSD Replay] " + _replayStatus);
            _ddHost?.Context?.RecordDiagnostic?.Invoke("bmr-auto-record-status", new { status = _replayStatus });
        }
    }

    private void StartRunLogExport()
    {
        if (_runLogExportTask != null) return;
        // Resolve host services on the framework thread; worker reads files only.
        string logs = DeepDungeonRunRecorder.GetLogDirectory();
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string replays = ResolveBmrReplayDirectory();
        string identity = _hostIdentity;
        var now = DateTime.UtcNow;
        _runLogExportNotice = "正在打包運行日誌與對應時段的 BMR 回放…";
        _runLogExportFailed = false;
        _runLogExportTask = Task.Run(() => DeepDungeonRunLogExporter.ExportRecent(logs, desktop, identity, now, replays));
        _runLogExportTask.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static string ResolveBmrReplayDirectory()
    {
        string parent = Path.GetDirectoryName(Service.PluginInterface.GetPluginConfigDirectory())!;
        string fallback = Path.Combine(parent, "BossModReborn", "replays");
        string configPath = Path.Combine(parent, "BossModReborn.json");
        try
        {
            using var input = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var config = JsonDocument.Parse(input);
            if (config.RootElement.TryGetProperty("Payload", out var payload) &&
                payload.TryGetProperty("BossMod.ReplayManagementConfig", out var replay) &&
                replay.TryGetProperty("ReplayFolder", out var folder) && folder.GetString() is { Length: > 0 } custom && Path.IsPathFullyQualified(custom))
                return custom;
        }
        catch (Exception ex) { Service.Log.Debug("[FSD Replay] Using default replay directory: " + ex.Message); }
        return fallback;
    }
}
