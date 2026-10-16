namespace RaidMarkerPrerequisite;

public sealed class DutyData
{
    public ushort TerritoryId { get; set; }
    public string TerritoryName { get; set; } = string.Empty;
    public DateTime FirstSeenUtc { get; set; }
    public DateTime LastSeenUtc { get; set; }
    public int VisitCount { get; set; }
    public List<PartySnapshot> PartySnapshots { get; set; } = new();
    public HashSet<ushort> ObservedStatusIds { get; set; } = new();
}

public sealed class PartySnapshot
{
    public DateTime CapturedUtc { get; set; }
    public List<PartyMemberSnapshot> Members { get; set; } = new();
}

public sealed class PartyMemberSnapshot
{
    public uint EntityId { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint JobId { get; set; }
    public List<StatusSnapshot> Statuses { get; set; } = new();
}

public sealed class StatusSnapshot
{
    public ushort StatusId { get; set; }
    public float RemainingTime { get; set; }
    public byte Stacks { get; set; }
    public uint SourceId { get; set; }
}

public interface IDutyDataStore
{
    IReadOnlyCollection<DutyData> Duties { get; }
    DutyData GetOrCreate(ushort territoryId, string territoryName);
    void Save();
}

public interface IDutyContext
{
    ushort TerritoryId { get; }
    DutyData? CurrentDuty { get; }
    IReadOnlyDictionary<uint, PlayerState> Players { get; }
}

public interface IMarkerController
{
    bool IsSupported { get; }
    void SetMarker(uint actorId, uint markerId);
    void ClearMarker(uint actorId);
    void ClearAll();
}

public sealed class DutyContext : IDutyContext
{
    private readonly MechanicState state;
    private readonly DutyStateService dutyState;

    public DutyContext(MechanicState state, DutyStateService dutyState)
    {
        this.state = state;
        this.dutyState = dutyState;
    }

    public ushort TerritoryId => state.TerritoryId;
    public DutyData? CurrentDuty => dutyState.CurrentDuty;
    public IReadOnlyDictionary<uint, PlayerState> Players => state.Players;
}

public sealed class NullMarkerController : IMarkerController
{
    public bool IsSupported => false;
    public void SetMarker(uint actorId, uint markerId) { }
    public void ClearMarker(uint actorId) { }
    public void ClearAll() { }
}
