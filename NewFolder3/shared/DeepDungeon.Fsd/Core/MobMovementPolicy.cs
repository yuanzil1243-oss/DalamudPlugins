namespace DeepDungeon.Fsd.Core;

public enum MobMovementOwner { Routine, Mechanics, Fight, Consolidation }

public static class MobMovementPolicy
{
    // Priority fights allow chasing/recovery; they are never a stationary movement lock.
    public static MobMovementOwner Choose(bool mechanics, bool priority, bool solo, bool consolidation) =>
        mechanics ? MobMovementOwner.Mechanics : priority ? MobMovementOwner.Fight :
        consolidation ? MobMovementOwner.Consolidation : solo ? MobMovementOwner.Fight : MobMovementOwner.Routine;
}
