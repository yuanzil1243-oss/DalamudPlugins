using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public sealed class FarmingSettings
{
    public FarmingMode Mode { get; set; }
    public CheckpointEntrySettings CheckpointEntry { get; set; } = new();
    public Dictionary<string, FarmingPreferences> Preferences { get; set; } = new();

    public FarmingPreferences GetPreferences(FsdSettings legacy)
    {
        string key = Mode switch
        {
            FarmingMode.Hoard => "Hoard:Create",
            FarmingMode.Aetherpool => "Aetherpool:Prepared",
            FarmingMode.HoardDiscovery => "HoardDiscovery:Prepared",
            FarmingMode.FreeExploration => "FreeExploration:Create",
            FarmingMode.FinalBoss => "FinalBoss:None",
            _ => "DeepProgression:Create"
        };
        if (!Preferences.TryGetValue(key, out var value))
        {
            value = new FarmingPreferences
            {
                OpenGold = Mode != FarmingMode.HoardDiscovery && legacy.NecromancerAutoOpenGoldChest,
                OpenSilver = Mode == FarmingMode.Aetherpool || Mode != FarmingMode.HoardDiscovery && legacy.NecromancerAutoOpenSilverChest,
                OpenBronze = Mode == FarmingMode.Aetherpool || Mode != FarmingMode.HoardDiscovery && legacy.NecromancerAutoOpenBronzeChest,
                Cycles = Mode == FarmingMode.FinalBoss ? 1 : legacy.NecromancerFsdLoopCount,
                Infinite = Mode != FarmingMode.FinalBoss && legacy.NecromancerFsdLoopInfinite
            };
            Preferences.Add(key, value);
        }
        value.BandedEnabled ??= Mode == FarmingMode.Hoard && legacy.NecromancerAutoBandedFarmEnabled;
        var targets = FarmingTargetPolicy.Constrain(Mode, new(value.BandedEnabled.Value,
            value.OpenGold, value.OpenSilver, value.OpenBronze));
        value.BandedEnabled = targets.Hoard;
        value.OpenGold = targets.Gold;
        value.OpenSilver = targets.Silver;
        value.OpenBronze = targets.Bronze;
        return value;
    }
}

public sealed class CheckpointEntrySettings
{
    public bool Enabled { get; set; } = true;
    public int StartFloor { get; set; } = 31;
    public int StopAfterFloor { get; set; } = 50;
    public int? ResumeSlot { get; set; }

    public FsdStartRequest CreateRequest(FarmingMode mode, FsdStartScenario scenario) =>
        Enabled && mode == FarmingMode.FreeExploration && scenario == FsdStartScenario.Farming
            ? new(StopAfterFloor: StopAfterFloor, DiagnosticStartFloor: ResumeSlot.HasValue ? null : StartFloor, ResumeSlot: ResumeSlot,
                Scenario: scenario)
            : new(Scenario: scenario);
}

public sealed class FarmingPreferences
{
    public bool? BandedEnabled { get; set; }
    public bool OpenGold { get; set; }
    public bool OpenSilver { get; set; }
    public bool OpenBronze { get; set; }
    public int Cycles { get; set; } = 1;
    public bool Infinite { get; set; }
    public bool PreserveSaveOnCompletion { get; set; }
}
