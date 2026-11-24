namespace DeepDungeon.Fsd.Runtime;

/// <summary>Floor-scoped, confirmed petrification clear. Status evidence, never a dispatch, selects enemies.</summary>
public sealed class HohPetrificationState
{
    public const uint StoneCurseStatus = 437;
    private int _floor;
    private DateTime _confirmedAt;
    private bool _seenActive;
    public bool Clearing { get; private set; }
    public bool StopAttackingAfterClear { get; private set; }
    public bool UsedThisFloor => _confirmedAt != default;
    public void BeginFloor(uint dungeon, int floor)
    {
        int key = dungeon == 2 ? floor : 0;
        if (_floor == key) return;
        _floor = key; _confirmedAt = default; _seenActive = false;
        Clearing = StopAttackingAfterClear = false;
    }
    public void ConfirmUse(DateTime now)
    { _confirmedAt = now; _seenActive = false; Clearing = true; StopAttackingAfterClear = false; }
    public void Observe(bool nativeActive, bool hasPetrifiedEnemies, bool passageOpen, DateTime now)
    {
        if (_confirmedAt == default) return;
        bool active = nativeActive || hasPetrifiedEnemies;
        _seenActive |= active;
        // Opening the passage ends voluntary petrified pulls immediately, even while stone curse remains.
        // The floor controller then resumes its existing chest/exploration/engagement work.
        StopAttackingAfterClear = passageOpen;
        Clearing = !passageOpen && (active || !_seenActive && now - _confirmedAt < TimeSpan.FromSeconds(5));
    }
    // After the clear, stop voluntary pulls; existing hate must still be finished.
    public bool AllowsTarget(bool petrified, bool engaged = false) =>
        StopAttackingAfterClear ? engaged : !Clearing || petrified;
}
