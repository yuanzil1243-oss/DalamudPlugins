using System.Text.Json;

namespace DeepDungeon.Fsd.Core;

public enum FeatureTriggerKind { AntiDisplacement, ForcedMovement, GcdProtection }
public sealed record FeatureTriggerCountData(int SchemaVersion = 1, long AntiDisplacement = 0,
    long ForcedMovement = 0, long GcdProtection = 0);

/// <summary>Monotonic totals. Native callbacks only change memory; the host flushes on its update thread.</summary>
public sealed class FeatureTriggerCounters : IDisposable
{
    private readonly object sync = new(), saveSync = new();
    private readonly string? path;
    private FeatureTriggerCountData data = new();
    private long revision, savedRevision;
    private bool readOnly, invalidPrimary;
    public string? SaveError { get; private set; }

    public FeatureTriggerCounters(string? filePath = null)
    {
        path = filePath == null ? null : Path.GetFullPath(filePath);
        if (path == null) return;
        int existing = 0, valid = 0;
        foreach (string name in new[] { path, path + ".bak" })
        {
            if (!File.Exists(name)) continue;
            existing++;
            try
            {
                using var json = JsonDocument.Parse(File.ReadAllBytes(name));
                var root = json.RootElement;
                int schema = root.GetProperty(nameof(FeatureTriggerCountData.SchemaVersion)).GetInt32();
                if (schema != 1) { readOnly = true; throw new InvalidDataException("Unknown counter schema."); }
                long a = root.GetProperty(nameof(FeatureTriggerCountData.AntiDisplacement)).GetInt64();
                long f = root.GetProperty(nameof(FeatureTriggerCountData.ForcedMovement)).GetInt64();
                long g = root.GetProperty(nameof(FeatureTriggerCountData.GcdProtection)).GetInt64();
                if (a < 0 || f < 0 || g < 0) throw new InvalidDataException("Negative counter.");
                data = new(1, Math.Max(data.AntiDisplacement, a), Math.Max(data.ForcedMovement, f), Math.Max(data.GcdProtection, g));
                valid++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            {
                if (name == path) invalidPrimary = true;
                SaveError = "计数文件暂不可读，已保留原文件。";
            }
        }
        if (existing > 0 && valid == 0) readOnly = true;
        if (readOnly) SaveError = "计数文件损坏或版本较新，暂不覆盖原记录。";
        else if (valid > 0 && invalidPrimary) revision = 1; // Restore a verified backup on the next host update.
        else SaveError = null;
    }

    public FeatureTriggerCountData Snapshot() { lock (sync) return data; }
    public long Count(FeatureTriggerKind kind)
    {
        lock (sync) return kind switch
        {
            FeatureTriggerKind.AntiDisplacement => data.AntiDisplacement,
            FeatureTriggerKind.ForcedMovement => data.ForcedMovement,
            FeatureTriggerKind.GcdProtection => data.GcdProtection,
            _ => 0
        };
    }
    public void Record(FeatureTriggerKind kind)
    {
        lock (sync)
        {
            long value = Count(kind);
            if (value == long.MaxValue || !Enum.IsDefined(kind)) return;
            data = kind switch
            {
                FeatureTriggerKind.AntiDisplacement => data with { AntiDisplacement = value + 1 },
                FeatureTriggerKind.ForcedMovement => data with { ForcedMovement = value + 1 },
                _ => data with { GcdProtection = value + 1 }
            };
            revision++;
        }
    }

    public bool Flush()
    {
        lock (saveSync)
        {
            FeatureTriggerCountData snapshot;
            long captured;
            lock (sync)
            {
                if (path == null || revision == savedRevision) return true;
                if (readOnly) return false;
                snapshot = data; captured = revision;
            }
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, new JsonSerializerOptions { WriteIndented = true });
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough)) { stream.Write(bytes); stream.Flush(true); }
                if (invalidPrimary && File.Exists(path))
                    File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                invalidPrimary = false;
                if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
                else File.Move(temporary, path);
                lock (sync) savedRevision = captured;
                SaveError = null;
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { SaveError = "计数暂未保存，将自动重试：" + ex.GetType().Name; return false; }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
    public void Dispose() => Flush();
}
