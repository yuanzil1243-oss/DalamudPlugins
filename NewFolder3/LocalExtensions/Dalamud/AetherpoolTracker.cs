using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace DeepDungeon.Fsd.Dalamud.GameState;

internal sealed class AetherpoolTracker : IDisposable
{
    private readonly NativeDeepDungeonLogMessageSource _source;
    private readonly AetherpoolStateStore _store = new();
    private bool _disposed;
    internal AetherpoolTracker(NativeDeepDungeonLogMessageSource source)
    { _source = source; _source.MessageReceived += OnMessage; }
    internal bool TryGet(uint dungeon, out AetherpoolReading reading) =>
        _store.TryGet(Service.PlayerState.ContentId, dungeon, DateTime.UtcNow, out reading);
    internal bool RecordConfirmedCraft(uint dungeon) =>
        _store.RecordConfirmedCraft(Service.PlayerState.ContentId, dungeon, DateTime.UtcNow);
    internal unsafe void Update(bool inDeepDungeon)
    {
        if (_disposed) return;
        ulong character = Service.PlayerState.ContentId;
        _store.SetCharacter(character);
        if (character == 0 || !inDeepDungeon || !Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]) return;
        var ef = EventFramework.Instance();
        var dd = ef != null ? ef->GetInstanceContentDeepDungeon() : null;
        if (dd == null || dd->DeepDungeonId == 0 || dd->WeaponLevel == 0 && dd->ArmorLevel == 0) return;
        _store.Observe(character, dd->DeepDungeonId, dd->WeaponLevel, dd->ArmorLevel,
            AetherpoolSource.InDutyMemory, DateTime.UtcNow);
    }
    private void OnMessage(NativeDeepDungeonLogMessage message)
    {
        if (_disposed || message.Id is not (7250 or 7252 or 7253) || message.ParameterCount < 2 ||
            message.Value1 is < 1 or > 8 || message.Value2 is < 1 or > 99) return;
        uint dungeon = (message.Value1 + 1) / 2;
        bool weapon = message.Value1 % 2 == 1;
        _store.Observe(Service.PlayerState.ContentId, dungeon,
            weapon ? (int)message.Value2 : null, weapon ? null : (int)message.Value2,
            AetherpoolSource.LogMessage, DateTime.UtcNow);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _source.MessageReceived -= OnMessage; _store.SetCharacter(0);
    }
}
