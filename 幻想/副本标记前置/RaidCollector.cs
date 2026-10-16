using System.Text.Json;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RaidMarkerPrerequisite;

public sealed class RaidCollector : IDisposable
{
    private readonly IClientState clientState;
    private readonly IObjectTable objectTable;
    private readonly IPartyList partyList;
    private readonly IPluginLog log;
    private string path;
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private StreamWriter? writer;
    private long nextSample;

    public bool IsRecording { get; private set; }
    public string? CurrentFile => IsRecording ? path : null;

    public RaidCollector(IDalamudPluginInterface pluginInterface, IClientState clientState, IObjectTable objectTable, IPartyList partyList, IPluginLog log)
    {
        this.clientState = clientState;
        this.objectTable = objectTable;
        this.partyList = partyList;
        this.log = log;
        var directory = Path.Combine(pluginInterface.GetPluginConfigDirectory(), "captures");
        Directory.CreateDirectory(directory);
        path = Path.Combine(directory, "capture-pending.jsonl");
    }

    public void Start()
    {
        if (IsRecording) return;
        path = Path.Combine(Path.GetDirectoryName(path)!, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
        writer = new StreamWriter(path, false) { AutoFlush = true };
        IsRecording = true;
        Write("session_start", new { territoryId = clientState.TerritoryType });
    }

    public void Stop()
    {
        if (!IsRecording) return;
        Write("session_stop", new { territoryId = clientState.TerritoryType });
        writer?.Dispose();
        writer = null;
        IsRecording = false;
    }

    public void Update()
    {
        if (!IsRecording || !clientState.IsLoggedIn || Environment.TickCount64 < nextSample) return;
        nextSample = Environment.TickCount64 + 500;
        var actors = objectTable.OfType<IBattleChara>().Where(x => x.IsTargetable).Take(128).Select(x => new
        {
            entityId = x.EntityId,
            name = x.Name.TextValue,
            castActionId = x.CastActionId,
            currentCastTime = x.CurrentCastTime,
            totalCastTime = x.TotalCastTime,
            statuses = x.StatusList.Select(s => new { id = s.StatusId, remaining = s.RemainingTime, param = s.Param, sourceId = s.SourceId }).ToArray(),
        }).ToArray();
        var party = partyList.Select(x => new { entityId = x.EntityId, name = x.Name.ToString(), jobId = x.ClassJob.RowId }).ToArray();
        Write("sample", new { territoryId = clientState.TerritoryType, actors, party });
    }

    private void Write(string kind, object payload)
    {
        if (writer is null) return;
        writer.WriteLine(JsonSerializer.Serialize(new { timestampUtc = DateTime.UtcNow, kind, payload }, json));
    }

    public void Dispose() => Stop();
}
