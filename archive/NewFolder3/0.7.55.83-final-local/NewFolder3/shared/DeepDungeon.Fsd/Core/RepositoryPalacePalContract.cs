using System.Security.Cryptography;
using System.Text.Json;

namespace DeepDungeon.Fsd.Core;

public readonly record struct RepositoryMarker(string Type, float X, float Y, float Z);

/// <summary>Public historical coordinates, independent of personal PalacePal configuration.</summary>
public static class RepositoryPalacePalContract
{
    public const string DataVersion = "20261118";
    public const string Sha256 = "b58967cd3d884a1c68cfad534acefc4d0891200278b794e33a8e3cb42f96309c";
    public const string DownloadUrl = "https://raw.githubusercontent.com/yuanzil1243-oss/DalamudPlugins/main/data/NewFolder3/palacepal/20261118.json";
    public const int MaximumBytes = 512 * 1024;
    public static bool Supports(int territory) => territory is >= 1281 and <= 1290;

    public static IReadOnlyDictionary<ushort, RepositoryMarker[]> Parse(byte[] bytes, string expectedSha256 = Sha256)
    {
        if (bytes.Length == 0 || bytes.Length > MaximumBytes ||
            !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Repository coordinate size or SHA256 validation failed.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        RequireFields(root, "schemaVersion", "dataVersion", "territories");
        if (root.GetProperty("schemaVersion").GetInt32() != 1 || root.GetProperty("dataVersion").GetString() != DataVersion)
            throw new InvalidDataException("Unsupported repository coordinate version.");
        var result = new Dictionary<ushort, RepositoryMarker[]>();
        foreach (var territory in root.GetProperty("territories").EnumerateObject())
        {
            if (!ushort.TryParse(territory.Name, out var id) || !Supports(id) || result.ContainsKey(id))
                throw new InvalidDataException("Unexpected or duplicate coordinate territory.");
            if (territory.Value.GetArrayLength() is < 1 or > 10000)
                throw new InvalidDataException("Invalid coordinate count.");
            var markers = new List<RepositoryMarker>();
            foreach (var item in territory.Value.EnumerateArray())
            {
                RequireFields(item, "t", "x", "y", "z");
                string? type = item.GetProperty("t").GetString();
                float x = item.GetProperty("x").GetSingle(), y = item.GetProperty("y").GetSingle(), z = item.GetProperty("z").GetSingle();
                if (type is not ("Trap" or "Hoard") || PalacePalImportPolicy.GetQuarantineReason(id, x, y, z) != null)
                    throw new InvalidDataException("Invalid repository coordinate.");
                markers.Add(new(type, x, y, z));
            }
            result.Add(id, markers.ToArray());
        }
        if (result.Count != 10) throw new InvalidDataException("Repository coordinates must declare all ten supported territories.");
        return result;
    }

    private static void RequireFields(JsonElement value, params string[] fields)
    {
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != fields.Length || actual.Distinct().Count() != fields.Length || actual.Except(fields).Any())
            throw new InvalidDataException("Unexpected repository coordinate fields.");
    }
}
