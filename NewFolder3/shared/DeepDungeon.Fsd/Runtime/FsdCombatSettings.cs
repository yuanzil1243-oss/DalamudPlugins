namespace DeepDungeon.Fsd.Runtime;

public sealed class FsdCombatSettings
{
    public int HighFloorEnemyLimit { get; set; } = 3;
    public int HohHighFloorEnemyLimit { get; set; } = 3;
    public int GetHighFloorEnemyLimit(uint dungeon) => Math.Clamp(dungeon == 2 ? HohHighFloorEnemyLimit : HighFloorEnemyLimit, 1, 6);
}
