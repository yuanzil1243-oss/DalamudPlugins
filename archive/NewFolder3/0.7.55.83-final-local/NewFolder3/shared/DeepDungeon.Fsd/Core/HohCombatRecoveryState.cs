using System.Numerics;

namespace DeepDungeon.Fsd.Core;

/// <summary>HoH-only evidence and obstruction memory. Ground/path safety remains with the existing navigator.</summary>
public sealed class HohCombatRecoveryState
{
    public static readonly TimeSpan BlockedDelay = TimeSpan.FromMilliseconds(800);
    public static readonly TimeSpan NoOutputDelay = TimeSpan.FromSeconds(4);
    public const float MaximumSearchDistance = 24f;
    public const float PlatformMargin = 1.4f;
    private static readonly TimeSpan MaximumEvidenceGap = TimeSpan.FromSeconds(1);
    private readonly List<Vector3> _failedAreas = new();
    private DateTime? _blockedSince, _eligibleSince, _lastSample, _verifyBlockedSince, _lastVerifySample;
    public Vector3? Origin { get; private set; }
    public int FailedPositions { get; private set; }
    public int SafeExits { get; private set; }
    public bool CanUseSafeExit => SafeExits < 4;
    public float MinimumOriginDistance => FailedPositions switch { 0 => 1, 1 => 4, 2 => 8, 3 => 12, _ => 16 };

    public bool ShouldStart(DateTime now, bool inRange, bool attackOpportunity, bool blocked)
    {
        if (!inRange || !attackOpportunity) { SuspendEvidence(); return false; }
        if (_lastSample is { } last && (now < last || now - last > MaximumEvidenceGap)) SuspendEvidence();
        _lastSample = now;
        _eligibleSince ??= now;
        if (blocked) _blockedSince ??= now;
        else _blockedSince = null;
        return _blockedSince is { } since && now - since >= BlockedDelay || now - _eligibleSince.Value >= NoOutputDelay;
    }

    public bool StillBlockedAtArrival(DateTime now, bool blocked, bool casting)
    {
        if (casting || !blocked) { _verifyBlockedSince = _lastVerifySample = null; return false; }
        if (_lastVerifySample is { } last && (now < last || now - last > MaximumEvidenceGap)) _verifyBlockedSince = null;
        _lastVerifySample = now;
        _verifyBlockedSince ??= now;
        return now - _verifyBlockedSince.Value >= BlockedDelay;
    }

    public void BeginAttempt(Vector3 player) => Origin ??= player;

    public bool AllowsCandidate(Vector3 player, Vector3 point, bool safeExit)
    {
        if (!CombatPositionPolicy.IsFinite(player) || !CombatPositionPolicy.IsFinite(point) ||
            !CombatPositionPolicy.HasRealHorizontalMove(player, point)) return false;
        var origin = Origin ?? player;
        float distance = HorizontalDistanceSquared(origin, point);
        float minimum = safeExit ? Math.Max(4, MinimumOriginDistance) : MinimumOriginDistance;
        return distance >= minimum * minimum && distance <= MaximumSearchDistance * MaximumSearchDistance &&
            !_failedAreas.Any(failed => HorizontalDistanceSquared(failed, point) < 4);
    }

    // Expansion is a search preference, not proof that a closer, fully verified firing point is bad.
    // Only the controller may use this after the original firing-margin checks pass.
    public bool AllowsFiringFallback(Vector3 player, Vector3 point) =>
        FailedPositions > 0 && CombatPositionPolicy.IsFinite(player) && CombatPositionPolicy.IsFinite(point) &&
        CombatPositionPolicy.HasRealHorizontalMove(player, point) &&
        HorizontalDistanceSquared(Origin ?? player, point) <= MaximumSearchDistance * MaximumSearchDistance &&
        !_failedAreas.Any(failed => HorizontalDistanceSquared(failed, point) < 4);

    public int CompareCandidates(Vector3 player, Vector3 a, bool aExit, bool aWide, Vector3 b, bool bExit, bool bWide)
    {
        int output = aExit.CompareTo(bExit);
        if (output != 0) return output;
        if (FailedPositions > 0 || aExit)
        {
            int platform = bWide.CompareTo(aWide);
            if (platform != 0) return platform;
        }
        return HorizontalDistanceSquared(player, a).CompareTo(HorizontalDistanceSquared(player, b));
    }

    public void RejectPosition(Vector3 point)
    {
        FailedPositions++;
        if (CombatPositionPolicy.IsFinite(point) && !_failedAreas.Any(p => HorizontalDistanceSquared(p, point) < 1))
            _failedAreas.Add(point);
    }

    public void CompleteSafeExit(Vector3 point) { SafeExits++; RejectPosition(point); }

    public void SuspendEvidence()
    {
        _blockedSince = _eligibleSince = _lastSample = _verifyBlockedSince = _lastVerifySample = null;
    }

    public void ObserveOutput()
    {
        SuspendEvidence(); Origin = null; FailedPositions = SafeExits = 0; _failedAreas.Clear();
    }

    private static float HorizontalDistanceSquared(Vector3 a, Vector3 b) =>
        Vector2.DistanceSquared(new(a.X, a.Z), new(b.X, b.Z));
}
