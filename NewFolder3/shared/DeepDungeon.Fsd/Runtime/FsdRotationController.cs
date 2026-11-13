using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public readonly record struct FsdRotationControlEvent(FsdRotationProvider Provider, bool Enabled,
    FsdRotationState Before, FsdRotationState After, bool Accepted, string Reason, uint DungeonId, byte Floor);

/// <summary>Dispatches output commands at gameplay boundaries, without enforcing a persistent switch state.</summary>
public sealed class FsdRotationController(Func<FsdRotationProvider, FsdRotationState> readState,
    Func<FsdRotationProvider, bool, string, bool> setEnabled,
    Action<FsdRotationControlEvent>? record = null)
{
    private bool _inDuty, _inCombat;
    private FsdRotationProvider _provider;
    private string _customCommand = string.Empty;
    private long _preparationSequence;
    private bool _aeShutdownPending;
    private DateTime _nextAeShutdownAttempt;
    public FsdRotationProvider? FailedProvider { get; private set; }
    public bool DesiredEnabled { get; private set; }

    public void Update(bool runActive, FarmingMode? mode, in DeepDungeonStateSnapshot state, bool inCombat,
        FsdRotationSettings settings, bool suppressOutput = false, long preparationSequence = 0,
        bool playerAvailable = true)
    {
        if (!runActive || !settings.TryValidate(out _))
        {
            Reset(); return;
        }
        // Existing providers keep their exit behavior; AE must close all three owned switches.
        if (!state.IsInDeepDungeonTerritory || !state.IsInDuty)
        {
            if (!DisableOwnedAe()) return;
            _inDuty = _inCombat = false;
            DesiredEnabled = false;
            _preparationSequence = 0;
            return;
        }
        if (!state.IsValid || state.IsTransitioning || !playerAvailable) return;
        if (_aeShutdownPending && settings.Provider != FsdRotationProvider.AeAssist && !DisableOwnedAe()) return;
        bool enabled = !suppressOutput && state.FloorKind != DeepDungeonFloorKind.Result &&
            mode is not (FarmingMode.Aetherpool or FarmingMode.HoardDiscovery);
        string command = (enabled ? settings.CustomEnableCommand : settings.CustomDisableCommand) ?? string.Empty;
        string? reason = !_inDuty ? "duty-entry" :
            DesiredEnabled != enabled ? "output-policy-changed" :
            _provider != settings.Provider || _customCommand != command ? "configuration-changed" :
            enabled && preparationSequence > 0 && preparationSequence != _preparationSequence ? "combat-preparation" :
            enabled && inCombat && !_inCombat ? "combat-started" : null;
        _inDuty = true;
        DesiredEnabled = enabled;
        _provider = settings.Provider;
        _customCommand = command;
        _inCombat = inCombat;
        _preparationSequence = preparationSequence;
        if (reason == null) return;

        // Readback is diagnostic only: RSR includes transient native-player availability.
        var before = readState(_provider);
        if (_provider == FsdRotationProvider.AeAssist) _aeShutdownPending = true;
        bool accepted = setEnabled(_provider, enabled, command);
        if (_provider == FsdRotationProvider.AeAssist && accepted && !enabled) _aeShutdownPending = false;
        var after = readState(_provider);
        FailedProvider = accepted ? null : _provider;
        record?.Invoke(new(_provider, enabled, before, after, accepted, reason, state.DungeonId, state.Floor));
    }

    public void Reset()
    {
        // An unavailable AE may remain loaded: retain the unresolved shutdown across restarts.
        // The inactive update loop must not flood commands every frame.
        if (!_aeShutdownPending || DateTime.UtcNow >= _nextAeShutdownAttempt) DisableOwnedAe();
        _inDuty = _inCombat = DesiredEnabled = false;
        _preparationSequence = 0;
        FailedProvider = _aeShutdownPending ? FsdRotationProvider.AeAssist : null;
    }

    private bool DisableOwnedAe()
    {
        // AE owns three explicit switches; do not leave pull enabled after a stop or duty exit.
        // Existing providers retain their previous exit behavior.
        if (!_aeShutdownPending) return true;
        bool accepted = setEnabled(FsdRotationProvider.AeAssist, false, string.Empty);
        _aeShutdownPending = !accepted;
        _nextAeShutdownAttempt = DateTime.UtcNow.AddSeconds(1);
        DesiredEnabled = false;
        FailedProvider = accepted ? null : FsdRotationProvider.AeAssist;
        return accepted;
    }
}
