namespace DeepDungeon.Fsd.Core;

/// <summary>A queued visit keeps its batch even when the live scan changes.</summary>
public sealed class SupplyChestWorkState
{
    private readonly Dictionary<int, int> _batches = [];
    private int _nextBatch;
    public IReadOnlyCollection<int> Rooms => _batches.Keys;
    public int BatchFor(int room) => _batches.GetValueOrDefault(room);
    public void Update(IEnumerable<int> rooms)
    {
        var current = rooms.ToHashSet();
        foreach (int room in _batches.Keys.Where(r => !current.Contains(r)).ToArray()) _batches.Remove(room);
        foreach (int room in current)
            if (!_batches.ContainsKey(room)) _batches.Add(room, ++_nextBatch);
    }
    public RoomPlanEntry Bind(RoomPlanEntry entry) => entry with
    {
        ChestBatch = entry.ShouldSearchChests ? BatchFor(entry.RoomIndex) : 0
    };
}
