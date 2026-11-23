namespace DeepDungeon.Fsd.Core;

public readonly record struct HohGazeObservation(bool RunActive, uint DungeonId, int Floor,
    bool PlayerAlive, ulong BossId, uint BossBaseId, bool BossAlive, uint CastActionId,
    double CastRemainingSeconds);

/// <summary>Pause attacks that can auto-face during Gozu's gaze; BMR retains facing and movement.</summary>
public sealed class HohGazeOutputGuard
{
    public const uint GozuBaseId = 9193;
    public const uint GazeActionId = 11922;
    public static readonly TimeSpan ResolutionMargin = TimeSpan.FromMilliseconds(750);
    private ulong _bossId;
    public DateTime SuppressUntilUtc { get; private set; }
    public bool IsActive { get; private set; }

    public bool Update(DateTime now, HohGazeObservation observed)
    {
        if (!observed.RunActive || observed.DungeonId != HohLocalPolicy.DungeonId || observed.Floor != 50 ||
            !observed.PlayerAlive || !observed.BossAlive || observed.BossId == 0 || observed.BossBaseId != GozuBaseId)
        {
            Reset();
            return false;
        }
        if (_bossId != observed.BossId) Reset();
        _bossId = observed.BossId;
        if (observed.CastActionId == GazeActionId && double.IsFinite(observed.CastRemainingSeconds))
        {
            var until = now.AddSeconds(Math.Clamp(observed.CastRemainingSeconds, 0, 10)) + ResolutionMargin;
            if (until > SuppressUntilUtc) SuppressUntilUtc = until;
        }
        return IsActive = now < SuppressUntilUtc;
    }

    public void Reset()
    {
        _bossId = 0;
        SuppressUntilUtc = default;
        IsActive = false;
    }
}
