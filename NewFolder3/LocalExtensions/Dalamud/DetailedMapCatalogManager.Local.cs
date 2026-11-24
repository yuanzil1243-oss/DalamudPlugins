using System.Globalization;
using System.Text.Json;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud.GameState;

internal sealed partial class DetailedMapCatalogManager
{
    private readonly string _bundledCatalogRoot;
    internal bool HasUsableDetailedMap => _catalogBaseUri != null || _currentCatalog != null;
    internal bool IsOfflineDetailedMap => _catalogBaseUri == null && _currentCatalog != null;

    // Dalamud loads dependency assemblies from streams, where Assembly.Location is empty.
    // Its plugin interface retains the actual installation path for both dev and installed plugins.
    internal static string ResolveBundledCatalogRoot(string? hostAssemblyPath, string? runtimeAssemblyPath,
        string fallbackDirectory)
    {
        foreach (string? path in new[] { hostAssemblyPath, runtimeAssemblyPath })
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory)) return Path.Combine(directory, "DetailedMaps");
        }
        return Path.Combine(fallbackDirectory, "DetailedMaps");
    }

    // Read bundled data in place: never overwrite a user's cache/config or delete official downloads.
    private void LoadBundledCatalogIfPreferred()
    {
        try
        {
            string scenarioRoot = Path.Combine(_bundledCatalogRoot, _loadedScenarioKey);
            string pointerPath = Path.Combine(scenarioRoot, InstalledPointerFileName);
            if (!File.Exists(pointerPath)) return;
            DetailedMapCatalogLatest latest = DetailedMapCatalogContract.ParseLatest(File.ReadAllBytes(pointerPath));
            ValidateLatestPaths(latest, _loadedScenarioKey);
            if (_currentCatalog != null && !PreferBundled(latest.ReleaseId, _currentCatalog.ReleaseId,
                    IsInstalledReleaseComplete(_currentCatalog, _currentHoardYield))) return;
            string releaseRoot = Path.Combine(scenarioRoot, "releases", $"{latest.ReleaseId}-{latest.CatalogSha256[..12]}");
            byte[] bytes = File.ReadAllBytes(Path.Combine(releaseRoot, DetailedMapCatalogContract.CatalogFileName));
            var catalog = ValidateDownloadedCatalog(latest, bytes,
                File.ReadAllBytes(Path.Combine(releaseRoot, DetailedMapCatalogContract.SignatureFileName)), _loadedScenarioKey);
            HoardYieldCatalog? yield = catalog.HoardYieldSha256 == null ? null : ValidateHoardYield(catalog,
                File.ReadAllBytes(Path.Combine(releaseRoot, HoardYieldCatalogContract.FileName)));
            var presentations = new Dictionary<(int LayoutIndex, int RoomIndex), DetailedMapRoomGraphPresentation>();
            catalog.WarmRoomLookup();
            foreach (var room in catalog.Rooms)
                presentations.Add((room.LayoutIndex, room.RoomIndex), DetailedMapRoomGraphAnalyzer.Analyze(room));
            _presentations.Clear();
            foreach (var pair in presentations) _presentations.Add(pair.Key, pair.Value);
            _currentCatalog = catalog;
            _currentHoardYield = yield;
            _installedPointer = latest;
            _localLoadError = _hoardYieldLoadError = string.Empty;
            Service.Log.Info($"[LocalExtensions.Maps] Verified bundled {catalog.ScenarioKey} / {catalog.ReleaseId}.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or
            JsonException or ArgumentException or System.Security.Cryptography.CryptographicException)
        {
            // A broken bundle must not replace a valid installed catalog.
            if (_currentCatalog == null) _localLoadError = error.Message;
            Service.Log.Warning($"[LocalExtensions.Maps] Bundled catalog rejected: {error.Message}");
        }
    }

    internal static bool PreferBundled(string bundled, string installed, bool installedComplete)
    {
        static bool Parse(string release, out DateOnly day, out int revision)
        {
            day = default; revision = 0;
            int dot = release.LastIndexOf('.');
            return dot > 0 && DateOnly.TryParseExact(release[..dot], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out day) && int.TryParse(release[(dot + 1)..], out revision) && revision >= 0;
        }
        if (!Parse(bundled, out var bd, out var br) || !Parse(installed, out var id, out var ir)) return false;
        int compare = bd.CompareTo(id);
        return compare > 0 || compare == 0 && (br > ir || br == ir && !installedComplete);
    }
}
