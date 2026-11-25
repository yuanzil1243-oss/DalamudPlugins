using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private AeOutputLease? _aeOutput;
    private AeTargetSelectorDtr? _aeSelectorDtr;
    private readonly AeTargetSelectorReadback _aeSelectorVerification = new();

    private bool EnsureAeOutput()
    {
        var running = Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsRunning");
        var pull = Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsPullEnabled");
        var setRunning = Service.PluginInterface.GetIpcSubscriber<bool, object>("AEAssist.CombatRoutine.SetRunning");
        var setPull = Service.PluginInterface.GetIpcSubscriber<bool, object>("AEAssist.CombatRoutine.SetPullEnabled");
        if (!running.HasFunction || !pull.HasFunction || !setRunning.HasAction || !setPull.HasAction ||
            !HasRegisteredCommand(AeOutputLease.TargetSelectorDisableCommand)) return false;
        _aeSelectorDtr ??= Service.PluginInterface.Create<AeTargetSelectorDtr>();
        _aeOutput ??= new(() => running.InvokeFunc(), () => pull.InvokeFunc(),
            value => setRunning.InvokeAction(value), value => setPull.InvokeAction(value),
            DisableAeTargetSelector);
        return true;
    }

    private bool DisableAeTargetSelector()
    {
        if (!HasRegisteredCommand(AeOutputLease.TargetSelectorDisableCommand) ||
            !Service.CommandManager.ProcessCommand(AeOutputLease.TargetSelectorDisableCommand)) return false;
        _aeSelectorVerification.CommandSent(Environment.TickCount64);
        return true;
    }

    private FsdRotationState ReadAeState()
    {
        if (!EnsureAeOutput()) return FsdRotationState.Unavailable;
        return Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsRunning").InvokeFunc() &&
            Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsPullEnabled").InvokeFunc()
            ? FsdRotationState.On : FsdRotationState.Off;
    }

    private void UpdateAeLifecycle()
    {
        if (!IsRunActive || _configuration.Rotation.Provider != FsdRotationProvider.AEAssist ||
            !_currentDeepDungeonState.IsInDuty || !_currentDeepDungeonState.IsInDeepDungeonTerritory)
        {
            RestoreAeOutput();
            return;
        }
        if (!_aeSelectorVerification.Pending) return;
        if (!_currentDeepDungeonState.IsValid || _currentDeepDungeonState.IsTransitioning || Service.LocalPlayer == null)
        {
            _aeSelectorVerification.DeferIfPending(Environment.TickCount64);
            return;
        }
        var status = _aeSelectorDtr?.Read() ?? AeTargetSelectorState.Unknown;
        var verified = _aeSelectorVerification.Observe(Environment.TickCount64, status);
        if (verified == AeTargetSelectorVerificationResult.Pending) return;
        _ddHost?.FloorController.RecordReplayEvent("ae-target-selector-readback", new
            { source = "readonly-dtr-entry", state = status.ToString(), result = verified.ToString() });
        if (verified == AeTargetSelectorVerificationResult.StillEnabled)
            StopForRotationError("AE 目標選擇器仍開啟；請關閉後再啟動探索。");
        else if (verified == AeTargetSelectorVerificationResult.Unknown)
            Service.Log.Warning("[LocalExtensions.AE] 已發送目標選擇器關閉指令，但無法從資訊欄確認狀態；未記為已關閉。");
    }

    private void RestoreAeOutput()
    {
        _aeSelectorVerification.Reset();
        if (_aeOutput?.Restore() == false)
            LogRotationFailure(FsdRotationProvider.AEAssist, "無法恢復先前 AE 開關，等待 IPC 恢復後重試。");
    }
}
