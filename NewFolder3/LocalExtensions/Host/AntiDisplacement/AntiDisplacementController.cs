// SPDX-License-Identifier: GPL-3.0-only
using AntiDisplacementV2;
using DeepDungeon.Fsd.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace NewFolder3;

/// <summary>Owns an independent hook for verified deep-dungeon scenes, without requiring FSD.</summary>
internal sealed class AntiDisplacementController(Configuration configuration, IClientState client,
    ICondition conditions, IObjectTable objects, ISigScanner scanner, IGameInteropProvider interop,
    IPluginLog log) : IDisposable
{
    private sealed record Frame(Scene Scene, long At, bool Enabled);
    private Frame frame = new(default, 0, false);
    private NativeMotion? native;
    private volatile bool requested, disposed, faulted;
    private string? error;
    private long suppressed;
    private readonly DisplacementTriggerTracker? triggerTracker = configuration.TriggerCounters is { } counters ? new(counters) : null;

    public void Update()
    {
        if (disposed) return;
        try
        {
            bool enabled = configuration.AntiDisplacementEnabled;
            if (requested != enabled)
            {
                requested = enabled;
                if (!enabled) { faulted = false; error = null; }
            }
            var scene = requested && !faulted ? ReadScene() : default;
            if (!KnockbackProtectionPolicy.Allows(scene)) { Suspend(); return; }
            native ??= new(scanner, interop, Decide);
            Volatile.Write(ref frame, new(scene, Environment.TickCount64, true));
            if (!native.Enabled)
            {
                native.Enable();
                if (!native.Enabled) throw new InvalidOperationException("防擊退 Hook 未能啟用。");
                log.Info($"[NewFolder3.AntiDisplacement] Enabled: dungeon={scene.Dungeon}; floor={scene.Floor}; {native.Verification}; {native.Backend}.");
            }
        }
        catch (Exception ex) { Fault(ex.GetBaseException().Message); }
    }

    private unsafe Scene ReadScene()
    {
        var player = objects.LocalPlayer;
        if (!client.IsLoggedIn || player == null || player.IsDead ||
            conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51] ||
            conditions[ConditionFlag.WatchingCutscene] || conditions[ConditionFlag.WatchingCutscene78] ||
            conditions[ConditionFlag.OccupiedInCutSceneEvent] || conditions[ConditionFlag.Unconscious] ||
            conditions[ConditionFlag.Mounted] || conditions[ConditionFlag.RidingPillion] ||
            conditions[ConditionFlag.InFlight] || conditions[ConditionFlag.Swimming] || conditions[ConditionFlag.Diving]) return default;
        if (!(conditions[ConditionFlag.BoundByDuty] || conditions[ConditionFlag.BoundByDuty56] ||
            conditions[ConditionFlag.BoundByDuty95])) return default;
        var events = EventFramework.Instance();
        var dungeon = events == null ? null : events->GetInstanceContentDeepDungeon();
        return dungeon == null ? default : new(player.Address, player.EntityId, client.TerritoryType,
            dungeon->DeepDungeonId, dungeon->Floor, Environment.CurrentManagedThreadId, true);
    }

    private Decision Decide(Motion motion)
    {
        var current = Volatile.Read(ref frame);
        int thread = Environment.CurrentManagedThreadId;
        bool enabled = requested && !disposed && !faulted && current.Enabled;
        var live = enabled && thread == current.Scene.Thread && KnockbackProtectionPolicy.Allows(current.Scene)
            ? ReadScene() : default;
        var decision = KnockbackProtectionPolicy.Decide(enabled, current.Scene, live,
            current.At, Environment.TickCount64, thread, motion);
        if (decision.Suppress)
        {
            Interlocked.Increment(ref suppressed);
            try
            {
                triggerTracker?.Suppressed(new(live.EntityId, (long)live.Actor, live.Territory, live.Dungeon, live.Floor),
                    motion.Angle, motion.Distance, motion.Duration, motion.NativeMode, motion.Flags, Environment.TickCount64);
            }
            catch { /* Statistics never turn suppression into a pass-through. */ }
        }
        return decision;
    }

    private void Suspend()
    {
        triggerTracker?.Reset();
        Volatile.Write(ref frame, new(default, 0, false));
        if (native?.Enabled == true)
        {
            native.Disable();
            log.Info($"[NewFolder3.AntiDisplacement] Suspended; native requests pass through; blocked={Interlocked.Read(ref suppressed)}.");
        }
    }

    private void Fault(string reason)
    {
        bool first = !faulted;
        faulted = true;
        error = reason;
        Volatile.Write(ref frame, new(default, 0, false));
        try { native?.Disable(); }
        catch (Exception ex) { error += "; " + ex.GetBaseException().Message; }
        if (first) log.Warning("[NewFolder3.AntiDisplacement] " + error);
    }

    public void RevokeForHostError()
    {
        if (requested) Fault("FSD 主循環異常，防擊退已停用；關閉再開啟可重新檢查。");
    }

    public void DrawSettings()
    {
        bool enabled = configuration.AntiDisplacementEnabled;
        if (ImGui.Checkbox("防擊退／吸引##antiDisplacement", ref enabled))
        {
            configuration.AntiDisplacementEnabled = enabled;
            requested = enabled;
            faulted = false;
            error = null;
            if (!enabled) { try { Suspend(); } catch (Exception ex) { Fault(ex.Message); } }
            configuration.Save(configuration.Fsd);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾選後進入地宮即嘗試阻止擊退與吸引，不需啟動 FSD。\n出本、過圖或死亡時停用。");
        TriggerCounterUi.Draw(configuration.TriggerCounters, FeatureTriggerKind.AntiDisplacement);
        if (error != null) ImGui.TextWrapped("防擊退未啟用：" + error);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        requested = false;
        Volatile.Write(ref frame, new(default, 0, false));
        try { native?.Dispose(); }
        finally { native = null; }
    }
}
