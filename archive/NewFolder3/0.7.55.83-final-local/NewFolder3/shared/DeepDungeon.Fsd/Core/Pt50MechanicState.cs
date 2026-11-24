using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public sealed class Pt50MechanicState
{
    public bool SandActive { get; private set; }
    public bool ReducedArena { get; private set; }
    public bool ChaseActive => RemainingHits > 0;
    public bool FirstHit { get; private set; }
    public int RemainingHits { get; private set; }
    public Vector2 Circle { get; private set; }
    public DateTime NextHitAt { get; private set; }
    public DateTime WindAt { get; private set; }
    public DateTime WindResolvedAt { get; private set; }
    public int Revision { get; private set; }
    private readonly HashSet<(uint, uint)> _events = [];

    public void ObserveCast(uint action, Vector2 center, double remaining, DateTime now)
    {
        if (action == 43531) SandActive = true;
        if (action is 43537 or 43538) ReducedArena = true;
        if (action == 43534 && !ChaseActive)
        {
            SandActive = true;
            FirstHit = true;
            RemainingHits = 4;
            Circle = center;
            NextHitAt = now.AddSeconds(Math.Clamp(remaining, 0, 3));
            ++Revision;
        }
        if (action == 43538)
        {
            SandActive = true;
            WindAt = now.AddSeconds(Math.Clamp(remaining, 0, 8));
        }
    }

    public bool ObserveAction(uint action, uint sequence, Vector2 center, DateTime now)
    {
        if (!_events.Add((action, sequence))) return false;
        if (action == 43531) SandActive = true;
        if (action is 43537 or 43538) ReducedArena = true;
        if (action is 43534 or 43535 or 43536)
        {
            // Refuse to invent a mid-sequence chase without the first cast/first hit.
            if (!ChaseActive && action != 43534) return false;
            if (!ChaseActive) RemainingHits = 4;
            SandActive = true;
            FirstHit = false;
            Circle = center;
            RemainingHits = action == 43536 ? 0 : Math.Max(0, RemainingHits - 1);
            NextHitAt = now.AddSeconds(Pt50IslandRoutePlanner.Interval);
            ++Revision;
        }
        if (action == 43538)
        {
            WindAt = default;
            WindResolvedAt = now;
            ++Revision;
        }
        return true;
    }

    public bool IsStale(DateTime now) => ChaseActive && now > NextHitAt.AddSeconds(2);

    public void Reset()
    {
        SandActive = FirstHit = ReducedArena = false;
        RemainingHits = 0;
        Circle = default;
        NextHitAt = WindAt = WindResolvedAt = default;
        _events.Clear();
        ++Revision;
    }
}
