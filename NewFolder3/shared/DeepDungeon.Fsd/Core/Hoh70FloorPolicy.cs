using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>HoH-only additions; existing PT targeting and patrol policies remain authoritative.</summary>
public static class Hoh70FloorPolicy
{
    public const uint CentaurBaseId = 8995, CentaurNameId = 7584, MimicNameId = 7394;
    public static bool Enabled(FarmingMode? mode, uint dungeon, int floor) =>
        mode == FarmingMode.FreeExploration && dungeon == 2 && floor is > 0 and < 100 && floor % 10 != 0;
    public static bool IsCentaur(uint baseId, uint nameId) => baseId == CentaurBaseId || nameId == CentaurNameId;
    public static bool AllowsVoluntaryTarget(FarmingMode? mode, uint dungeon, int floor,
        uint baseId, uint nameId, bool engaged, int playerRoom, int targetRoom) =>
        !Enabled(mode, dungeon, floor) || engaged || !IsCentaur(baseId, nameId) &&
        (!HohHighFloorPolicy.Enabled(mode, dungeon, floor) || playerRoom >= 0 && targetRoom == playerRoom);
    public static int Priority(FarmingMode? mode, uint dungeon, int floor, uint baseId, uint nameId, int original) =>
        !Enabled(mode, dungeon, floor) ? original :
        nameId == MimicNameId ? 4 : IsCentaur(baseId, nameId) ? 3 : original;
    public static bool AllowsSerenity(int floor, int count, bool itemUseProhibited,
        bool harmfulEffect = false) => count > 0 &&
        (floor == 99 ? itemUseProhibited || harmfulEffect :
            floor is >= 91 and <= 98 ? itemUseProhibited : floor is > 0 and <= 90 && count > 2);
    public static bool AllowsFrailty(bool petrificationConfirmed, bool petrificationPending) =>
        !petrificationConfirmed && !petrificationPending;
}

/// <summary>Visit adjacent rooms, never navigate directly to a remote door-room patrol origin.</summary>
public sealed class HohRoomAdvanceState
{
    private readonly HashSet<int> _visited = [];
    private readonly Dictionary<(int From, int To), DateTime> _blockedUntil = [];
    public int CurrentRoom { get; private set; } = -1;
    private DateTime _enteredAt;
    public bool Enter(int room, DateTime now)
    {
        if (room < 0 || room == CurrentRoom) return false;
        CurrentRoom = room; _enteredAt = now; _visited.Add(room); return true;
    }
    public int Next(int room, IReadOnlyList<int> rooms, int[,] hops, DateTime now)
    {
        Enter(room, now);
        if (room < 0 || room >= hops.GetLength(0) || now - _enteredAt < TimeSpan.FromMilliseconds(800)) return -1;
        var valid = rooms.Where(r => r >= 0 && r < hops.GetLength(1)).ToArray();
        var remaining = valid.Where(r => !_visited.Contains(r) && hops[room, r] > 0 && hops[room, r] < 1000).ToArray();
        if (remaining.Length == 0)
        {
            _visited.Clear(); _visited.Add(room);
            remaining = valid.Where(r => r != room && hops[room, r] > 0 && hops[room, r] < 1000).ToArray();
        }
        if (remaining.Length == 0) return -1;
        foreach (int goal in remaining.OrderBy(r => hops[room, r]).ThenBy(r => r))
        {
            int next = valid.Where(r => hops[room, r] == 1 && (r == goal || hops[r, goal] == hops[room, goal] - 1) &&
                    (!_blockedUntil.TryGetValue((room, r), out var until) || now >= until))
                .OrderBy(r => _visited.Contains(r)).ThenBy(r => r).DefaultIfEmpty(-1).First();
            if (next >= 0) return next;
        }
        return -1;
    }
    public void Block(int from, int to, DateTime now) => _blockedUntil[(from, to)] = now.AddSeconds(10);
}

public readonly record struct HohMimicCandidate(ulong Id, Vector3 Position, bool Alive);

/// <summary>Disappearance alone never proves loot. Correlate only a newly spawned nearby mimic.</summary>
public sealed class HohMimicChestState
{
    private HashSet<ulong> _before = [];
    private DateTime _startedAt;
    private Vector3 _position;
    public ulong ChestId { get; private set; }
    public ulong MimicId { get; private set; }
    public void Begin(ulong chest, Vector3 position, DateTime now, IEnumerable<ulong> existingMimics)
    {
        if (chest == 0 || ChestId == chest) return;
        ChestId = chest; MimicId = 0; _position = position; _startedAt = now; _before = existingMimics.ToHashSet();
    }
    public bool Observe(DateTime now, bool chestMissing, IEnumerable<HohMimicCandidate> candidates)
    {
        if (ChestId == 0 || MimicId != 0 || !chestMissing || now < _startedAt || now - _startedAt > TimeSpan.FromSeconds(5)) return false;
        var match = candidates.Where(c => c.Id != 0 && c.Alive && !_before.Contains(c.Id) &&
            Vector3.DistanceSquared(c.Position, _position) <= 3 * 3).ToArray();
        if (match.Length != 1) return false;
        MimicId = match[0].Id; return true;
    }
}
