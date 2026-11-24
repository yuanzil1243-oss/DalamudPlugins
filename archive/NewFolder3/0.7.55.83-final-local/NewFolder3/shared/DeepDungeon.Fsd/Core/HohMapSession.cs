using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>HoH physical layouts are not identified by ActiveLayoutIndex alone.</summary>
public sealed class HohMapSession
{
    private int _floor = -1, _layout = -1;
    private Vector3?[]? _centers;
    public void Enter(int floor, int layout)
    {
        if (_floor == floor && _layout == layout) return;
        Clear(); _floor = floor; _layout = layout;
    }
    public void Clear() { _floor = _layout = -1; _centers = null; }
    public bool Set(IReadOnlyList<Vector3?> centers, Vector3 player)
    {
        if (centers.Count != 25 || !IsLocal(centers, player)) return false;
        _centers = centers.ToArray(); return true;
    }
    public bool Available(Vector3 player) => _centers != null && IsLocal(_centers, player);
    public bool TryGet(int room, Vector3 player, out Vector3 center)
    {
        center = default;
        if (room is < 0 or >= 25 || !Available(player) || _centers![room] is not { } p) return false;
        center = p; return true;
    }
    public static bool IsLocal(IReadOnlyList<Vector3?> centers, Vector3 player) =>
        CombatPositionPolicy.IsFinite(player) && centers.Any(c => c is { } p &&
            CombatPositionPolicy.IsFinite(p) && Vector2.DistanceSquared(new(p.X, p.Z), new(player.X, player.Z)) <= 120 * 120 &&
            Math.Abs(p.Y - player.Y) <= 20) && centers.All(c => c == null ||
                CombatPositionPolicy.IsFinite(c.Value) && Math.Abs(c.Value.Y - player.Y) <= 20 &&
                Vector2.DistanceSquared(new(c.Value.X, c.Value.Z), new(player.X, player.Z)) <= 450 * 450);
    public static bool IsLargeRoomMap(IEnumerable<int> rooms) => rooms.Order().SequenceEqual(
        new[] { 5, 6, 7, 8, 10, 11, 12, 13, 15, 16, 17, 18 });
}
