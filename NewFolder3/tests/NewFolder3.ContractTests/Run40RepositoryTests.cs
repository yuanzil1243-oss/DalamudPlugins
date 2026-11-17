using System.Net;
using System.Reflection;
using System.Collections;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;

namespace NewFolder3;

internal static class Run40RepositoryTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static byte[] Fixture(string type = "Hoard", int x = 1) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        schemaVersion = 1, dataVersion = RepositoryPalacePalContract.DataVersion,
        territories = Enumerable.Range(1281, 10).ToDictionary(t => t.ToString(), _ => new[] { new { t = type, x, y = 0, z = 2 } })
    });
    private static void Reject(byte[] bytes, string? hash = null)
    {
        try { RepositoryPalacePalContract.Parse(bytes, hash ?? Hash(bytes)); }
        catch (Exception error) when (error is InvalidDataException or JsonException or InvalidOperationException) { return; }
        throw new Exception("Invalid coordinates were accepted");
    }
    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return response(cancellationToken); }
    }
    private static HttpResponseMessage Ok(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "newfolder3-repository-" + Guid.NewGuid().ToString("N"));
        public string Cache => Path.Combine(Root, "RepositoryPalacePal", RepositoryPalacePalContract.DataVersion + ".json");
        public Workspace() => Directory.CreateDirectory(Root);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static RepositoryPalacePalStore Store(Workspace w, Handler handler, byte[] expected) =>
        new(w.Root, handler, new Uri("https://example.invalid/coordinates"), Hash(expected));
    private static void Finish(RepositoryPalacePalStore store)
    {
        var until = DateTime.UtcNow.AddSeconds(4);
        while (store.Pending && DateTime.UtcNow < until) { store.Poll(); Thread.Sleep(5); }
        Check(!store.Pending, "Background operation did not finish");
    }
    public static int Run()
    {
        var cases = new (string, Action)[]
        {
            ("repository-valid-coordinates-retain-type-and-scope", () => {
                var b = Fixture(); var data = RepositoryPalacePalContract.Parse(b, Hash(b));
                Check(data.Count == 10 && data[1284][0] == new RepositoryMarker("Hoard", 1, 0, 2), "coordinate roundtrip"); }),
            ("repository-hash-rejects-modified-bytes", () => Reject(Fixture(), new string('0', 64))),
            ("repository-size-is-bounded", () => Reject(new byte[RepositoryPalacePalContract.MaximumBytes + 1])),
            ("repository-rejects-unknown-marker-type", () => Reject(Fixture("Player"))),
            ("repository-rejects-known-invalid-21-30-coordinates", () => Reject(Fixture(x: 1001))),
            ("repository-rejects-unexpected-user-metadata", () => {
                var text = Encoding.UTF8.GetString(Fixture()).Replace("\"schemaVersion\":1", "\"player\":\"private\",\"schemaVersion\":1"); Reject(Encoding.UTF8.GetBytes(text)); }),
            ("repository-rejects-incomplete-territory-scope", () => {
                var text = Encoding.UTF8.GetString(Fixture()).Replace("\"1281\"", "\"9999\""); Reject(Encoding.UTF8.GetBytes(text)); }),
            ("repository-local-data-avoids-network", () => {
                using var w = new Workspace(); var b = Fixture(); var h = new Handler(_ => Task.FromResult(Ok(b)));
                using var s = Store(w, h, b); s.Request(1284, true); Check(!s.Pending && h.Calls == 0, "local precedence"); }),
            ("repository-other-dungeons-avoid-network", () => {
                using var w = new Workspace(); var b = Fixture(); var h = new Handler(_ => Task.FromResult(Ok(b)));
                using var s = Store(w, h, b); s.Request(770, false); Check(!s.Pending && h.Calls == 0, "only published traversal data"); }),
            ("repository-request-is-single-flight-and-does-not-block", SingleFlight),
            ("repository-verified-cache-works-without-network", OfflineCache),
            ("repository-corrupt-cache-is-redownloaded", CorruptCache),
            ("repository-http-error-is-bounded-and-throttled", HttpFailure),
            ("repository-corrupt-response-does-not-replace-cache", InvalidResponse),
            ("repository-oversized-response-is-not-cached", TooLarge),
            ("repository-unload-cancels-inflight-download", Cancellation),
            ("signed-map-unload-cancels-a-stalled-response-body", CatalogBodyCancellation),
            ("repository-fallback-fills-empty-memory-without-writing-personal-cache", () => PersonalCacheIsolation(false)),
            ("repository-fallback-cannot-replace-personal-territory", () => PersonalCacheIsolation(true)),
        };
        foreach (var (name, action) in cases) { action(); Console.WriteLine("  ok  " + name); }
        return cases.Length;
    }
    private static void SingleFlight()
    {
        using var w = new Workspace(); byte[] b = Fixture(); var gate = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Handler(ct => gate.Task.WaitAsync(ct)); using var s = Store(w, h, b);
        s.Request(1284, false); s.Request(1285, false);
        Check(s.Pending && s.Data == null && !File.Exists(w.Cache), "no partial publication");
        gate.SetResult(Ok(b)); Finish(s);
        Check(h.Calls == 1 && s.Data?.Count == 10 && File.ReadAllBytes(w.Cache).SequenceEqual(b), "one verified atomic publication");
    }
    private static void OfflineCache()
    {
        using var w = new Workspace(); byte[] b = Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(w.Cache)!); File.WriteAllBytes(w.Cache, b);
        var h = new Handler(_ => throw new Exception("Network must not be used")); using var s = Store(w, h, b); s.Request(1284, false); Finish(s);
        Check(s.Data?.Count == 10 && h.Calls == 0, "valid cache offline");
    }
    private static void CorruptCache()
    {
        using var w = new Workspace(); byte[] b = Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(w.Cache)!); File.WriteAllText(w.Cache, "broken");
        var h = new Handler(_ => Task.FromResult(Ok(b))); using var s = Store(w, h, b); s.Request(1284, false); Finish(s);
        Check(h.Calls == 1 && s.Data != null && File.ReadAllBytes(w.Cache).SequenceEqual(b), "invalid cache refreshed");
    }
    private static void HttpFailure()
    {
        using var w = new Workspace(); byte[] b = Fixture(); var h = new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var s = Store(w, h, b); s.Request(1284, false); Finish(s); s.Request(1284, false);
        Check(s.Data == null && h.Calls == 1 && !s.Pending && !File.Exists(w.Cache) && s.Status.Contains("失败"), "failure cooldown and visible status");
    }
    private static void InvalidResponse()
    {
        using var w = new Workspace(); byte[] b = Fixture(); Directory.CreateDirectory(Path.GetDirectoryName(w.Cache)!); File.WriteAllText(w.Cache, "old-invalid-cache");
        var h = new Handler(_ => Task.FromResult(Ok(Encoding.UTF8.GetBytes("{}")))); using var s = Store(w, h, b); s.Request(1284, false); Finish(s);
        Check(s.Data == null && File.ReadAllText(w.Cache) == "old-invalid-cache", "unverified response cannot overwrite disk");
    }
    private static void TooLarge()
    {
        using var w = new Workspace(); byte[] b = Fixture(); var h = new Handler(_ => Task.FromResult(Ok(new byte[RepositoryPalacePalContract.MaximumBytes + 1])));
        using var s = Store(w, h, b); s.Request(1284, false); Finish(s); Check(s.Data == null && !File.Exists(w.Cache), "size rejection");
    }
    private static void Cancellation()
    {
        using var w = new Workspace(); byte[] b = Fixture(); var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var h = new Handler(async ct => { entered.SetResult(true); try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { cancelled.SetResult(true); throw; } return Ok(b); });
        var s = Store(w, h, b); s.Request(1284, false); Check(entered.Task.Wait(2000), "download entered"); s.Dispose();
        Check(cancelled.Task.Wait(2000) && !s.Poll() && s.Data == null && !File.Exists(w.Cache), "unload cannot publish late result");
    }

    private sealed class StalledStream : MemoryStream
    {
        internal readonly TaskCompletionSource<bool> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(true);
            try { await Task.Delay(Timeout.Infinite, cancellationToken); }
            catch (OperationCanceledException) { Cancelled.TrySetResult(true); throw; }
            return 0;
        }
    }
    private sealed class StreamHandler(StalledStream stream) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) });
    }
    private static void CatalogBodyCancellation()
    {
        using var w = new Workspace();
        using var stream = new StalledStream();
        using var manager = new DetailedMapCatalogManager(w.Root,
            new DetailedMapHostOptions(new Uri("https://example.invalid/"), false, false, false), new StreamHandler(stream));
        manager.Update(true, "pt-21-30", false);
        Check(stream.Entered.Task.Wait(2000), "response headers completed and body read began");
        manager.Dispose();
        Check(stream.Cancelled.Task.Wait(2000) && !Directory.EnumerateFiles(w.Root, "installed.json", SearchOption.AllDirectories).Any(),
            "unload cancels body reads and cannot install a partial catalog");
    }

    private static void PersonalCacheIsolation(bool hasPersonalData)
    {
        using var w = new Workspace();
        byte[] bytes = Fixture();
        var handler = new Handler(_ => Task.FromResult(Ok(bytes)));
        using var store = Store(w, handler, bytes);
        store.Request(1284, false); Finish(store);
        PalacePalData.Shutdown();
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        var type = typeof(PalacePalData);
        void Set(string name, object value) => type.GetField(name, flags)!.SetValue(null, value);
        string personalPath = Path.Combine(w.Root, "personal.json");
        try
        {
            Set("_initialized", true); Set("_lastTerritory", (ushort)1284);
            Set("_localFilePath", personalPath); Set("_repository", store);
            if (hasPersonalData)
            {
                var cache = (IDictionary)type.GetField("_localByTerritory", flags)!.GetValue(null)!;
                var list = (IList)Activator.CreateInstance(cache.GetType().GetGenericArguments()[1])!;
                var markerType = type.GetNestedType("Marker", BindingFlags.NonPublic)!;
                var marker = Activator.CreateInstance(markerType)!;
                markerType.GetProperty("Type")!.SetValue(marker, "Hoard");
                markerType.GetProperty("X")!.SetValue(marker, 9f);
                list.Add(marker); cache[(ushort)1284] = list;
                var counts = (IDictionary)type.GetField("_validCountByTerritory", flags)!.GetValue(null)!;
                counts[(ushort)1284] = 1;
            }
            long revision = PalacePalData.Revision;
            type.GetMethod("ApplyRepository_NoLock", flags)!.Invoke(null, null);
            var positions = PalacePalData.GetCandidatePositionsCurrentTerritory();
            Check(PalacePalData.Revision > revision && PalacePalData.HasLocalCacheForCurrentTerritory &&
                positions.Count == 1 && positions[0].X == (hasPersonalData ? 9 : 1),
                "new public points invalidate caches but personal data retains precedence");
            type.GetMethod("SaveLocal_NoLock", flags)!.Invoke(null, null);
            using var saved = JsonDocument.Parse(File.ReadAllBytes(personalPath));
            Check(saved.RootElement.GetProperty("territories").EnumerateObject().Count() == (hasPersonalData ? 1 : 0),
                "public points must remain solely in their verified repository cache");
            Check(File.ReadAllBytes(w.Cache).SequenceEqual(bytes), "public snapshot persists independently");
        }
        finally { PalacePalData.Shutdown(); }
    }

    public static int ValidateLiveRepository()
    {
        foreach (string scenario in new[] { "pt-21-30", "pt-31-40" })
        {
            using var w = new Workspace();
            Check(!Directory.EnumerateFileSystemEntries(w.Root).Any(), "fresh configuration");
            using var manager = new DetailedMapCatalogManager(w.Root, NewFolder3BuildProfile.CreateDetailedMapHostOptions());
            manager.Update(true, scenario, false);
            var until = DateTime.UtcNow.AddSeconds(90);
            while (manager.GetStatus(true, scenario).Checking && DateTime.UtcNow < until)
            { Thread.Sleep(10); manager.Update(true, scenario, false); }
            var status = manager.GetStatus(true, scenario);
            Check(status.HasValidCatalog && manager.TryAcquireRunSnapshot(true, scenario, out var snapshot, out _) &&
                snapshot.Policy == DetailedMapRuntimePolicy.DetailedMap && snapshot.HoardYield != null,
                "live repository must load signed map and yield data into an empty configuration: " + status.Message);
            manager.ReleaseRunSnapshot();
            using var offline = new DetailedMapCatalogManager(w.Root, new DetailedMapHostOptions(null, false, false, false));
            Check(offline.GetStatus(true, scenario).HasValidCatalog &&
                offline.TryAcquireRunSnapshot(true, scenario, out var cached, out _) && cached.HoardYield != null,
                "live download must survive restart without a network service");
            Console.WriteLine("  ok  live-empty-config-" + scenario + "-signed-download-and-offline-restart");
        }
        using (var w = new Workspace())
        {
            using var store = new RepositoryPalacePalStore(w.Root);
            store.Request(1284, false);
            var until = DateTime.UtcNow.AddSeconds(20);
            while (store.Pending && DateTime.UtcNow < until) { store.Poll(); Thread.Sleep(10); }
            Check(store.Data?.Count == 10 && File.Exists(w.Cache), "live public coordinates: " + store.Status);
            byte[] bytes = File.ReadAllBytes(w.Cache);
            var handler = new Handler(_ => throw new Exception("offline reload must not use HTTP"));
            using var offline = Store(w, handler, bytes); offline.Request(1284, false); Finish(offline);
            Check(offline.Data?.Count == 10 && handler.Calls == 0, "live coordinate snapshot reloads offline");
            Console.WriteLine("  ok  live-empty-config-coordinates-download-and-offline-restart");
        }
        return 3;
    }

    public static int ValidatePublishedData(string directory)
    {
        string root = Path.GetFullPath(directory);
        var bytes = File.ReadAllBytes(Path.Combine(root, "palacepal", RepositoryPalacePalContract.DataVersion + ".json"));
        var data = RepositoryPalacePalContract.Parse(bytes);
        Check(data.Count == 10 && data[1284].Length > 0 && data.Values.SelectMany(x => x).Count(x => x.Type == "Hoard") == 80,
            "published historical coverage");
        Console.WriteLine("  ok  published-coordinate-artifact-sha-and-coverage");
        foreach (string scenario in new[] { "pt-21-30", "pt-31-40" })
        {
            using var w = new Workspace();
            using var manager = new DetailedMapCatalogManager(w.Root, new DetailedMapHostOptions(new Uri("https://example.invalid/"), false, false, false),
                new FilesHandler(Path.Combine(root, "detailed")));
            manager.Update(true, scenario, false);
            var until = DateTime.UtcNow.AddSeconds(5);
            while (manager.GetStatus(true, scenario).Checking && DateTime.UtcNow < until) { Thread.Sleep(5); manager.Update(true, scenario, false); }
            var status = manager.GetStatus(true, scenario);
            Check(status.HasValidCatalog && !status.RetryAvailable && manager.TryAcquireRunSnapshot(true, scenario, out var snapshot, out _) &&
                snapshot.Policy == DetailedMapRuntimePolicy.DetailedMap, "new install downloads original signed catalog: " + status.Message);
            manager.ReleaseRunSnapshot();
            using var offline = new DetailedMapCatalogManager(w.Root, new DetailedMapHostOptions(null, false, false, false));
            Check(offline.GetStatus(true, scenario).HasValidCatalog, "downloaded signed catalog persists offline");
            Console.WriteLine("  ok  published-signed-" + scenario + "-downloads-into-empty-config-and-reloads-offline");
        }
        return 3;
    }
    private sealed class FilesHandler(string directory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = Path.Combine(directory, request.RequestUri!.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            return Task.FromResult(File.Exists(path) ? Ok(File.ReadAllBytes(path)) : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
