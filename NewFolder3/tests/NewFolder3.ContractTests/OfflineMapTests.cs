using System.Reflection;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud;
using DeepDungeon.Fsd.Dalamud.GameState;

namespace NewFolder3;

internal static class OfflineMapTests
{
    internal static int Run()
    {
        int passed = 0;
        void Check(string name, Action action) { action(); Console.WriteLine("  ok  offline-map-" + name); passed++; }
        string root = Path.Combine(Path.GetTempPath(), "newfolder3-offline-map-" + Guid.NewGuid().ToString("N"));
        string bundle = Path.Combine(AppContext.BaseDirectory, "DetailedMaps");
        var log = typeof(Service).GetProperty("Log", BindingFlags.NonPublic | BindingFlags.Static)!;
        object? previousLog = log.GetValue(null);
        log.SetValue(null, DispatchProxy.Create<IPluginLog, SilentLog>());
        var options = new DetailedMapHostOptions(null, false, false);
        try
        {
            Check("stream-loaded-dependency-uses-host-plugin-path", () =>
            {
                string host = Path.Combine(root, "installed-plugin", "NewFolder3.dll");
                string fallback = Path.Combine(root, "game-directory");
                Assert(DetailedMapCatalogManager.ResolveBundledCatalogRoot(host, "", fallback) ==
                    Path.Combine(root, "installed-plugin", "DetailedMaps"));
                Assert(DetailedMapCatalogManager.ResolveBundledCatalogRoot(host,
                    Path.Combine(root, "other-assembly", "dependency.dll"), fallback) ==
                    Path.Combine(root, "installed-plugin", "DetailedMaps"));
            });
            Check("missing-assembly-paths-have-safe-fallback", () =>
            {
                string fallback = Path.Combine(root, "test-host");
                Assert(DetailedMapCatalogManager.ResolveBundledCatalogRoot(null, "", fallback) ==
                    Path.Combine(fallback, "DetailedMaps"));
                Assert(DetailedMapCatalogManager.ResolveBundledCatalogRoot(null,
                    Path.Combine(root, "dependency.dll"), fallback) == Path.Combine(root, "DetailedMaps"));
            });
            Check("fresh-user-two-signed-bundles-no-http-or-config-writes", () =>
            {
                string config = Path.Combine(root, "fresh");
                using var manager = new DetailedMapCatalogManager(config, options);
                Assert(!manager.HasOnlineCatalogService);
                foreach (var scope in DetailedMapScenarioCatalog.Definitions)
                {
                    manager.Update(true, scope.Key, false);
                    var status = manager.GetStatus(true, scope.Key);
                    Assert(status.HasValidCatalog && !status.Checking && !status.RetryAvailable &&
                        status.ReleaseId == "2026-11-25.1" && manager.IsOfflineDetailedMap);
                    Assert(manager.TryAcquireRunSnapshot(true, scope.Key, out var snapshot, out _));
                    Assert(snapshot.Policy == DetailedMapRuntimePolicy.DetailedMap && snapshot.HoardYield != null);
                    Assert(snapshot.Catalog!.Rooms.Length == 42);
                    manager.ReleaseRunSnapshot();
                }
                Assert(!Directory.Exists(config));
            });
            Check("installed-official-cache-works-without-service", () =>
            {
                string config = Path.Combine(root, "official-cache");
                CopyTree(bundle, Path.Combine(config, "DetailedMapCatalogs"));
                var before = Files(config);
                using var manager = new DetailedMapCatalogManager(config, options, bundledCatalogRoot: Path.Combine(root, "absent"));
                foreach (var scope in DetailedMapScenarioCatalog.Definitions)
                {
                    Assert(manager.GetStatus(true, scope.Key).HasValidCatalog);
                    Assert(manager.TryAcquireRunSnapshot(true, scope.Key, out _, out _));
                    manager.ReleaseRunSnapshot();
                }
                manager.Update(false, "pt-21-30", false);
                Assert(before.SequenceEqual(Files(config)));
            });
            foreach (string filename in new[] { "catalog.json", "catalog.sig.json", "hoard-yield-catalog.json" })
                Check("tampered-bundle-rejected-" + filename, () =>
                {
                    string bad = Path.Combine(root, "bad-" + filename);
                    CopyTree(bundle, bad);
                    foreach (var path in Directory.GetFiles(bad, filename, SearchOption.AllDirectories))
                    {
                        if (filename == "catalog.sig.json")
                        {
                            string text = File.ReadAllText(path);
                            File.WriteAllText(path, text.Replace("catalog-p256-v1", "untrusted-key"));
                        }
                        else File.AppendAllText(path, " ");
                    }
                    using var manager = new DetailedMapCatalogManager(Path.Combine(root, "empty-" + filename),
                        options, bundledCatalogRoot: bad);
                    foreach (var scope in DetailedMapScenarioCatalog.Definitions)
                    {
                        Assert(!manager.GetStatus(true, scope.Key).HasValidCatalog);
                        Assert(!manager.TryAcquireRunSnapshot(true, scope.Key, out _, out _));
                    }
                });
            Check("invalid-bundle-does-not-replace-valid-user-cache", () =>
            {
                string config = Path.Combine(root, "official-cache");
                var before = Files(config);
                using var manager = new DetailedMapCatalogManager(config, options,
                    bundledCatalogRoot: Path.Combine(root, "bad-catalog.json"));
                Assert(manager.GetStatus(true, "pt-21-30").HasValidCatalog);
                Assert(manager.TryAcquireRunSnapshot(true, "pt-21-30", out _, out _));
                manager.ReleaseRunSnapshot();
                Assert(before.SequenceEqual(Files(config)));
            });
            Check("date-and-numeric-revision-preference", () =>
            {
                Assert(DetailedMapCatalogManager.PreferBundled("2026-11-23.10", "2026-11-23.9", true));
                Assert(!DetailedMapCatalogManager.PreferBundled("2026-11-23.9", "2026-11-23.10", true));
                Assert(!DetailedMapCatalogManager.PreferBundled("2026-11-23.1", "2026-11-24.1", true));
                Assert(DetailedMapCatalogManager.PreferBundled("2026-11-24.1", "2026-11-23.10", true));
                Assert(!DetailedMapCatalogManager.PreferBundled("2026-11-23.1", "2026-11-23.1", true));
                Assert(DetailedMapCatalogManager.PreferBundled("2026-11-23.1", "2026-11-23.1", false));
                Assert(!DetailedMapCatalogManager.PreferBundled("unknown", "2026-11-23.1", false));
            });
            Check("swapped-installed-scope-rejected", () =>
            {
                string config = Path.Combine(root, "swapped-cache");
                CopyTree(Path.Combine(bundle, "pt-21-30"), Path.Combine(config, "DetailedMapCatalogs", "pt-31-40"));
                using var manager = new DetailedMapCatalogManager(config, options, bundledCatalogRoot: Path.Combine(root, "absent"));
                Assert(!manager.GetStatus(true, "pt-31-40").HasValidCatalog);
                Assert(!manager.TryAcquireRunSnapshot(true, "pt-31-40", out _, out _));
            });
        }
        finally
        {
            log.SetValue(null, previousLog);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
        return passed;
    }
    private static string[] Files(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order()
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
    private static void CopyTree(string source, string target)
    {
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(file, dest);
        }
    }
    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Offline map contract failed."); }
    public class SilentLog : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) => method?.ReturnType == typeof(void)
            ? null : method?.ReturnType.IsValueType == true ? Activator.CreateInstance(method.ReturnType) : null;
    }
}
