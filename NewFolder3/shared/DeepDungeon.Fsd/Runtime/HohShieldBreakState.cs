using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public readonly record struct HohShieldBreakFrame(ulong Target, bool Eligible, bool Engaged, bool HasShield,
    bool AeEnabled, bool NativeObserver, bool OtherProtection, bool OutputPosition, bool ActionReady,
    bool PlayerBusy);
public readonly record struct HohShieldBreakDecision(bool Cast, bool HoldPosition, string Reason, int Attempts);

/// <summary>Finite damage-only assistance for AE's breakable151 stop; never starts combat or changes AE switches.</summary>
public sealed class HohShieldBreakState
{
    public const uint Shield = 151;
    public const int MaximumAttempts = 8;
    public static readonly TimeSpan QuietTime = TimeSpan.FromSeconds(2.5), Window = TimeSpan.FromSeconds(24);
    private sealed class Episode(DateTime now)
    {
        internal DateTime Seen=now, LastAttempt, LastExternalSkill;
        internal DateTime? Started;
        internal uint Action;
        internal int Attempts;
        internal bool Accepted;
    }
    private readonly Dictionary<ulong, Episode> _episodes = new();
    private readonly Dictionary<ulong, DateTime> _externalSkills = new();
    private uint _dungeon; private int _floor;
    private bool _scoped;
    public ulong HoldTarget { get; private set; }
    private DateTime _holdUntil;
    public static bool Enabled(FarmingMode? mode, uint dungeon, int floor, FsdRotationProvider rotation) =>
        mode == FarmingMode.FreeExploration && rotation == FsdRotationProvider.AeAssist && dungeon == 2 &&
        floor is > 0 and < 100 && floor % 10 != 0;
    public void BeginFloor(FarmingMode? mode, uint dungeon, int floor, FsdRotationProvider rotation)
    {
        bool scoped=Enabled(mode,dungeon,floor,rotation);
        if (_scoped!=scoped || _dungeon!=dungeon || _floor!=floor) Reset();
        _scoped=scoped; _dungeon=dungeon; _floor=floor;
    }
    // AA alone is insufficient evidence that AE resumed its skill rotation.
    public bool ObserveHit(ulong target, DateTime at, uint action)
    {
        if (!_scoped || target==0 || action is 0 or 8) return false;
        if (_episodes.TryGetValue(target,out var e) && e.Accepted && e.Action==action && at>=e.LastAttempt &&
            at-e.LastAttempt<=TimeSpan.FromSeconds(5)) return true;
        if (!_externalSkills.ContainsKey(target) && _externalSkills.Count>=128)
            _externalSkills.Remove(_externalSkills.MinBy(p=>p.Value).Key);
        _externalSkills[target]=at;
        if (_episodes.TryGetValue(target,out e)) e.LastExternalSkill=at;
        return false;
    }
    public HohShieldBreakDecision Update(in HohShieldBreakFrame f, DateTime now)
    {
        HoldTarget=0;
        // Keep exhausted episodes until a confirmed shield disappearance or a floor reset.
        // Evicting by age would renew the same target's budget after selection changes.
        if (!_scoped || !f.Eligible || f.Target==0 || !f.Engaged)
            return new(false,false,"out-of-scope-or-output-not-owned",0);
        if (!f.HasShield)
        {
            bool wasBreaking=_episodes.Remove(f.Target);
            return new(false,false,wasBreaking?"shield-gone-return-to-ae":"no-shield",0);
        }
        if (!f.AeEnabled || !f.NativeObserver)
            return new(false,false,"out-of-scope-or-output-not-owned",0);
        if (!_episodes.TryGetValue(f.Target,out var e))
        {
            if (_episodes.Count>=64) return new(false,false,"episode-capacity",0);
            _episodes[f.Target]=e=new(now);
            if (_externalSkills.TryGetValue(f.Target,out var last)) e.LastExternalSkill=last;
        }
        if (f.OtherProtection) return new(false,false,"other-invulnerability-or-reflect",e.Attempts);
        if (now<e.Seen || now-e.Seen<QuietTime || now-e.LastExternalSkill<QuietTime)
            return new(false,false,"waiting-for-ae-skill-progress",e.Attempts);
        if (e.Attempts>=MaximumAttempts || e.Started is {} started && now-started>=Window)
            return new(false,false,"shield-break-budget-exhausted",e.Attempts);
        if (!f.OutputPosition) return new(false,false,"needs-safe-output-position",e.Attempts);
        e.Started??=now;
        HoldTarget=f.Target; _holdUntil=now.AddMilliseconds(500);
        bool ready=!f.PlayerBusy && f.ActionReady && (e.LastAttempt==default ||
            now-e.LastAttempt>=(e.Accepted?QuietTime:TimeSpan.FromSeconds(1)));
        return new(ready,true,ready?"ae-on-stoneskin-no-skill-progress":"waiting-for-native-action-ready",e.Attempts);
    }
    public void RecordAttempt(ulong target, uint action, bool accepted, DateTime now)
    {
        if (!_scoped || !_episodes.TryGetValue(target,out var e) || e.Attempts>=MaximumAttempts) return;
        e.Attempts++; e.Action=action; e.Accepted=accepted; e.LastAttempt=now;
    }
    public bool HoldsOutput(ulong target, DateTime now) => target!=0 && target==HoldTarget && now<=_holdUntil;
    public void Reset()
    { _episodes.Clear(); _externalSkills.Clear(); HoldTarget=0; _holdUntil=default; }
}

public static class HohShieldBreakActions
{
    // Base actions verified against the local CN client. Native adjusted IDs handle level-sync upgrades.
    public static uint ForJob(uint job) => job switch
    {
        1 or 19=>9, 3 or 21=>31, 32=>3617, 37=>16137,
        2 or 20=>53, 4 or 22=>75, 29 or 30=>2240, 34=>7477, 39=>24373, 41=>34606,
        5 or 23=>97, 31=>2866, 38=>15989,
        7 or 25=>141, 26 or 27=>163, 35=>7503, 42=>34650,
        6 or 24=>119, 28=>17869, 33=>3596, 40=>24283,
        _=>0
    };
}
