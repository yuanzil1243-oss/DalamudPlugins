// SPDX-License-Identifier: AGPL-3.0-or-later
using DeepDungeon.Fsd.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using ForcedMovementLab;

namespace NewFolder3;

/// <summary>Independent normal-input permission hook; never starts navigation or supplies directions.</summary>
internal sealed class ForcedMovementController : IDisposable
{
    private readonly Configuration configuration;
    private readonly GameSceneReader reader;
    private readonly ICondition conditions;
    private readonly IPluginLog log;
    private readonly MovementGate gate = new();
    private readonly NativeBackend native;
    private readonly ForcedMovementTriggerTracker? triggerTracker;
    private bool requested, faulted, disposed;
    private string? error;

    public ForcedMovementController(Configuration configuration, IClientState client, ICondition conditions,
        IObjectTable objects, ISigScanner scanner, IGameInteropProvider interop, IPluginLog log)
    {
        this.configuration = configuration;
        this.conditions = conditions;
        this.log = log;
        reader = new(client, objects, conditions);
        // Constructing the backend does not scan, create or enable a hook.
        triggerTracker = configuration.TriggerCounters is { } counters ? new(counters) : null;
        native = new(scanner, interop, gate, ValidateLive, ObserveCounterPermission);
    }

    public void Update()
    {
        if (disposed) return;
        try
        {
            SetRequested(configuration.ForcedMovementEnabled);
            if (!requested || faulted) { Suspend("已關閉或故障"); return; }
            long now = Environment.TickCount64;
            var scene = ReadScene(now);
            if (!ForcedMovementScope.Allows(scene, now)) { Suspend(scene.Reason); return; }
            gate.Publish(scene);
            if (!native.HookEnabled)
            {
                if (!native.Prepare(out var reason)) { Fault(reason); return; }
                log.Info("[NewFolder3.ForcedMovement] Enabled: verified normal movement permissions; dungeon=" +
                    scene.Dungeon + "; floor=" + scene.Floor + ".");
            }
            else if (gate.Status(now).Fault is { } reason) Fault(reason);
            if (!faulted) triggerTracker?.Position(CounterIdentity(scene), scene.Position, now);
        }
        catch (Exception ex) { Fault(ex.GetBaseException().Message); }
    }

    private void SetRequested(bool enabled)
    {
        if (requested == enabled) return;
        requested = enabled;
        faulted = false;
        error = null;
        gate.Request(enabled);
    }

    private unsafe Scene ReadScene(long now)
    {
        var scene = reader.Read(now);
        if (!scene.Eligible) return scene;
        if (!(conditions[ConditionFlag.BoundByDuty] || conditions[ConditionFlag.BoundByDuty56] ||
            conditions[ConditionFlag.BoundByDuty95])) return scene with { Eligible = false, Reason = "不在地宮副本內" };
        var events = EventFramework.Instance();
        var dungeon = events == null ? null : events->GetInstanceContentDeepDungeon();
        if (dungeon == null) return scene with { Eligible = false, Reason = "未讀到地宮現場" };
        scene = scene with { Dungeon = dungeon->DeepDungeonId, Floor = dungeon->Floor,
            Thread = Environment.CurrentManagedThreadId };
        return ForcedMovementScope.Allows(scene, now) ? scene :
            scene with { Eligible = false, Reason = "地宮、區域或樓層不符" };
    }

    private bool ValidateLive(Scene cached)
    {
        // No object/native scene reads on another thread, and no cached authorization after leaving a duty.
        int thread = Environment.CurrentManagedThreadId;
        if (thread != cached.Thread) return false;
        long now = Environment.TickCount64;
        return ForcedMovementScope.Matches(cached, ReadScene(now), now, thread);
    }

    private static TriggerScene CounterIdentity(Scene scene) =>
        new(scene.ActorId, (long)scene.ActorAddress, scene.Territory, scene.Dungeon, scene.Floor);

    private void ObserveCounterPermission(Scene scene, uint id, byte original, byte returned, long now)
    {
        if (triggerTracker == null || !gate.Status(now).Active || scene.Thread != Environment.CurrentManagedThreadId) return;
        var live = ReadScene(now);
        if (ForcedMovementScope.Matches(scene, live, now, Environment.CurrentManagedThreadId))
            triggerTracker.Permission(CounterIdentity(live), live.Position, id, original, returned, now);
    }

    private void Suspend(string reason)
    {
        triggerTracker?.Reset();
        gate.Suspend(reason, Environment.TickCount64);
        if (!native.HookEnabled) return;
        native.Disable();
        log.Info("[NewFolder3.ForcedMovement] Suspended; original movement permissions restored.");
    }

    private void Fault(string reason)
    {
        bool first = !faulted;
        faulted = true;
        error = reason;
        gate.Fail(reason);
        triggerTracker?.Reset();
        try { native.Disable(); }
        catch (Exception ex) { error += "; " + ex.GetBaseException().Message; }
        if (first) log.Warning("[NewFolder3.ForcedMovement] " + error);
    }

    public void RevokeForHostError()
    {
        if (requested) Fault("FSD 主循環異常，強制移動已停用；關閉再開啟可重新檢查。");
    }

    public void DrawSettings()
    {
        bool enabled = configuration.ForcedMovementEnabled;
        if (ImGui.Checkbox("強制移動##forcedMovement", ref enabled))
        {
            configuration.ForcedMovementEnabled = enabled;
            SetRequested(enabled);
            if (!enabled) { try { Suspend("已關閉"); } catch (Exception ex) { Fault(ex.Message); } }
            configuration.Save(configuration.Fsd);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("僅地宮內，嘗試在眩暈、止步等限制下保留正常移動。\n出本、過圖或死亡時停用；不會自行移動。");
        TriggerCounterUi.Draw(configuration.TriggerCounters, FeatureTriggerKind.ForcedMovement);
        if (error != null) ImGui.TextWrapped("強制移動未啟用：" + error);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        requested = false;
        gate.Request(false);
        gate.Suspend("已卸載", Environment.TickCount64);
        native.Dispose();
    }
}
