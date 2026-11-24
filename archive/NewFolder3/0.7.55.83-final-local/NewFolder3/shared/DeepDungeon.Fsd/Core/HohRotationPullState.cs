namespace DeepDungeon.Fsd.Core;

/// <summary>One rotation-owned pull at a time. Selection is never engagement evidence.</summary>
public sealed class HohRotationPullState
{
    private readonly Dictionary<ulong, DateTime> _deferred = [];
    private DateTime _started, _readySince, _next;
    public ulong Pending { get; private set; }
    public ulong DeferredTarget { get; private set; }
    public string Reason { get; private set; } = "idle";

    public ulong Select(IReadOnlyList<OutputTargetCandidate> candidates, bool allowed, bool mayStart,
        ulong selected, DateTime now)
    {
        DeferredTarget = 0;
        foreach (var id in _deferred.Where(p => now >= p.Value).Select(p => p.Key).ToArray()) _deferred.Remove(id);
        if (!allowed) { Pending = 0; _readySince = default; Reason = "safety-hold"; return 0; }
        if (Pending != 0)
        {
            var current = candidates.FirstOrDefault(c => c.Id == Pending);
            if (current.Id == 0 || current.Engaged)
            {
                Reason = current.Engaged ? "engagement-confirmed" : "candidate-unavailable";
                Pending = 0; _readySince = default; _next = now.AddMilliseconds(350); return 0;
            }
            // Start the output timeout only after actual selection at a usable firing position.
            if (current.CanHit && selected == Pending && _readySince == default) _readySince = now;
            if (now - _started >= TimeSpan.FromSeconds(24) ||
                _readySince != default && now - _readySince >= TimeSpan.FromSeconds(6))
            {
                DeferredTarget = Pending; _deferred[Pending] = now.AddSeconds(20);
                Pending = 0; _readySince = default; _next = now.AddSeconds(1);
                Reason = "rotation-pull-timeout"; return 0;
            }
            Reason = current.CanHit ? "await-rotation-engagement" : "navigate-to-output";
            return Pending;
        }
        if (!mayStart || now < _next) return 0;
        var next = candidates.Where(c => !c.Engaged && c.Priority == 0 && !_deferred.ContainsKey(c.Id))
            .OrderBy(c => c.Distance).ThenBy(c => c.Id).FirstOrDefault();
        if (next.Id == 0) { Reason = "no-safe-candidate"; return 0; }
        Pending = next.Id; _started = now; _readySince = default;
        Reason = next.CanHit ? "await-rotation-engagement" : "navigate-to-output";
        return Pending;
    }
    public void Reset()
    { Pending = DeferredTarget = 0; _started = _readySince = _next = default; _deferred.Clear(); Reason = "idle"; }
}
