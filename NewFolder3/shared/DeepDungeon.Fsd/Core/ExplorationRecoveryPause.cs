namespace DeepDungeon.Fsd.Core;

public readonly record struct ExplorationRecoverySnapshot(uint DungeonId, byte Floor, bool InDuty,
    bool StateReady, bool Transitioning, bool PlayerAlive, bool DutyFailed, bool DutyCompleted,
    bool TimerExpired = false);

/// <summary>A typed error stop retained only for the original live floor, never an arbitrary restart.</summary>
public sealed class ExplorationRecoveryPause
{
    public bool IsPaused { get; private set; }
    public uint DungeonId { get; private set; }
    public byte Floor { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public void Pause(uint dungeonId, byte floor, string reason)
    { IsPaused = true; DungeonId = dungeonId; Floor = floor; Reason = reason; }
    public bool CanResume(in ExplorationRecoverySnapshot s) => IsPaused &&
        s.InDuty && s.StateReady && !s.Transitioning && s.PlayerAlive && !s.DutyFailed && !s.DutyCompleted && !s.TimerExpired &&
        s.DungeonId == DungeonId && s.Floor == Floor && s.Floor > 0;
    public void Clear() { IsPaused = false; DungeonId = 0; Floor = 0; Reason = string.Empty; }
}
