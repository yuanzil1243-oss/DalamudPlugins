using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public enum HohContinuityPhase { Running, Disconnected, WaitingRevival, ConfirmedDeath, Exited, Mismatch }
public readonly record struct HohContinuityFrame(bool LoggedIn, bool PlayerAvailable, bool Stable,
    bool InDuty, uint DungeonId, byte Floor, uint Territory, bool Dead, bool Terminal);

/// <summary>Per-attempt HoH continuity; does not change pause, completion, or save ownership.</summary>
public sealed class HohRunContinuity
{
    public static readonly TimeSpan RevivalGrace = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan ReconnectSettle = TimeSpan.FromSeconds(1);
    public HohContinuityPhase Phase { get; private set; }
    public bool BossRebuffPending { get; private set; }
    public bool HoldsHost => Phase is HohContinuityPhase.Disconnected or HohContinuityPhase.WaitingRevival or HohContinuityPhase.Mismatch;
    public bool HoldsControls => HoldsHost || BossRebuffPending || Phase is HohContinuityPhase.ConfirmedDeath or HohContinuityPhase.Exited;
    public long ResumeSequence { get; private set; }
    public bool HasIdentity => _territory != 0;
    private uint _territory;
    private int _floorset;
    private byte _lastFloor;
    private DateTime? _deathAt, _stableAt;
    private DateTime? _revivedAt;
    public static bool Applies(FarmingMode? mode, uint dungeonId) => mode == FarmingMode.FreeExploration && dungeonId == 2;

    public void Observe(in HohContinuityFrame f, DateTime now)
    {
        // Verified completion/failure must release the scenario to finish its own cleanup,
        // even if its event arrived during a disconnected/revival waiting frame.
        if (f.Terminal) { Phase = HohContinuityPhase.Exited; return; }
        if (Phase is HohContinuityPhase.Mismatch or HohContinuityPhase.ConfirmedDeath or HohContinuityPhase.Exited) return;
        if (_territory == 0)
        {
            if (!f.LoggedIn || !f.PlayerAvailable || !f.Stable || !f.InDuty || f.DungeonId != 2 || f.Floor == 0) return;
            Remember(f);
        }
        if (!f.LoggedIn || !f.PlayerAvailable && f.Stable)
        {
            Phase = HohContinuityPhase.Disconnected; _stableAt = null; return;
        }
        if (Phase == HohContinuityPhase.Disconnected)
        {
            if (!f.PlayerAvailable || !f.Stable) { _stableAt = null; return; }
            _stableAt ??= now;
            if (now - _stableAt < ReconnectSettle) return;
            if (!f.InDuty) { Phase = HohContinuityPhase.Exited; return; }
            if (f.DungeonId != 2 || f.Territory != _territory || (f.Floor - 1) / 10 != _floorset)
            { Phase = HohContinuityPhase.Mismatch; return; }
            Phase = _deathAt.HasValue ? HohContinuityPhase.WaitingRevival : HohContinuityPhase.Running;
            if (!f.Dead) Resume(f.Floor, now);
            else if (_deathAt.HasValue) _deathAt = now; // Require observed death online, not an offline wall-clock timeout.
        }
        if (!f.PlayerAvailable || !f.Stable || !f.InDuty) return;
        if (f.Dead) { ObserveDeath(now); return; }
        if (Phase == HohContinuityPhase.WaitingRevival) Resume(f.Floor, now);
        if (f.DungeonId == 2 && f.Floor > 0) Remember(f);
    }

    public void ObserveDeath(DateTime now)
    {
        if (Phase is HohContinuityPhase.ConfirmedDeath or HohContinuityPhase.Mismatch or HohContinuityPhase.Exited) return;
        _deathAt ??= now;
        Phase = now - _deathAt >= RevivalGrace ? HohContinuityPhase.ConfirmedDeath : HohContinuityPhase.WaitingRevival;
    }

    private void Remember(in HohContinuityFrame f)
    { _territory = f.Territory; _floorset = (f.Floor - 1) / 10; _lastFloor = f.Floor; }
    private void Resume(byte floor, DateTime now)
    {
        if (_deathAt.HasValue) _revivedAt = now;
        BossRebuffPending = (floor == 0 ? _lastFloor : floor) % 10 == 0;
        Phase = HohContinuityPhase.Running; _deathAt = _stableAt = null; ResumeSequence++;
    }
    public void CompleteBossRebuff() { if (BossRebuffPending) { BossRebuffPending = false; ResumeSequence++; } }
    public bool DeferWipe(bool playerDead, DateTime now) => HasIdentity &&
        Phase is not (HohContinuityPhase.ConfirmedDeath or HohContinuityPhase.Exited or HohContinuityPhase.Mismatch) &&
        (playerDead || HoldsHost || BossRebuffPending || _revivedAt is { } revived && now - revived < TimeSpan.FromSeconds(2));
    public void Reset()
    {
        Phase = HohContinuityPhase.Running; BossRebuffPending = false; ResumeSequence = 0;
        _territory = 0; _floorset = 0; _lastFloor = 0; _deathAt = _stableAt = _revivedAt = null;
    }
}

public enum HohRebuffItemState { Present, NoStock, Forbidden, Pending, Ready }
public enum HohRebuffStep { Complete, Wait, Steel, Strength }
public static class HohRevivalBuffPolicy
{
    public static HohRebuffItemState ObserveItem(bool buffPresent, bool requestPendingOrRecentlyConfirmed,
        int stock, bool floorAllows, bool temporarilyBlocked, bool canAttempt, bool usable) =>
        buffPresent ? HohRebuffItemState.Present : requestPendingOrRecentlyConfirmed ? HohRebuffItemState.Pending :
        stock <= 0 ? HohRebuffItemState.NoStock : !floorAllows ? HohRebuffItemState.Forbidden :
        !temporarilyBlocked && canAttempt && usable ? HohRebuffItemState.Ready : HohRebuffItemState.Pending;
    private static bool Resolved(HohRebuffItemState s) => s is HohRebuffItemState.Present or HohRebuffItemState.NoStock or HohRebuffItemState.Forbidden;
    public static HohRebuffStep Next(HohRebuffItemState steel, HohRebuffItemState strength)
    {
        if (!Resolved(steel)) return steel == HohRebuffItemState.Ready ? HohRebuffStep.Steel : HohRebuffStep.Wait;
        if (!Resolved(strength)) return strength == HohRebuffItemState.Ready ? HohRebuffStep.Strength : HohRebuffStep.Wait;
        return HohRebuffStep.Complete;
    }
}
