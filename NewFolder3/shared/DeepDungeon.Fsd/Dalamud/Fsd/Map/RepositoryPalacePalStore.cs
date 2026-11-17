using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.Map;

/// <summary>Bounded background download; publication into game state happens only via Poll.</summary>
internal sealed class RepositoryPalacePalStore : IDisposable
{
    private readonly HttpClient _client;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _cachePath;
    private readonly Uri _url;
    private readonly string _expectedSha;
    private Task<IReadOnlyDictionary<ushort, RepositoryMarker[]>>? _pending;
    private DateTime _retryAfter;
    private bool _disposed;
    internal IReadOnlyDictionary<ushort, RepositoryMarker[]>? Data { get; private set; }
    internal bool Pending => _pending != null;
    internal string Status { get; private set; } = string.Empty;

    internal RepositoryPalacePalStore(string configDirectory, HttpMessageHandler? handler = null,
        Uri? url = null, string expectedSha = RepositoryPalacePalContract.Sha256)
    {
        _cachePath = Path.Combine(configDirectory, "RepositoryPalacePal", RepositoryPalacePalContract.DataVersion + ".json");
        _url = url ?? new Uri(RepositoryPalacePalContract.DownloadUrl);
        _expectedSha = expectedSha;
        _client = handler == null ? new HttpClient() : new HttpClient(handler, true);
        _client.Timeout = TimeSpan.FromSeconds(15);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("NewFolder3/0.7.55.40");
    }

    internal void Request(int territory, bool hasLocalData)
    {
        if (_disposed || hasLocalData || !RepositoryPalacePalContract.Supports(territory) ||
            Data != null || _pending != null || DateTime.UtcNow < _retryAfter) return;
        Status = "缺少本层段点位，正在加载仓库公共点位…";
        _pending = Task.Run(() => LoadAsync(_lifetime.Token), _lifetime.Token);
    }

    internal bool Poll()
    {
        if (_disposed || _pending?.IsCompleted != true) return false;
        var completed = _pending;
        _pending = null;
        try
        {
            Data = completed.GetAwaiter().GetResult();
            Status = "已加载仓库公共点位（历史候选点，不保证全覆盖）";
            return true;
        }
        catch (Exception error)
        {
            _retryAfter = DateTime.UtcNow.AddMinutes(1);
            Status = "公共点位加载失败，可稍后重试：" + error.Message;
            return false;
        }
    }

    private async Task<IReadOnlyDictionary<ushort, RepositoryMarker[]>> LoadAsync(CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        cancellation = deadline.Token;
        if (File.Exists(_cachePath))
        {
            try
            {
                if (new FileInfo(_cachePath).Length <= RepositoryPalacePalContract.MaximumBytes)
                    return RepositoryPalacePalContract.Parse(await File.ReadAllBytesAsync(_cachePath, cancellation), _expectedSha);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidOperationException)
            { /* Invalid cache is never used; try the trusted repository instead. */ }
        }
        using var response = await _client.GetAsync(_url, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > RepositoryPalacePalContract.MaximumBytes)
            throw new InvalidDataException("Public coordinate response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellation).ConfigureAwait(false)) != 0)
        {
            if (buffer.Length + count > RepositoryPalacePalContract.MaximumBytes)
                throw new InvalidDataException("Public coordinate response is too large.");
            buffer.Write(chunk, 0, count);
        }
        byte[] bytes = buffer.ToArray();
        var parsed = RepositoryPalacePalContract.Parse(bytes, _expectedSha);
        Directory.CreateDirectory(Path.GetDirectoryName(_cachePath)!);
        string temporary = _cachePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            File.Move(temporary, _cachePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return parsed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _client.Dispose();
        // Observe faults even when unloading before the next framework poll.
        if (_pending != null) _ = _pending.ContinueWith(t => _ = t.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }
}
