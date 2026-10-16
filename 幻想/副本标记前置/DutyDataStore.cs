using System.Text.Json;
using Dalamud.Plugin;

namespace RaidMarkerPrerequisite;

public sealed class DutyDataStore : IDutyDataStore
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly Dictionary<ushort, DutyData> duties = new();
    private readonly JsonSerializerOptions options = new() { WriteIndented = true };

    public DutyDataStore(IDalamudPluginInterface pluginInterface)
    {
        this.pluginInterface = pluginInterface;
        Load();
    }

    public IReadOnlyCollection<DutyData> Duties => duties.Values;

    public DutyData GetOrCreate(ushort territoryId, string territoryName)
    {
        if (!duties.TryGetValue(territoryId, out var duty))
        {
            duty = new DutyData
            {
                TerritoryId = territoryId,
                TerritoryName = territoryName,
                FirstSeenUtc = DateTime.UtcNow,
            };
            duties[territoryId] = duty;
        }

        duty.TerritoryName = territoryName;
        duty.LastSeenUtc = DateTime.UtcNow;
        duty.VisitCount++;
        return duty;
    }

    public void Save()
    {
        var path = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "duties.json");
        var json = JsonSerializer.Serialize(duties.Values.OrderBy(x => x.TerritoryId), options);
        File.WriteAllText(path, json);
    }

    private void Load()
    {
        var path = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "duties.json");
        if (!File.Exists(path))
            return;

        try
        {
            var loaded = JsonSerializer.Deserialize<List<DutyData>>(File.ReadAllText(path));
            if (loaded is null)
                return;
            foreach (var duty in loaded)
                duties[duty.TerritoryId] = duty;
        }
        catch
        {
            // 损坏的采集文件不应阻止插件启动，下一次保存会生成新文件。
        }
    }
}
