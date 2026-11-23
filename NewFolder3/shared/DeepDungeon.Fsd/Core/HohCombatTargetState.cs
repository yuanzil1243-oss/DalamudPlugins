namespace DeepDungeon.Fsd.Core;

public readonly record struct HohCombatCandidate(ulong Id, bool Alive, bool Targetable, bool Engaged,
    bool Allowed, bool NewPullAllowed, int StoneskinPriority, uint Hp, float DistanceSquared);
public readonly record struct HohNativeHit(long Revision, DateTime At, uint ActionId, uint Sequence, int Damage);

/// <summary>Normal HoH floors only. Selection is not evidence that a monster has engaged.</summary>
public sealed class HohCombatTargetState
{
    public static readonly TimeSpan SelectionGrace = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan SelectionTimeout = TimeSpan.FromSeconds(6);
    private readonly HashSet<ulong> _engaged = new();
    private readonly Dictionary<ulong, HohNativeHit> _hits = new();
    private uint _dungeon;
    private int _floor;
    private DateTime? _outOfCombatSince, _selectionSince, _selectedSince;
    private ulong _selectionTarget;
    private long _revision;
    public bool Enabled { get; private set; }
    public ulong ChosenId { get; private set; }
    public bool HasEngagement => _engaged.Count > 0;
    public int EngagedCount => _engaged.Count;
    public bool SelectionTimedOut { get; private set; }

    public void BeginFloor(FarmingMode? mode, uint dungeon, int floor)
    {
        bool enabled = mode == FarmingMode.FreeExploration && dungeon == 2 && floor is > 0 and < 100 && floor % 10 != 0;
        if (Enabled != enabled || _dungeon != dungeon || _floor != floor) Reset();
        Enabled = enabled; _dungeon = dungeon; _floor = floor;
    }

    public void Observe(IEnumerable<HohCombatCandidate> candidates, bool inCombat, DateTime now)
    {
        if (!Enabled) return;
        bool observedEngagement = false;
        foreach (var c in candidates)
        {
            if (c.Id == 0) continue;
            if (!c.Alive) { _engaged.Remove(c.Id); _hits.Remove(c.Id); if (ChosenId == c.Id) ChosenId = 0; }
            else if (c.Engaged) { _engaged.Add(c.Id); observedEngagement = true; }
        }
        // One delayed hate-list/target update must not erase a living old opponent.
        if (inCombat || observedEngagement) _outOfCombatSince = null;
        else
        {
            _outOfCombatSince ??= now;
            if (now - _outOfCombatSince.Value >= TimeSpan.FromSeconds(2)) _engaged.Clear();
        }
    }

    public bool IsEngaged(ulong id) => Enabled && id != 0 && _engaged.Contains(id);

    public ulong Select(IReadOnlyList<HohCombatCandidate> candidates, ulong preferred, bool allowNewTargets,
        ulong rotationTarget = 0)
    {
        if (!Enabled) return 0;
        var live = candidates.Where(c => c.Id != 0 && c.Alive && c.Targetable && c.Allowed).ToArray();
        var engaged = live.Where(c => c.Engaged || IsEngaged(c.Id)).ToArray();
        // A validated shared output decision also owns pursuit. Retain all old hate separately.
        // Engaged special enemies always preempt ordinary switching / gathering.
        int priority = engaged.Select(c => c.StoneskinPriority).DefaultIfEmpty(0).Max();
        var output = live.FirstOrDefault(c => c.Id == rotationTarget &&
            (c.Engaged || IsEngaged(c.Id) || allowNewTargets && c.NewPullAllowed));
        if (output.Id != 0 && output.StoneskinPriority >= priority)
        { ChosenId = output.Id; return ChosenId; }
        if (engaged.Length > 0)
        {
            ChosenId = engaged.OrderByDescending(c => c.StoneskinPriority)
                .ThenByDescending(c => c.Id == preferred).ThenByDescending(c => c.Id == ChosenId)
                .ThenByDescending(c => c.Hp).ThenBy(c => c.DistanceSquared).ThenBy(c => c.Id).First().Id;
            return ChosenId;
        }
        if (HasEngagement) return 0; // Reacquire missing old hate; never replace it with an idle monster.
        if (!allowNewTargets) { ChosenId = 0; return 0; }
        var next = live.FirstOrDefault(c => c.Id == preferred && c.NewPullAllowed);
        if (next.Id == 0) next = live.FirstOrDefault(c => c.Id == ChosenId && c.NewPullAllowed);
        if (next.Id == 0) next = live.Where(c => c.NewPullAllowed)
            .OrderBy(c => c.DistanceSquared).ThenBy(c => c.Id).FirstOrDefault();
        ChosenId = next.Id;
        return ChosenId;
    }

    public bool ObserveSelection(DateTime now, ulong desired, ulong hard, ulong soft)
    {
        if (!Enabled || desired == 0) { ResetSelection(); return false; }
        if (_selectionTarget != desired || _selectionSince is { } since && now < since)
        { ResetSelection(); _selectionTarget = desired; _selectionSince = now; }
        if (hard == desired && (soft == 0 || soft == desired))
        {
            _selectedSince ??= now;
            _selectionSince = now; SelectionTimedOut = false;
            return now - _selectedSince.Value >= SelectionGrace;
        }
        _selectedSince = null;
        SelectionTimedOut = now - _selectionSince!.Value >= SelectionTimeout;
        return false;
    }

    public void ObserveHit(ulong target, DateTime now, uint action, uint sequence, int damage)
    {
        if (!Enabled || target == 0 || action == 0 || damage < 0) return;
        if (_hits.TryGetValue(target, out var previous) && previous.ActionId == action && previous.Sequence == sequence) return;
        _hits[target] = new(++_revision, now, action, sequence, damage);
        _engaged.Add(target); // A native direct hit, including fully absorbed damage, is positive evidence.
    }
    public bool TryGetHit(ulong target, out HohNativeHit hit) => _hits.TryGetValue(target, out hit);
    public bool ConsumeNewHit(ulong target, ref long revision, DateTime now)
    {
        if (!Enabled || !TryGetHit(target, out var hit) || hit.Revision <= revision) return false;
        revision = hit.Revision;
        return now >= hit.At && now - hit.At <= TimeSpan.FromSeconds(2);
    }
    public static bool IsDirectPlayerDamage(bool scoped, uint player, uint source, byte actionType, byte effectType, byte flags) =>
        scoped && player != 0 && source == player && actionType == 1 && effectType is 3 or 5 or 6 && (flags & 0x80) == 0;
    public static bool HasAttackOpportunity(bool selected, bool suppressed, bool inActionRange,
        bool casting, ulong castTarget, ulong desired) =>
        desired != 0 && selected && !suppressed && inActionRange && (!casting || castTarget == desired);
    public void ResetSelection()
    { _selectionTarget = 0; _selectionSince = _selectedSince = null; SelectionTimedOut = false; }
    public void Reset()
    {
        _engaged.Clear(); _hits.Clear(); _outOfCombatSince = null; ChosenId = 0;
        ResetSelection(); Enabled = false;
    }
}
