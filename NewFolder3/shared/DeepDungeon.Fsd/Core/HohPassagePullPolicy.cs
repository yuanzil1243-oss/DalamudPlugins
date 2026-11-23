namespace DeepDungeon.Fsd.Core;

/// <summary>Native passage stages, not an estimate that each kill advances one stage.</summary>
public static class HohPassagePullPolicy
{
    public const int OpenProgress = 11;
    public static int MaximumEngaged(int? progress) => progress switch
    {
        >= OpenProgress => 0,
        >= 8 and < OpenProgress => 1,
        >= 6 and < 8 => 3,
        >= 0 and < 6 => 6,
        _ => 1 // Unknown state permits finishing combat, never speculative extra pulls.
    };
}
