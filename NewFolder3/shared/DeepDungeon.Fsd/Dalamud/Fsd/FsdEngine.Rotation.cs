using global::Dalamud.Plugin.Ipc;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private static ICallGateSubscriber<bool>? _wrathRotationState, _rsrRotationState, _promeRotationState, _promeRotationStop, _promeRotationStart;
    private static ICallGateSubscriber<string, object>? _rsrRotationMode;
    private ICallGateSubscriber<bool>? _aeRunning, _aePull;
    private ICallGateSubscriber<bool, object>? _aeSetRunning, _aeSetPull;
    private AeRotationControl? _aeRotation;
    private static readonly DateTime[] RotationErrorRetryAt = new DateTime[Enum.GetValues<FsdRotationProvider>().Length + 1];
    private bool _bossRotationSuppressed;
    private bool _pt99OutputGateClosed;

    private bool SetBossMovementOverride(bool active)
    {
        if (active && !_configuration.BossMechanics.UsesBmrBossHandling()) return false;
        bool accepted = _bossMechanics.SetInternalMovementOverride(active);
        _configuration.BossMechanicsActive = _bossMechanics.IsEnabled;
        return accepted;
    }

    private void UpdateCompanionControls()
    {
        if (!IsRunActive || !_configuration.BossMechanics.UsesBmrBossHandling()) _bossRotationSuppressed = false;
        var mode = _ddHost?.Context?.FarmingPlan?.Mode;
        _bossMechanics.Update(IsRunActive, _currentDeepDungeonState, _configuration.BossMechanics, mode);
        if (IsRunActive && !_configuration.Rotation.TryValidate(out var error))
        {
            StopForRotationError(error);
            return;
        }
        bool inCombat = Service.Condition[ConditionFlag.InCombat];
        bool pt99OutputGateClosed = Pt99OutputGateClosed();
        _ddHost?.Context?.ObserveCombatState(inCombat);
        if (IsRunActive) _ddHost?.Context?.EnforceWisdomTargetGate();
        _rotationControl.Update(IsRunActive, mode, _currentDeepDungeonState,
            inCombat, _configuration.Rotation, _bossRotationSuppressed || pt99OutputGateClosed || _ddHost?.Context?.NormalCombatOutputSuppressed == true ||
                (_ddHost?.IsFinalBossPractice == true && (!_ddHost.DutyEntryReady || _ddHost.Context?.DutyCompletionObserved == true)),
            _ddHost?.Context?.CombatPreparationSequence ?? 0,
            Service.LocalPlayer is { IsDead: false } &&
            !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51]);
        if (IsRunActive && _rotationControl.FailedProvider is { } failed)
            StopForRotationError($"無法{(_rotationControl.DesiredEnabled ? "啟用" : "關閉")}輸出：{GetRotationProviderLabel(failed)}。");
    }

    private bool Pt99OutputGateClosed()
    {
        bool scoped = IsRunActive && _configuration.BossMechanics.UsesBmrBossHandling() &&
            _currentDeepDungeonState is { IsInDuty: true, DungeonId: 4, Floor: 99 };
        bool light = false, dark = false;
        if (scoped && Service.LocalPlayer is { } player)
            foreach (var buff in player.StatusList)
            {
                light |= buff.StatusId == Pt99TargetPolicy.LightStatusId;
                dark |= buff.StatusId == Pt99TargetPolicy.DarkStatusId;
            }
        var target = Service.TargetManager.Target as IBattleNpc;
        bool closed = scoped && (target is not { IsDead: false, IsTargetable: true } ||
            !Pt99TargetPolicy.IsOutputTargetCompatible(light, dark, target.BaseId,
                _ddHost?.Context?.CombatAssist.Pt99Decision.Target == Pt99TargetKind.Minion));
        if (closed != _pt99OutputGateClosed)
        {
            _pt99OutputGateClosed = closed;
            _ddHost?.Context?.RecordDiagnostic?.Invoke("pt99-output-gate", new { closed, light, dark,
                targetId = target?.GameObjectId, targetBaseId = target?.BaseId, observedUtc = DateTime.UtcNow });
        }
        return closed;
    }

    private void StopForRotationError(string error)
    {
        _farmingError = error;
        Service.Log.Warning($"[FSD.Rotation] Stopping FSD: {error}");
        _ddHost?.StopFsd("rotation-control-failure", new { error, provider = _configuration.Rotation.Provider.ToString(),
            desiredEnabled = _rotationControl.DesiredEnabled, state = _currentDeepDungeonState,
            playerAvailable = Service.LocalPlayer != null, inCombat = Service.Condition[ConditionFlag.InCombat],
            betweenAreas = Service.Condition[ConditionFlag.BetweenAreas], betweenAreas51 = Service.Condition[ConditionFlag.BetweenAreas51] });
    }

    private void RecordRotationControl(FsdRotationControlEvent control)
    {
        var installed = Service.PluginInterface.InstalledPlugins.FirstOrDefault(plugin => control.Provider switch
        {
            FsdRotationProvider.RotationSolverReborn => plugin.InternalName is "RotationSolver" or "RotationSolverReborn",
            FsdRotationProvider.WrathCombo => plugin.InternalName == "WrathCombo",
            FsdRotationProvider.PromeRotation => plugin.InternalName == "PromeRotation",
            FsdRotationProvider.InsertNameHere3 => plugin.InternalName == "InsertNameHere3",
            FsdRotationProvider.AeAssist => plugin.InternalName.StartsWith("AEAssist", StringComparison.OrdinalIgnoreCase),
            _ => false
        });
        _ddHost?.FloorController.RecordReplayEvent("rotation-control", new {
            provider = control.Provider.ToString(), control.Enabled, before = control.Before.ToString(),
            after = control.After.ToString(), control.Accepted, control.Reason, control.DungeonId, control.Floor,
            plugin = installed?.InternalName, version = installed?.Version.ToString(),
            playerAvailable = Service.LocalPlayer != null, job = Service.LocalPlayer?.ClassJob.RowId,
            inCombat = Service.Condition[ConditionFlag.InCombat], state = _currentDeepDungeonState });
    }

    private bool TryValidateRotation(out string error)
    {
        var settings = _configuration.Rotation;
        if (!settings.TryValidate(out error)) return false;
        if (settings.Provider == FsdRotationProvider.Custom)
        {
            if (HasRegisteredCommand(settings.CustomEnableCommand) && HasRegisteredCommand(settings.CustomDisableCommand)) return true;
            error = "自訂輸出指令不可用，請確認插件已載入。";
            return false;
        }
        if (ReadRotationState(settings.Provider) != FsdRotationState.Unavailable) return true;
        error = $"自動輸出插件不可用：{GetRotationProviderLabel(settings.Provider)}。";
        return false;
    }

    private static bool HasRegisteredCommand(string command)
    {
        string value = command.Trim();
        int end = 0;
        while (end < value.Length && !char.IsWhiteSpace(value[end])) end++;
        return Service.CommandManager.Commands.ContainsKey(value[..end]);
    }

    private FsdRotationState ReadRotationState(FsdRotationProvider provider)
    {
        if (provider == FsdRotationProvider.Custom) return FsdRotationState.Unknown;
        try
        {
            ICallGateSubscriber<bool> read;
            switch (provider)
            {
                case FsdRotationProvider.RotationSolverReborn:
                    _rsrRotationMode ??= Service.PluginInterface.GetIpcSubscriber<string, object>("RotationSolverReborn.ChangeOperatingMode");
                    if (!_rsrRotationMode.HasAction) return FsdRotationState.Unavailable;
                    read = _rsrRotationState ??= Service.PluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
                    break;
                case FsdRotationProvider.WrathCombo:
                    if (!Service.CommandManager.Commands.ContainsKey("/wrath")) return FsdRotationState.Unavailable;
                    read = _wrathRotationState ??= Service.PluginInterface.GetIpcSubscriber<bool>("WrathCombo.GetAutoRotationState");
                    break;
                case FsdRotationProvider.InsertNameHere3:
                    return Service.CommandManager.Commands.ContainsKey("/inh3")
                        ? FsdRotationState.Unknown : FsdRotationState.Unavailable;
                case FsdRotationProvider.PromeRotation:
                    _promeRotationStart ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Start");
                    _promeRotationStop ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Stop");
                    if (!_promeRotationStart.HasFunction || !_promeRotationStop.HasFunction) return FsdRotationState.Unavailable;
                    read = _promeRotationState ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.IsRunning");
                    break;
                case FsdRotationProvider.AeAssist:
                    return EnsureAeRotationControl() ? _aeRotation!.ReadState() : FsdRotationState.Unavailable;
                default: return FsdRotationState.Unavailable;
            }
            return !read.HasFunction ? FsdRotationState.Unknown : read.InvokeFunc() ? FsdRotationState.On : FsdRotationState.Off;
        }
        catch (Exception error)
        {
            _ddHost?.FloorController.RecordReplayEvent("rotation-state-read-error", new {
                provider = provider.ToString(), error = error.ToString(), state = _currentDeepDungeonState });
            Service.Log.Warning($"[FSD.Rotation] ReadRotationState provider={provider} error={error}");
            return FsdRotationState.Unavailable;
        }
    }

    private bool SetRotationEnabled(FsdRotationProvider provider, bool enabled, string customCommand)
    {
        try
        {
            bool succeeded;
            if (provider == FsdRotationProvider.RotationSolverReborn)
            {
                // Dalamud converts the string to the provider's enum. Manual keeps hostile target selection with FSD; direct mode setting avoids slash-command toggles.
                (_rsrRotationMode ??= Service.PluginInterface.GetIpcSubscriber<string, object>("RotationSolverReborn.ChangeOperatingMode"))
                    .InvokeAction(enabled ? "Manual" : "Off");
                succeeded = true;
            }
            else succeeded = provider switch
            {
                FsdRotationProvider.WrathCombo => Service.CommandManager.ProcessCommand(enabled ? "/wrath auto on" : "/wrath auto off"),
                FsdRotationProvider.InsertNameHere3 => Service.CommandManager.ProcessCommand(enabled ? "/inh3 on" : "/inh3 off"),
                FsdRotationProvider.PromeRotation => (enabled
                    ? _promeRotationStart ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Start")
                    : _promeRotationStop ??= Service.PluginInterface.GetIpcSubscriber<bool>("PromeRotation.IPC.Stop")).InvokeFunc(),
                FsdRotationProvider.AeAssist => EnsureAeRotationControl() && _aeRotation!.SetEnabled(enabled),
                FsdRotationProvider.Custom => FsdRotationSettings.IsSingleSlashCommand(customCommand) &&
                    HasRegisteredCommand(customCommand) && Service.CommandManager.ProcessCommand(customCommand.Trim()),
                _ => false
            };
            if (succeeded) Service.Log.Info($"[FSD.Rotation] {(enabled ? "Enable" : "Disable")} request accepted for {provider}; stop will not restore output.");
            else LogRotationFailure(provider, "mode request was not accepted");
            return succeeded;
        }
        catch (Exception error)
        {
            _ddHost?.FloorController.RecordReplayEvent("rotation-control-error", new {
                provider = provider.ToString(), enabled, error = error.ToString(), state = _currentDeepDungeonState });
            LogRotationFailure(provider, error.Message);
            return false;
        }
    }

    private static void LogRotationFailure(FsdRotationProvider provider, string error)
    {
        var now = DateTime.UtcNow;
        if (now < RotationErrorRetryAt[(int)provider]) return;
        RotationErrorRetryAt[(int)provider] = now.AddSeconds(5);
        Service.Log.Warning($"[FSD.Rotation] Cannot set output for {provider}: {error}");
    }

    private bool EnsureAeRotationControl()
    {
        _aeRunning ??= Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsRunning");
        _aePull ??= Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsPullEnabled");
        _aeSetRunning ??= Service.PluginInterface.GetIpcSubscriber<bool, object>("AEAssist.CombatRoutine.SetRunning");
        _aeSetPull ??= Service.PluginInterface.GetIpcSubscriber<bool, object>("AEAssist.CombatRoutine.SetPullEnabled");
        if (!_aeRunning.HasFunction || !_aePull.HasFunction || !_aeSetRunning.HasAction || !_aeSetPull.HasAction ||
            !HasRegisteredCommand("/aestop")) return false;
        _aeRotation ??= new AeRotationControl(() => _aeRunning.InvokeFunc(), () => _aePull.InvokeFunc(),
            enabled => _aeSetRunning.InvokeAction(enabled), enabled => _aeSetPull.InvokeAction(enabled),
            stopped => Service.CommandManager.ProcessCommand(stopped ? "/aestop on" : "/aestop off"));
        return true;
    }
}
