using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public readonly record struct TriggerScene(ulong ActorId, long ActorAddress, uint Territory, uint Dungeon, int Floor);

/// <summary>The native motion ABI has no event sequence. Identical requests in one 250ms burst count once.</summary>
public sealed class DisplacementTriggerTracker(FeatureTriggerCounters counters)
{
    private readonly object sync = new();
    private (TriggerScene Scene, float Angle, float Distance, float Duration, byte Mode, ulong Flags)? previous;
    private long lastAt;
    public void Suppressed(TriggerScene scene, float angle, float distance, float duration, byte mode, ulong flags, long now)
    {
        var key = (scene, angle, distance, duration, mode, flags);
        lock (sync)
        {
            bool duplicate = previous == key && now >= lastAt && now - lastAt <= 250;
            previous = key; lastAt = now;
            if (!duplicate) counters.Record(FeatureTriggerKind.AntiDisplacement);
        }
    }
    public void Reset() { lock (sync) previous = null; }
}

/// <summary>Counts actual horizontal movement after a real denied-to-allowed callback, once per restriction episode.</summary>
public sealed class ForcedMovementTriggerTracker(FeatureTriggerCounters counters)
{
    private readonly object sync = new();
    private TriggerScene scene;
    private bool initialized, active, counted;
    private readonly HashSet<uint> denied = [];
    private Vector3 origin;
    private long lastRelease, clearSince = -1;

    public void Permission(TriggerScene identity, Vector3 position, uint id, byte original, byte returned, long now)
    {
        lock (sync)
        {
            EnsureScene(identity);
            Recover(now);
            if (original == 0 && returned != 0)
            {
                if (!active) { active = true; counted = false; origin = position; }
                denied.Add(id); lastRelease = now; clearSince = -1;
            }
            else if (original != 0 && active)
            {
                denied.Remove(id);
                if (denied.Count == 0 && clearSince < 0) clearSince = now;
            }
        }
    }
    public void Position(TriggerScene identity, Vector3 position, long now)
    {
        lock (sync)
        {
            EnsureScene(identity); Recover(now);
            if (!active || counted || now < lastRelease || now - lastRelease > 500) return;
            float dx = position.X - origin.X, dz = position.Z - origin.Z;
            float square = dx * dx + dz * dz;
            if (!float.IsFinite(square) || !float.IsFinite(position.Y) || !float.IsFinite(origin.Y) ||
                Math.Abs(position.Y - origin.Y) > .5f || square > 9f)
            { ResetCore(); return; } // A teleport/large correction is not normal input movement.
            if (square >= .08f * .08f)
            { counted = true; counters.Record(FeatureTriggerKind.ForcedMovement); }
        }
    }
    private void EnsureScene(TriggerScene identity)
    {
        if (!initialized || scene != identity) { ResetCore(); scene = identity; initialized = true; }
    }
    private void Recover(long now)
    {
        if (active && denied.Count == 0 && clearSince >= 0 && now >= clearSince && now - clearSince >= 500)
        { active = counted = false; clearSince = -1; }
    }
    private void ResetCore() { initialized = active = counted = false; denied.Clear(); clearSince = -1; }
    public void Reset() { lock (sync) ResetCore(); }
}

/// <summary>Only a matching server action-effect receipt can confirm this watchdog's direct request.</summary>
public sealed class GcdTriggerConfirmation(FeatureTriggerCounters counters)
{
    private sealed record Pending(long At, ulong Actor, uint Action, ulong Target, ushort Sequence);
    private readonly object sync = new();
    private Pending? pending;
    public void Arm(long now, ulong actor, uint action, ulong target, ushort sequence)
    { lock (sync) pending = actor != 0 && action != 0 && target != 0 ? new(now, actor, action, target, sequence) : null; }
    public bool ArmAccepted(long now, bool accepted, bool queued, ushort before, ushort after,
        ulong actor, uint action, ulong target)
    {
        if (!accepted || queued || before == after) { Reset(); return false; }
        Arm(now, actor, action, target, after);
        return actor != 0 && action != 0 && target != 0;
    }
    public bool Observe(long now, ulong actor, uint action, uint sequence, ulong target, bool actionTypeMatches = true)
    {
        lock (sync)
        {
            if (pending is not { } p) return false;
            if (now < p.At || now - p.At > 30000) { pending = null; return false; }
            if (!actionTypeMatches || actor != p.Actor || action != p.Action || sequence != p.Sequence || target != p.Target) return false;
            pending = null;
            counters.Record(FeatureTriggerKind.GcdProtection);
            return true;
        }
    }
    public void Reset() { lock (sync) pending = null; }
}
