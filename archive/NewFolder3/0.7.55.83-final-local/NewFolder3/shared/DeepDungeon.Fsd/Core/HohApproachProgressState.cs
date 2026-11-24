namespace DeepDungeon.Fsd.Core;

/// <summary>Rejected idle-target plans hand off to patrol; changing targets is not progress.</summary>
public sealed class HohApproachProgressState
{
    public static readonly TimeSpan NoProgressLimit = TimeSpan.FromSeconds(60);
    private uint _dungeon;
    private int _floor = -1, _deferredRoom = -1;
    private bool _enabled, _deferred;
    private DateTime? _lastTick;
    private readonly HashSet<int> _seenRooms = [];
    public TimeSpan ActiveNoProgress { get; private set; }
    public bool Exhausted => _enabled && _deferred && ActiveNoProgress >= NoProgressLimit;

    public void BeginFloor(FarmingMode? mode, uint dungeon, int floor)
    {
        bool enabled = Hoh70FloorPolicy.Enabled(mode, dungeon, floor);
        if (_enabled != enabled || _dungeon != dungeon || _floor != floor) Reset();
        _enabled = enabled; _dungeon = dungeon; _floor = floor;
    }

    public void Tick(int room, DateTime now)
    {
        if (!_enabled) return;
        // A paused host contributes no time. Repeated ticks/target changes cannot renew the limit.
        if (_deferred && _lastTick is { } last && now >= last && now - last <= TimeSpan.FromSeconds(1))
            ActiveNoProgress += now - last;
        _lastTick = now;
        if (room < 0) return;
        bool newRoom = _seenRooms.Add(room);
        if (_deferredRoom >= 0 && room != _deferredRoom) _deferredRoom = -1;
        if (newRoom) ActiveNoProgress = TimeSpan.Zero;
    }

    public void Defer(int room, DateTime now)
    {
        if (!_enabled) return;
        _deferred = true; _deferredRoom = room;
        if (room >= 0) _seenRooms.Add(room);
        _lastTick ??= now;
    }

    public bool RequiresPatrol(int room) => _enabled && _deferred &&
        (_deferredRoom < 0 ? room < 0 : room == _deferredRoom);

    public void ObserveOutput() => Reset();
    private void Reset()
    {
        _deferred = false; _deferredRoom = -1; _lastTick = null;
        ActiveNoProgress = TimeSpan.Zero; _seenRooms.Clear();
    }
}
