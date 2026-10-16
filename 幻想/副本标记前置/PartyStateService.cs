using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;

namespace RaidMarkerPrerequisite;

public sealed class PartyStateService
{
    private readonly IPartyList partyList;
    private readonly IObjectTable objectTable;
    private readonly IPluginLog log;

    public PartyStateService(IPartyList partyList, IObjectTable objectTable, IPluginLog log)
    {
        this.partyList = partyList;
        this.objectTable = objectTable;
        this.log = log;
    }

    public void Update(MechanicState state)
    {
        var present = new HashSet<uint>();
        foreach (var member in partyList)
        {
            var entityId = member.EntityId;
            if (entityId == 0)
                continue;

            present.Add(entityId);
            if (!state.Players.TryGetValue(entityId, out var player))
            {
                player = new PlayerState
                {
                    EntityId = entityId,
                    Name = member.Name.ToString(),
                    JobId = member.ClassJob.RowId,
                };
                state.Players[entityId] = player;
            }

            player.IsPresent = true;
            player.Statuses.Clear();
            if (objectTable.SearchById(entityId) is IBattleChara battleChara)
            {
                foreach (var status in battleChara.StatusList)
                    player.Statuses.Add(new StatusState(checked((ushort)status.StatusId), status.RemainingTime, checked((byte)Math.Min(status.Param, byte.MaxValue)), status.SourceId));
            }
        }

        foreach (var player in state.Players.Values)
            player.IsPresent = present.Contains(player.EntityId);
    }

    public PartySnapshot Capture(MechanicState state)
    {
        return new PartySnapshot
        {
            CapturedUtc = DateTime.UtcNow,
            Members = state.Players.Values.Where(x => x.IsPresent).Select(player => new PartyMemberSnapshot
            {
                EntityId = player.EntityId,
                Name = player.Name,
                JobId = player.JobId,
                Statuses = player.Statuses.Select(status => new StatusSnapshot
                {
                    StatusId = status.StatusId,
                    RemainingTime = status.RemainingTime,
                    Stacks = status.Stacks,
                    SourceId = status.SourceId,
                }).ToList(),
            }).ToList(),
        };
    }
}
