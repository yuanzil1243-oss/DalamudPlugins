using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace RaidMarkerPrerequisite;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string Command = "/副本标记";
    private readonly DutyStateService dutyState;
    private readonly PartyStateService partyState;
    private readonly MechanicState state = new();
    private readonly DutyDataStore dataStore;
    private long nextSnapshotAt;
    private readonly RaidCollector collector;

    public Plugin()
    {
        dataStore = new DutyDataStore(PluginInterface);
        dutyState = new DutyStateService(ClientState, Log, dataStore);
        partyState = new PartyStateService(PartyList, ObjectTable, Log);
        collector = new RaidCollector(PluginInterface, ClientState, ObjectTable, PartyList, Log);
        ClientState.TerritoryChanged += OnTerritoryChanged;
        Framework.Update += OnFrameworkUpdate;
        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "record 开始采集，stop 停止采集，reset 重置状态；不带参数显示摘要。",
        });
    }

    public string Name => "副本标记前置";

    private void OnTerritoryChanged(uint territoryId)
    {
        var territory = checked((ushort)territoryId);
        state.Reset(territory);
        dutyState.Refresh(territory);
        Log.Information("进入区域 {TerritoryId}，副本状态已重置。", territoryId);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (!ClientState.IsLoggedIn)
            return;

        var territoryId = checked((ushort)ClientState.TerritoryType);
        if (state.TerritoryId != territoryId)
        {
            state.Reset(territoryId);
            dutyState.Refresh(territoryId);
        }

        partyState.Update(state);
        collector.Update();
        if (dutyState.CurrentDuty is { } currentDuty && Environment.TickCount64 >= nextSnapshotAt)
        {
            currentDuty.PartySnapshots.Add(partyState.Capture(state));
            if (currentDuty.PartySnapshots.Count > 1800)
                currentDuty.PartySnapshots.RemoveRange(0, currentDuty.PartySnapshots.Count - 1800);
            dataStore.Save();
            nextSnapshotAt = Environment.TickCount64 + 2000;
        }
        if (dutyState.CurrentDuty is { } duty && state.Players.Count > 0)
        {
            foreach (var status in state.Players.Values.SelectMany(x => x.Statuses))
                duty.ObservedStatusIds.Add(status.StatusId);
        }
    }

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            state.Reset(checked((ushort)ClientState.TerritoryType));
            ChatGui.Print("副本标记前置：状态已重置。");
            return;
        }

        if (args.Trim().Equals("record", StringComparison.OrdinalIgnoreCase))
        {
            collector.Start();
            ChatGui.Print($"副本采集器已开始：{collector.CurrentFile}");
            return;
        }
        if (args.Trim().Equals("stop", StringComparison.OrdinalIgnoreCase))
        {
            collector.Stop();
            ChatGui.Print("副本采集器已停止。");
            return;
        }

        if (dutyState.CurrentDuty is { } duty)
            dataStore.Save();
        ChatGui.Print($"副本标记前置：区域 {state.TerritoryId}，队伍成员 {state.Players.Count}，当前标记 {state.MarkedActors.Count}，已记录副本 {dataStore.Duties.Count} 个。" );
        foreach (var player in state.Players.Values)
            ChatGui.Print($"{player.Name} ({player.EntityId:X8}) 状态数：{player.Statuses.Count}");
    }

    public void Dispose()
    {
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update -= OnFrameworkUpdate;
        CommandManager.RemoveHandler(Command);
        collector.Dispose();
        dataStore.Save();
    }
}
