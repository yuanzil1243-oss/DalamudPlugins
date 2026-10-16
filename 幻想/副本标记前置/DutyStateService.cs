using Dalamud.Plugin.Services;

namespace RaidMarkerPrerequisite;

public sealed class DutyStateService
{
    private readonly IClientState clientState;
    private readonly IPluginLog log;

    public ushort TerritoryId { get; private set; }
    public DutyData? CurrentDuty { get; private set; }
    private readonly IDutyDataStore dataStore;

    public DutyStateService(IClientState clientState, IPluginLog log, IDutyDataStore dataStore)
    {
        this.clientState = clientState;
        this.log = log;
        this.dataStore = dataStore;
    }

    public void Refresh(ushort territoryId)
    {
        TerritoryId = territoryId;
        CurrentDuty = dataStore.GetOrCreate(territoryId, $"Territory {territoryId}");
        dataStore.Save();
        log.Debug("副本前置：当前 TerritoryType={TerritoryId}，登录状态={LoggedIn}。", territoryId, clientState.IsLoggedIn);
    }
}
