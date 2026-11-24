// SPDX-License-Identifier: GPL-3.0-only
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using MoveSpeedLab;

namespace NewFolder3;

/// <summary>Host-only integration of the tested 0.1.0.2 policy. No FSD movement changes.</summary>
internal sealed class LocalSpeedController : IDisposable
{
    private readonly Configuration configuration;
    private readonly Func<bool> runActive;
    private readonly IPluginLog log;
    private readonly GameContextReader reader;
    private readonly NativeSpeedBackend backend;
    private readonly SpeedSession session = new();
    private bool requested;

    public LocalSpeedController(Configuration configuration, Func<bool> runActive,
        IClientState client, ICondition conditions, IObjectTable objects,
        ISigScanner scanner, IGameInteropProvider interop, IPluginLog log)
    {
        this.configuration = configuration;
        this.runActive = runActive;
        this.log = log;
        reader = new(client, conditions, objects);
        backend = new(scanner, interop, ReadLiveContext);
    }

    private SpeedContext ReadLiveContext()
    {
        if (!configuration.LocalSpeedEnabled || !runActive() || session.FaultLatched) return default;
        return reader.Read();
    }

    public void Update()
    {
        try
        {
            if (requested != configuration.LocalSpeedEnabled)
            {
                requested = configuration.LocalSpeedEnabled;
                if (requested) session.Enable();
                else session.Disable();
            }
            if (backend.RuntimeError is { } error) Fault(error);
            if (!requested || session.FaultLatched || !runActive())
            {
                backend.Release();
                session.ResetContext();
                return;
            }
            long now = Environment.TickCount64;
            var context = ReadLiveContext();
            // Enabling is the user's opt-in; the existing host warning and tooltip require other modifiers off.
            var decision = session.Evaluate(context, configuration.LocalSpeedMultiplier, true, now, runActive());
            if (decision.Apply && !backend.Initialized && !backend.TryInitialize(out var failure))
            {
                Fault(failure);
                return;
            }
            backend.Publish(decision, context, now);
        }
        catch (Exception error) { Fault(error.Message); }
    }

    private void Fault(string error)
    {
        backend.Release();
        if (!session.FaultLatched) log.Warning("[NewFolder3.LocalSpeed] " + error);
        session.Fault(error);
    }

    public void ReleaseIfStopped()
    {
        if (!runActive()) { backend.Release(); session.ResetContext(); }
    }

    public void RevokeForHostError() => Fault("FSD 主循環發生異常；已撤銷本地移速，請檢查日誌。");

    public void DrawSettings()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("內置移速");
        bool enabled = configuration.LocalSpeedEnabled;
        if (ImGui.Checkbox("啟用##localSpeed", ref enabled))
        {
            configuration.LocalSpeedEnabled = enabled;
            if (!enabled) backend.Release();
            configuration.Save(configuration.Fsd);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(session.Error ?? "僅在 FSD 運行中的普通層及最終獎勵層生效；Boss／副本外／停止時撤銷。使用前關閉獨立移速實驗室及其他移速修正。接口故障須處理後重新載入插件。");
        float multiplier = configuration.LocalSpeedMultiplier;
        ImGui.SetNextItemWidth(180);
        if (ImGui.InputFloat("移速調整##localSpeedMultiplier", ref multiplier, 0.1f, 0.5f, "%.2f"))
        {
            backend.Release();
            configuration.LocalSpeedMultiplier = float.IsFinite(multiplier)
                ? Math.Clamp(multiplier, SpeedPolicy.MinimumMultiplier, SpeedPolicy.MaximumMultiplier) : 1f;
            configuration.Save(configuration.Fsd);
        }
    }

    public void Dispose() => backend.Dispose();
}
