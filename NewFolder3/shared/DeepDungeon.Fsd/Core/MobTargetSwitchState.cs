namespace DeepDungeon.Fsd.Core;

public readonly record struct OutputTargetCandidate(ulong Id, uint Hp, float Distance, int Priority, bool Engaged, bool CanHit);

/// <summary>One selection for output and chase; no new pull while a priority fight or consolidation owns the floor.</summary>
public sealed class MobTargetSwitchState
{
    private ulong _target;
    private uint _hp;
    private DateTime _progressAt, _unavailableAt;
    private readonly Dictionary<ulong, DateTime> _deferred = [];
    public string Reason { get; private set; } = "none";
    public ulong Select(IReadOnlyList<OutputTargetCandidate> candidates, bool allowNew, DateTime now)
    {
        var current = candidates.FirstOrDefault(c => c.Id == _target);
        if (current.Id != 0 && current.Hp < _hp) _progressAt = now;
        bool stalled = _target != 0 && now - _progressAt >= TimeSpan.FromSeconds(5);
        var engaged = candidates.Where(c => c.Engaged).ToArray();
        int priority = engaged.Select(c => c.Priority).DefaultIfEmpty(0).Max();
        bool Available(OutputTargetCandidate c) => !_deferred.TryGetValue(c.Id, out var until) || now >= until;
        var hittable = engaged.Where(c => c.Priority == priority && c.CanHit && Available(c) && (!stalled || c.Id != _target))
            .OrderByDescending(c => priority == 0 ? (long)c.Hp : -(long)c.Hp).ThenBy(c => c.Distance).ToArray();
        OutputTargetCandidate chosen = default;
        if (current.Id != 0 && current.Engaged && current.Priority == priority && current.CanHit && !stalled && Available(current))
        { chosen = current; Reason = "current-hittable"; }
        else if (hittable.Length > 0)
        { chosen = hittable[0]; Reason = "switch-to-hittable-engaged"; }
        if (chosen.Id == 0)
        {
            if (_unavailableAt == default) _unavailableAt = now;
            bool ready = now - _unavailableAt >= TimeSpan.FromSeconds(1.5) || stalled;
            if (allowNew && priority == 0 && ready)
            {
                // Hold one pending pull long enough for the external rotation to act.
                if (current.Id != 0 && !current.Engaged && current.CanHit && !stalled && Available(current)) chosen = current;
                else chosen = candidates.Where(c => !c.Engaged && c.CanHit && Available(c))
                    .OrderBy(c => c.Distance).ThenBy(c => c.Id).FirstOrDefault();
                if (chosen.Id != 0) Reason = "bounded-new-target";
            }
            if (chosen.Id == 0)
            {
                chosen = engaged.Where(c => c.Priority == priority).OrderByDescending(c => Available(c))
                    .ThenBy(c => c.Distance).FirstOrDefault();
                Reason = chosen.Id == 0 ? "no-target" : "approach-existing-target";
            }
        }
        else _unavailableAt = default;
        if (chosen.Id != _target)
        {
            if (_target != 0 && (!current.CanHit || stalled)) _deferred[_target] = now.AddSeconds(5);
            _target = chosen.Id; _progressAt = now;
        }
        _hp = chosen.Hp;
        foreach (var id in _deferred.Where(p => now >= p.Value).Select(p => p.Key).ToArray()) _deferred.Remove(id);
        return _target;
    }
    public void Reset()
    {
        _target = 0; _hp = 0; _progressAt = _unavailableAt = default; _deferred.Clear(); Reason = "reset";
    }
}
