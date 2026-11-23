namespace DeepDungeon.Fsd.Runtime;

/// <summary>Suppresses room-index flapping while world coordinates oscillate at a doorway.</summary>
public sealed class HohCombatRoomLatch
{
    public const float SwitchAdvantageSquared = 9f;
    private uint _dungeon;
    private int _floor = -1;
    public int Room { get; private set; } = -1;

    public int Resolve(uint dungeon, int floor, int candidateRoom, float candidateDistanceSquared, float currentDistanceSquared)
    {
        if (_dungeon != dungeon || _floor != floor) { _dungeon = dungeon; _floor = floor; Room = -1; }
        if (candidateRoom < 0 || !float.IsFinite(candidateDistanceSquared)) return Room;
        if (Room < 0 || float.IsFinite(currentDistanceSquared) &&
            candidateDistanceSquared + SwitchAdvantageSquared < currentDistanceSquared)
            Room = candidateRoom;
        return Room;
    }

    public void Reset() { _dungeon = 0; _floor = -1; Room = -1; }
}
