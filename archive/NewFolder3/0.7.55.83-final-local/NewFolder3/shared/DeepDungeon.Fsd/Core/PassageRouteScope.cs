namespace DeepDungeon.Fsd.Core;

/// <summary>One forward-only room path; replanning cannot re-add branches or rooms already passed.</summary>
public sealed class PassageRouteScope
{
    private int[] _rooms = [];
    private int _first;
    public bool Ready => _rooms.Length > 0;
    public IReadOnlyList<int> Rooms => _rooms;
    public bool Initialize(int start, int passage, int[,] distances)
    {
        if (Ready) return true;
        int n = distances.GetLength(0);
        if (start < 0 || passage < 0 || start >= n || passage >= n || distances[start, passage] < 0) return false;
        var path = new List<int> { start };
        int current = start;
        while (current != passage && path.Count <= n)
        {
            int next = Enumerable.Range(0, n).FirstOrDefault(i => distances[current, i] == 1 &&
                distances[i, passage] == distances[current, passage] - 1, -1);
            if (next < 0) return false;
            path.Add(next); current = next;
        }
        if (current != passage) return false;
        _rooms = path.ToArray(); return true;
    }
    public void ObserveRoom(int room)
    {
        int index = Array.IndexOf(_rooms, room);
        if (index >= _first) _first = index;
    }
    public bool Allows(int room) => !Ready || Array.IndexOf(_rooms, room) >= _first;
    public IReadOnlyList<RoomPlanEntry> Filter(IReadOnlyList<RoomPlanEntry> route) => !Ready ? route :
        route.Where(e => Allows(e.RoomIndex)).OrderBy(e => Array.IndexOf(_rooms, e.RoomIndex)).ToArray();
}
