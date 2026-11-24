using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private AeOutputLease? _aeOutput;

    private bool EnsureAeOutput()
    {
        var running = Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsRunning");
        var pull = Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsPullEnabled");
        var setRunning = Service.PluginInterface.GetIpcSubscriber<bool, object>("AEAssist.CombatRoutine.SetRunning");
        var setPull = Service.PluginInterface.GetIpcSubscriber<bool, object>("AEAssist.CombatRoutine.SetPullEnabled");
        if (!running.HasFunction || !pull.HasFunction || !setRunning.HasAction || !setPull.HasAction) return false;
        _aeOutput ??= new(() => running.InvokeFunc(), () => pull.InvokeFunc(),
            value => setRunning.InvokeAction(value), value => setPull.InvokeAction(value));
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
            RestoreAeOutput();
    }

    private void RestoreAeOutput()
    {
        if (_aeOutput?.Restore() == false)
            LogRotationFailure(FsdRotationProvider.AEAssist, "無法恢復先前 AE 開關，等待 IPC 恢復後重試。");
    }
}
