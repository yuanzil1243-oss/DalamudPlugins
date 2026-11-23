using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct HohPassageCandidate(ulong Id, Vector3 Position, bool InPassageRoom);

/// <summary>Per-floor candidates. A beacon model and arrival are not proof of a working passage.</summary>
public sealed class HohPassageCandidates
{
    // This run's confirmed real passages took7.2–7.4 seconds after standing still.
    // Shorten the former12-second failure window without rejecting real activation.
    public const double ActivationWaitSeconds = 9;
    private readonly List<Vector3> _rejected = [];
    private HohPassageCandidate? _selected;
    private DateTime? _lastTick;
    private double _atBeaconSeconds;
    public int RejectedCount => _rejected.Count;
    public HohPassageCandidate? Current => _selected;
    public bool TrySelect(IReadOnlyList<HohPassageCandidate> candidates, Vector3 player, DateTime now,
        out HohPassageCandidate selected, out bool changed, out bool expired, bool activationEligible = true)
    {
        if (!activationEligible) { Suspend(); selected = _selected ?? default; changed = expired = false; return _selected.HasValue; }
        var previous = _selected;
        expired = false;
        double elapsed = _lastTick is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 1) : 0;
        _lastTick = now;
        if (_selected is { } active)
        {
            bool near = Vector2.DistanceSquared(new(player.X, player.Z), new(active.Position.X, active.Position.Z)) <= 1.25f * 1.25f &&
                Math.Abs(player.Y - active.Position.Y) <= 3;
            // Brief recentering does not refill the activation wait budget.
            if (near) _atBeaconSeconds += elapsed;
            if (_atBeaconSeconds + 1e-9 >= ActivationWaitSeconds) { RejectCurrent(); expired = true; }
            else if (!candidates.Any(c => Same(c, active))) { _selected = null; _atBeaconSeconds = 0; }
        }
        if (_selected == null && _rejected.Count < 16)
        {
            foreach (var c in candidates.Where(c => CombatPositionPolicy.IsFinite(c.Position) && !Rejected(c.Position))
                .OrderByDescending(c => c.InPassageRoom).ThenBy(c => Vector3.DistanceSquared(c.Position, player)))
            { _selected = c; _atBeaconSeconds = 0; break; }
        }
        changed = previous.HasValue != _selected.HasValue || previous is { } a && _selected is { } b && !Same(a, b);
        selected = _selected ?? default;
        return _selected.HasValue;
    }
    public void RejectCurrent()
    {
        if (_selected is { } c && !Rejected(c.Position)) _rejected.Add(c.Position);
        _selected = null; _atBeaconSeconds = 0;
    }
    public void Suspend() => _lastTick = null;
    public void Clear() { _selected = null; _rejected.Clear(); _atBeaconSeconds = 0; _lastTick = null; }
    private bool Rejected(Vector3 position) => _rejected.Any(p => Vector3.DistanceSquared(p, position) <= 1);
    private static bool Same(HohPassageCandidate a, HohPassageCandidate b) =>
        a.Id == b.Id && Vector3.DistanceSquared(a.Position, b.Position) <= 1;
}
