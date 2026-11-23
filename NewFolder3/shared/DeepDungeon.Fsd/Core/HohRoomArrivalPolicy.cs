using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public static class HohRoomArrivalPolicy
{
    public static bool CanBeginSearch(uint dungeonId, bool arrived, int requestedRoom, int? currentTarget,
        Vector3 player, Vector3 destination) => dungeonId == 2 && arrived && currentTarget == requestedRoom &&
        float.IsFinite(player.X) && float.IsFinite(player.Y) && float.IsFinite(player.Z) &&
        float.IsFinite(destination.X) && float.IsFinite(destination.Y) && float.IsFinite(destination.Z) &&
        Vector3.DistanceSquared(player, destination) <= 1.2f * 1.2f;
    public static bool CanContinueSearch(uint dungeonId, int? worldArrivedRoom, int requestedRoom,
        bool insideExistingSearchArea, float heightDifference) => dungeonId == 2 && worldArrivedRoom == requestedRoom &&
        insideExistingSearchArea && float.IsFinite(heightDifference) && Math.Abs(heightDifference) <= 4;
}

/// <summary>Measures continuous navigation only; combat pauses and zoning do not consume this budget.</summary>
public sealed class HohRoomProgressWatch
{
    private int? _room;
    private Vector3 _anchor;
    private DateTime _progressAt, _lastTick;
    public bool Stalled(int room, Vector3 position, DateTime now)
    {
        if (_room != room || now - _lastTick > TimeSpan.FromSeconds(3) || now < _lastTick ||
            Vector3.DistanceSquared(position, _anchor) >= 1.5f * 1.5f)
        { _room = room; _anchor = position; _progressAt = now; }
        _lastTick = now;
        if (now - _progressAt < TimeSpan.FromSeconds(20)) return false;
        _progressAt = now;
        return true;
    }
    public void Reset() => _room = null;
}
