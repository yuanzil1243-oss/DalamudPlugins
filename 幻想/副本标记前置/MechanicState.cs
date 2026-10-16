namespace RaidMarkerPrerequisite;

public sealed class MechanicState
{
    public ushort TerritoryId { get; private set; }
    public Dictionary<uint, PlayerState> Players { get; } = new();
    public HashSet<uint> MarkedActors { get; } = new();

    public void Reset(ushort territoryId)
    {
        TerritoryId = territoryId;
        Players.Clear();
        MarkedActors.Clear();
    }
}

public sealed class PlayerState
{
    public uint EntityId { get; init; }
    public string Name { get; init; } = string.Empty;
    public uint JobId { get; init; }
    public bool IsPresent { get; set; }
    public List<StatusState> Statuses { get; } = new();
}

public readonly record struct StatusState(ushort StatusId, float RemainingTime, byte Stacks, uint SourceId);
