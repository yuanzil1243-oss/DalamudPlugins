using System.Numerics;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Runtime;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Enums;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Ipc;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private HohBossOrbitController? _hohBossOrbit;
    private MovementInputController? _hohOrbitInput;
    private readonly CombatPositionNavigation _hohOrbitGeometry = new();
    private ICallGateSubscriber<Vector3, Vector3, bool>? _hohOrbitPathSafe;
    private ICallGateSubscriber<bool>? _hohOrbitModuleActive;

    private HohBossOrbitFrame ReadHohOrbitFrame()
    {
        var player = Service.LocalPlayer;
        var target = Service.TargetManager.Target as IBattleNpc;
        bool targetReady = target is { IsTargetable: true, IsDead: false } && target.CurrentHp > 1 &&
            target.ObjectKind == ObjectKind.BattleNpc && (BattleNpcSubKind)target.SubKind == BattleNpcSubKind.Combatant;
        return new(_currentDeepDungeonState, IsRunActive, Service.Condition[ConditionFlag.InCombat],
            player is { IsDead: false } && !Service.Condition[ConditionFlag.BetweenAreas] &&
                !Service.Condition[ConditionFlag.BetweenAreas51],
            _configuration.BossMechanics.UsesBmrBossHandling(),
            targetReady ? target!.GameObjectId : 0, player?.Position ?? default, target?.Position ?? default,
            target?.HitboxRadius ?? 0, player?.HitboxRadius ?? 0);
    }

    internal void HandleHohBossCircleCommand(string[] args)
    {
        try
        {
            var command = HohBossOrbitCommand.Parse(args);
            if (command.Stop)
            {
                _hohBossOrbit?.Stop(notify: false);
                Service.Chat.Print("[NewFolder3] 已停止Boss绕圈。");
                return;
            }
            if (command.Status)
            {
                Service.Chat.Print("[NewFolder3] " + (_hohBossOrbit?.Status ?? "绕圈实验已关闭"));
                return;
            }
            _hohBossOrbit ??= new(SetBossMovementOverride, MoveHohOrbit, StopHohOrbitInput,
                p => _hohOrbitGeometry.GroundPoint(p, .15f, .5f), IsHohOrbitSegmentSafe, ReportHohOrbit);
            if (!_hohBossOrbit.Start(ReadHohOrbitFrame(), command.Radius, command.Clockwise, DateTime.UtcNow, out var message))
            { Service.Chat.PrintError("[NewFolder3] " + message); return; }
            Service.Chat.Print("[NewFolder3] " + message + "；随时输入 /newfolder3 circle off 停止。");
        }
        catch (Exception error) { Service.Chat.PrintError("[NewFolder3] " + error.Message); }
    }

    private bool IsHohOrbitSegmentSafe(Vector3 from, Vector3 to)
    {
        _hohOrbitModuleActive ??= Service.PluginInterface.GetIpcSubscriber<bool>("BossMod.HasActiveModule");
        _hohOrbitPathSafe ??= Service.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>("BossMod.Hints.IsDashSafe");
        return _hohOrbitModuleActive.HasFunction && _hohOrbitModuleActive.InvokeFunc() &&
            _hohOrbitPathSafe.HasFunction && _hohOrbitPathSafe.InvokeFunc(from, to) &&
            CombatPositionNavigation.Sight(from, to, out _) == CombatSight.Clear;
    }

    private void MoveHohOrbit(Vector3 point)
    {
        // Only an accepted BMR handoff reaches here. Do not replace any other movement controller.
        _ddHost?.Context?.Navigator.CancelAll();
        _hohOrbitInput ??= new MovementInputController { IsAutoMove = false, Precision = .1f };
        _hohOrbitInput.DesiredPosition = point;
        _hohOrbitInput.Enabled = true;
    }
    private void StopHohOrbitInput() { if (_hohOrbitInput != null) _hohOrbitInput.Enabled = false; }
    private void ReportHohOrbit(string status)
    {
        Service.Log.Information("[HOH.BossCircle] " + status);
        Service.Chat.Print("[NewFolder3] " + status);
        _ddHost?.Context?.RecordDiagnostic?.Invoke("hoh-boss-circle", new { status });
    }
    private void UpdateHohBossOrbit()
    {
        if (_hohBossOrbit?.Enabled != true) return;
        try { _hohBossOrbit.Update(ReadHohOrbitFrame(), DateTime.UtcNow); }
        catch (Exception error)
        {
            _hohBossOrbit.Stop("绕圈状态读取失败，已停止");
            Service.Log.Error(error, "[HOH.BossCircle] Update failed");
        }
    }
    private void DisposeHohBossOrbit()
    {
        try { _hohBossOrbit?.Dispose(); }
        finally { _hohOrbitInput?.Dispose(); _hohOrbitInput = null; }
    }
}
