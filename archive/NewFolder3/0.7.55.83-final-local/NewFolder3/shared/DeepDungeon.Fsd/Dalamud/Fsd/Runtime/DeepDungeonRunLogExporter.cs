using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

internal sealed record RunLogExportResult(string ArchivePath, int ReplayCount, string ReplayStatus);

internal static class DeepDungeonRunLogExporter
{
    private static readonly TimeSpan RecentWindow = TimeSpan.FromDays(3);
    private sealed record Window(DateTime Start, DateTime End, bool Closed);
    private sealed record Evidence(string Entry, long Bytes, string Sha256, bool ChangedDuringCopy,
        bool SnapshotOnly, DateTime SourceModifiedUtc, string Completeness);

    internal static RunLogExportResult ExportRecent(string logDirectory, string destinationDirectory,
        string hostIdentity, DateTime nowUtc, string? replayDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostIdentity);
        if (!Directory.Exists(logDirectory)) throw new InvalidOperationException("運行日誌目錄不存在。");
        if (!Directory.Exists(destinationDirectory)) throw new InvalidOperationException("導出目的目錄不存在。");
        DateTime cutoff = nowUtc.ToUniversalTime() - RecentWindow;
        string[] logs = Directory.EnumerateFiles(logDirectory, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Where(p => File.GetLastWriteTimeUtc(p) >= cutoff).Order(StringComparer.Ordinal).ToArray();
        if (logs.Length == 0) throw new InvalidOperationException("最近三天沒有可導出的運行日誌。");
        string host = string.Concat(hostIdentity.Split('.', 2)[0].Select(c =>
            Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string archivePath = Path.Combine(destinationDirectory, $"{host}-運行日誌-{DateTime.Now:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.zip");
        var warnings = new List<string>();
        var evidence = new List<Evidence>();
        var windows = new List<Window>();
        int replayCount = 0;
        string replayStatus;
        try
        {
            using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (string source in logs)
                {
                    byte[] bytes = ReadLogSnapshot(source, out bool changed, out DateTime modified);
                    string entryName = "DeepDungeonRuns/" + Path.GetFileName(source);
                    using (var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open()) entry.Write(bytes);
                    var window = ReadWindow(bytes, nowUtc);
                    if (window != null) windows.Add(window);
                    else warnings.Add($"{entryName}: 無法解析時間範圍，不以檔案大小推斷完整性。");
                    evidence.Add(new(entryName, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), changed,
                        window?.Closed != true || changed, modified, window?.Closed == true ? "記錄含 run-recorder-closing；仍需核對樓層/通關證據" : "運行未確認結束，僅為匯出時快照"));
                }
                if (string.IsNullOrEmpty(replayDirectory) || !Directory.Exists(replayDirectory))
                    replayStatus = "BMR 回放目錄不存在；本包只有運行日誌。";
                else
                {
                    foreach (string source in Directory.EnumerateFiles(replayDirectory, "*.log", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
                    {
                        try
                        {
                            var info = new FileInfo(source);
                            DateTime start = ReplayStartUtc(info);
                            DateTime end = info.LastWriteTimeUtc > start ? info.LastWriteTimeUtc : start;
                            if (!windows.Any(w => start <= w.End.AddMinutes(2) && end >= w.Start.AddMinutes(-2))) continue;
                            evidence.Add(CopyReplaySnapshot(archive, info));
                            replayCount++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        { warnings.Add($"BMR/{Path.GetFileName(source)}: 未能讀取：{ex.Message}"); }
                    }
                    replayStatus = replayCount > 0
                        ? "回放按日誌時間範圍匹配；均為快照，完整性未經回放解析驗收。"
                        : "未找到可讀的對應時段回放；可能未啟用錄製、目錄不同或記錄尚未寫出。";
                }
                if (warnings.Count > 0) replayStatus += $" 另有 {warnings.Count} 項警告，詳見打包清單。";
                using var report = archive.CreateEntry("打包清單.json", CompressionLevel.Optimal).Open();
                JsonSerializer.Serialize(report, new { exportedAtUtc = nowUtc, replayDirectory, replayCount, replayStatus,
                    note = "不以非空、大小穩定或檔案存在證明回放完整；正在錄製時建議退本後重新匯出。未停止或重啟 BMR 錄製。",
                    windows, warnings, files = evidence }, new JsonSerializerOptions { WriteIndented = true });
            }
            return new(archivePath, replayCount, replayStatus);
        }
        catch
        {
            if (File.Exists(archivePath)) File.Delete(archivePath);
            throw;
        }
    }

    private static byte[] ReadLogSnapshot(string path, out bool changed, out DateTime modified)
    {
        var before = new FileInfo(path); long length = before.Length; modified = before.LastWriteTimeUtc;
        if (length > 256L * 1024 * 1024) throw new IOException("單份運行日誌超過 256 MB，請分開提供。");
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[checked((int)length)];
        input.ReadExactly(bytes);
        before.Refresh(); changed = !before.Exists || before.Length != length || before.LastWriteTimeUtc != modified;
        return bytes;
    }

    private static Window? ReadWindow(byte[] bytes, DateTime nowUtc)
    {
        DateTime? first = null, last = null; bool closed = false;
        using var reader = new StreamReader(new MemoryStream(bytes));
        while (reader.ReadLine() is { } line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("timestampUtc", out var stamp) && stamp.TryGetDateTime(out var time))
                {
                    time = time.ToUniversalTime();
                    if (first == null || time < first) first = time;
                    if (last == null || time > last) last = time;
                }
                if (doc.RootElement.TryGetProperty("eventType", out var type) && type.GetString() == "run-recorder-closing") closed = true;
            }
            catch (JsonException) { /* Partial final lines in a live snapshot are not completion. */ }
        }
        return first.HasValue && last.HasValue ? new(first.Value, closed ? last.Value : nowUtc, closed) : null;
    }

    internal static DateTime ReplayStartUtc(FileInfo file)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(file.Name), @"(\d{4}_\d{2}_\d{2}_\d{2}_\d{2}_\d{2})(?:$|\D)", RegexOptions.CultureInvariant);
        if (match.Success && DateTime.TryParseExact(match.Groups[1].Value, "yyyy_MM_dd_HH_mm_ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal, out var local)) return local.ToUniversalTime();
        return file.CreationTimeUtc;
    }

    private static Evidence CopyReplaySnapshot(ZipArchive archive, FileInfo file)
    {
        // Read exactly the initial length, so a growing recording cannot prolong export indefinitely.
        long length = file.Length; DateTime modified = file.LastWriteTimeUtc;
        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        string entryName = "BMR/replays/" + file.Name;
        using var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[1024 * 1024]; long copied = 0;
        while (copied < length)
        {
            int count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, length - copied));
            if (count == 0) break;
            entry.Write(buffer, 0, count); hash.AppendData(buffer, 0, count); copied += count;
        }
        file.Refresh(); bool changed = copied != length || !file.Exists || file.Length != length || file.LastWriteTimeUtc != modified;
        return new(entryName, copied, Convert.ToHexStringLower(hash.GetHashAndReset()), changed, true, modified,
            changed ? "錄製/變動中的快照，不能當成完整回放" : "回放快照；尚未核對退本與樓層覆蓋，不能據此宣稱完整");
    }
}
