using global::Dalamud.Plugin.Ipc;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.JobGauge.Types;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Actions;
using DeepDungeon.Fsd.Runtime;
using System.Numerics;

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
    private readonly HohGazeOutputGuard _hohGazeOutputGuard = new();
    private readonly SummonerOpenerController _summonerOpener = new();
    private bool _summonerOpenerSawCombat;
    private double _summonerOpenerTimerAtCast;
    private bool _summonerOpenerStatusAtCast;
    private DateTime _nextHohGazeAutoAttackStop;

    private bool SetBossMovementOverride(bool active)
    {
        if (active && !_configuration.BossMechanics.UsesBmrBossHandling()) return false;
        bool accepted = _bossMechanics.SetInternalMovementOverride(active);
        _configuration.BossMechanicsActive = _bossMechanics.IsEnabled;
        return accepted;
    }

    private void UpdateCompanionControls()
    {
        if (_ddHost?.Context is { } shieldContext) shieldContext.ShieldBreakOutputPermitted = false;
        if (!IsRunActive || !_configuration.BossMechanics.UsesBmrBossHandling()) _bossRotationSuppressed = false;
        var mode = _ddHost?.Context?.FarmingPlan?.Mode;
        _bossMechanics.Update(IsRunActive, _currentDeepDungeonState, _configuration.BossMechanics, mode);
        UpdateHoh90Kiting();
        if (IsRunActive && !_configuration.Rotation.TryValidate(out var error))
        {
            StopForRotationError(error);
            return;
        }
        bool inCombat = Service.Condition[ConditionFlag.InCombat];
        bool pt99OutputGateClosed = Pt99OutputGateClosed();
        bool hohGazeOutputGateClosed = UpdateHohGazeOutputGate();
        UpdateHoh70Puddle();
        _ddHost?.Context?.ObserveCombatState(inCombat);
        if (IsRunActive) _ddHost?.Context?.EnforceWisdomTargetGate();
        bool outputSuppressed = _bossRotationSuppressed || pt99OutputGateClosed || hohGazeOutputGateClosed || _ddHost?.Context?.NormalCombatOutputSuppressed == true ||
                (_ddHost?.IsFinalBossPractice == true && (!_ddHost.DutyEntryReady || _ddHost.Context?.DutyCompletionObserved == true));
        bool openerHolding = UpdateSummonerOpener(outputSuppressed, inCombat, now: DateTime.UtcNow);
        _rotationControl.Update(IsRunActive, mode, _currentDeepDungeonState,
            inCombat, _configuration.Rotation, outputSuppressed || openerHolding,
            _ddHost?.Context?.CombatPreparationSequence ?? 0,
            Service.LocalPlayer is { IsDead: false } &&
            !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51]);
        if (_ddHost?.Context is { } outputContext)
            outputContext.ShieldBreakOutputPermitted = IsRunActive && !outputSuppressed && !openerHolding &&
                _rotationControl.DesiredEnabled && _rotationControl.FailedProvider == null &&
                _aeRotation?.LastStopRequest == false;
        if (IsRunActive && _rotationControl.FailedProvider is { } failed)
            StopForRotationError($"無法{(_rotationControl.DesiredEnabled ? "啟用" : "關閉")}輸出：{GetRotationProviderLabel(failed)}。");
    }

    private unsafe bool UpdateSummonerOpener(bool outputSuppressed, bool inCombat, DateTime now)
    {
        if (inCombat) _summonerOpenerSawCombat = true;
        else if (_summonerOpenerSawCombat)
        {
            _summonerOpener.ResetCombat();
            _summonerOpenerSawCombat = false;
        }
        var player = Service.LocalPlayer;
        var target = Service.TargetManager.Target as IBattleNpc;
        bool scoped = IsRunActive && _configuration.Rotation.Provider == FsdRotationProvider.AeAssist &&
            _currentDeepDungeonState is { IsInDuty: true, IsTransitioning: false, FloorKind: DeepDungeonFloorKind.Mob } && player is { IsDead: false } &&
            player.ClassJob.RowId == 27 && !outputSuppressed &&
            !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51] &&
            target is { IsDead: false, IsTargetable: true, CurrentHp: > 0 } &&
            Vector2.DistanceSquared(new(player.Position.X, player.Position.Z), new(target.Position.X, target.Position.Z)) <= 25 * 25;
        if (!scoped)
        {
            _summonerOpener.Abort();
            if (target == null || player?.ClassJob.RowId != 27 || !IsRunActive) _summonerOpener.ResetCombat();
            return _summonerOpener.HoldsOutput;
        }

        uint readyAction = 0;
        var manager = ActionManager.Instance();
        if (manager != null)
        {
            uint level = player!.Level;
            var gauge = Service.JobGauges.Get<SMNGauge>();
            bool tranceReady = level < 70 && level >= 58 &&
                manager->GetActionStatus(ActionType.Action, SummonerOpenerActionPolicy.TranceActionId, player.GameObjectId) == Service.ActionStatus_Ready;
            uint adjustedBahamut = level >= 70 ? manager->GetAdjustedActionId(SummonerOpenerActionPolicy.BahamutActionId) : 0;
            bool bahamutReady = level >= 70 && gauge.IsBahamutReady && adjustedBahamut != 0 &&
                manager->GetActionStatus(ActionType.Action, adjustedBahamut, target!.GameObjectId) == Service.ActionStatus_Ready;
            bool phoenixReady = level >= 80 && gauge.IsPhoenixReady &&
                manager->GetActionStatus(ActionType.Action, SummonerOpenerActionPolicy.PhoenixActionId, target!.GameObjectId) == Service.ActionStatus_Ready;
            readyAction = SummonerOpenerActionPolicy.Select(level, tranceReady, bahamutReady,
                adjustedBahamut, phoenixReady);
        }
        if (_summonerOpener.Stage == SummonerOpenerStage.Idle && readyAction != 0)
            _summonerOpener.TryBegin(target!.GameObjectId, readyAction, true, now);

        bool effectStatus = player!.StatusList.Any(status => status.StatusId is 808 or 3228 or 1436 or 3229);
        double summonTimer = ReadSummonerTimer();
        bool effect = effectStatus && !_summonerOpenerStatusAtCast || summonTimer > _summonerOpenerTimerAtCast + 0.01;
        var previousStage = _summonerOpener.Stage;
        uint castAction = _summonerOpener.Tick(now, ReadRotationState(FsdRotationProvider.AeAssist) == FsdRotationState.Off, effect);
        if (castAction != 0)
        {
            _summonerOpenerTimerAtCast = summonTimer;
            _summonerOpenerStatusAtCast = effectStatus;
            var attempt = FsdActionExecutor.CastWithDiagnostics(castAction,
                castAction == 3581 ? player.GameObjectId : target!.GameObjectId);
            _summonerOpener.RecordCast(attempt.Accepted, now);
            _ddHost?.FloorController.RecordReplayEvent("summoner-opener-attempt", new { targetId = target!.GameObjectId,
                requestedActionId = castAction, attempt, stage = _summonerOpener.Stage.ToString(), observedUtc = now });
        }
        if (previousStage != _summonerOpener.Stage && _summonerOpener.Stage is SummonerOpenerStage.Complete or SummonerOpenerStage.Failed)
            _ddHost?.FloorController.RecordReplayEvent("summoner-opener-finished", new { targetId = _summonerOpener.TargetId,
                actionId = _summonerOpener.ActionId, stage = _summonerOpener.Stage.ToString(), effect, observedUtc = now });
        return _summonerOpener.HoldsOutput;
    }

    private static double ReadSummonerTimer()
    {
        try { return Convert.ToDouble(Service.JobGauges.Get<SMNGauge>().SummonTimerRemaining); }
        catch { return 0; }
    }

    private bool UpdateHohGazeOutputGate()
    {
        var state = _currentDeepDungeonState;
        bool scoped = IsRunActive && state is { IsInDuty: true, DungeonId: 2, Floor: 50 } &&
            Service.LocalPlayer is { IsDead: false } &&
            !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51];
        // Reacquire from the live object table; never retain a battle object across floor changes.
        IBattleNpc? boss = scoped ? Service.GameObjects.OfType<IBattleNpc>()
            .FirstOrDefault(n => n.BaseId == HohGazeOutputGuard.GozuBaseId && !n.IsDead && n.CurrentHp > 0) : null;
        var now = DateTime.UtcNow;
        bool before = _hohGazeOutputGuard.IsActive;
        bool closed = _hohGazeOutputGuard.Update(now, new(scoped, state.DungeonId, state.Floor,
            Service.LocalPlayer is { IsDead: false }, boss?.GameObjectId ?? 0, boss?.BaseId ?? 0,
            boss is { IsDead: false, CurrentHp: > 0 }, boss is { IsCasting: true } ? boss.CastActionId : 0,
            boss is { IsCasting: true } ? boss.TotalCastTime - boss.CurrentCastTime : 0));
        if (closed && now >= _nextHohGazeAutoAttackStop)
        {
            _nextHohGazeAutoAttackStop = now.AddMilliseconds(250);
            OmenTools.Interop.Game.ExecuteCommand.Implementations.AutoAttackCommand.Disable();
        }
        if (!closed) _nextHohGazeAutoAttackStop = default;
        UpdateHohGazeMovement(closed, boss?.Position ?? default);
        if (closed != before)
        {
            _ddHost?.Context?.RecordDiagnostic?.Invoke("hoh50-gaze-output-gate", new { closed,
                until = _hohGazeOutputGuard.SuppressUntilUtc, bossId = boss?.GameObjectId,
                castActionId = boss?.CastActionId, observedUtc = now });
            Service.Log.Info($"[HOH50.GazeOutput] suppressed={closed}, until={_hohGazeOutputGuard.SuppressUntilUtc:o}; BMR retains facing/movement");
        }
        return closed;
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
            plugin = installed?.InternalName, version = DiagnosticMetadata.Read(() => installed?.Version.ToString()),
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
