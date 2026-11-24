namespace DeepDungeon.Fsd.Core;

/// <summary>Verified PT hazards, keyed by language-independent BNpcName row IDs.</summary>
public static class MobPullPolicy
{
    public static bool CanInitiate(FarmingMode? mode, uint dungeonId, int floor, uint nameId) =>
        !ProgressionModePolicy.IsProgression(mode) || dungeonId != 4 || !IsHighRisk(floor, nameId);

    public static bool RequiresSolo(FarmingMode? mode, uint dungeonId, int floor, uint nameId) =>
        ProgressionModePolicy.IsProgression(mode) && dungeonId == 4 && IsHighRisk(floor, nameId);

    public static bool CanInitiateControlled(FarmingMode? mode, uint dungeonId, int floor, uint nameId,
        int engagedCount, bool dangerousEngaged, bool alreadyEngaged) =>
        alreadyEngaged || !dangerousEngaged &&
        (!RequiresSolo(mode, dungeonId, floor, nameId) || engagedCount == 0);

    private static bool IsHighRisk(int floor, uint nameId) =>
        floor is >= 61 and <= 69 && nameId == 14179 || // PT66: lethal needle cast.
        floor is >= 71 and <= 79 && nameId is 14194 or 14197 || // Rockeater poison; Braggadocio AoEs.
        floor is >= 91 and <= 98 && nameId is 14219 or 14221 or 14224; // Supplied guide: dream woe, destruction, morbol.
}
