using System.Runtime.InteropServices;
using DeepDungeon.Fsd.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace NewFolder3;

/// <summary>Short window-local input pulse based on actual client inactivity timers.</summary>
internal sealed class AntiKickController
{
    private readonly Configuration configuration;
    private readonly IClientState client;
    private readonly ICondition conditions;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private readonly AntiKickPulseScheduler scheduler = new();
    private static readonly int[] ModifierKeys = [0x10, 0x11, 0x12, 0x5B, 0x5C];
    private nint pressedWindow;
    private long verifyAt, nextFailureLog, nextObservation;
    private AntiKickInputTimers before;

    public AntiKickController(Configuration configuration, IClientState client,
        ICondition conditions, IObjectTable objects, IPluginLog log)
    {
        this.configuration = configuration; this.client = client;
        this.conditions = conditions; this.objects = objects; this.log = log;
    }

    public unsafe void Update()
    {
        long now = Environment.TickCount64;
        if (!scheduler.Holding && now < nextObservation) return;
        nextObservation = now + 1000;
        try
        {
            bool eligible = IsEligible();
            var timers = eligible ? ReadTimers() : new AntiKickInputTimers(float.NaN, float.NaN, float.NaN);
            if (verifyAt != 0 && now >= verifyAt)
            {
                verifyAt = 0;
                if (eligible)
                {
                    bool reset = timers.ObservedResetSince(before);
                    Log($"[NewFolder3.AntiKick] input-timer-check before={before} after={timers} clientResetObserved={reset}; server kick prevention is not confirmed.", !reset);
                }
            }
            switch (scheduler.Update(now, eligible, timers, eligible && !scheduler.Holding && ModifierHeld()))
            {
                case AntiKickPulseCommand.Press:
                    var framework = Framework.Instance();
                    var window = framework == null || framework->GameWindow == null ? 0 : framework->GameWindow->WindowHandle;
                    before = timers;
                    // Record ownership before dispatch: even a timeout may have delivered key-down.
                    pressedWindow = window;
                    if (window == 0 || !SendKey(window, down: true))
                    {
                        ReleaseKey(); scheduler.CancelPulse();
                        Log("[NewFolder3.AntiKick] Game window/input unavailable; no confirmed input pulse. Retry is rate-limited.", true);
                    }
                    else Log($"[NewFolder3.AntiKick] input-pulse-down timers={before}; awaiting release and timer observation.");
                    break;
                case AntiKickPulseCommand.Release:
                    ReleaseKey();
                    verifyAt = eligible ? now + 2000 : 0;
                    break;
            }
            if (!eligible) verifyAt = 0;
        }
        catch (Exception error)
        {
            ReleaseKey(); scheduler.CancelPulse(); verifyAt = 0;
            if (now >= nextFailureLog)
            {
                nextFailureLog = now + 60_000;
                Log($"[NewFolder3.AntiKick] Input observation/pulse failed: {error.Message}", true);
            }
        }
    }

    private unsafe bool IsEligible()
    {
        if (!configuration.AntiKickEnabled || !client.IsLoggedIn ||
            !conditions[ConditionFlag.InDeepDungeon] ||
            !(conditions[ConditionFlag.BoundByDuty] || conditions[ConditionFlag.BoundByDuty56] ||
                conditions[ConditionFlag.BoundByDuty95]) ||
            conditions[ConditionFlag.BetweenAreas] || conditions[ConditionFlag.BetweenAreas51] ||
            conditions[ConditionFlag.WatchingCutscene] || conditions[ConditionFlag.WatchingCutscene78] ||
            conditions[ConditionFlag.OccupiedInCutSceneEvent] || conditions[ConditionFlag.Unconscious] ||
            objects.LocalPlayer is not { IsDead: false }) return false;
        var events = EventFramework.Instance();
        var dungeon = events == null ? null : events->GetInstanceContentDeepDungeon();
        return dungeon != null && AntiDisplacementV2.DungeonScope.Matches(client.TerritoryType,
            dungeon->DeepDungeonId, dungeon->Floor);
    }

    private static unsafe AntiKickInputTimers ReadTimers()
    {
        var ui = UIModule.Instance();
        var timers = ui == null ? null : ui->GetInputTimerModule();
        return timers == null ? new(float.NaN, float.NaN, float.NaN) :
            new(timers->AfkTimer, timers->ContentInputTimer, timers->InputTimer);
    }

    private static bool ModifierHeld() => ModifierKeys
        .Any(key => (GetAsyncKeyState(key) & 0x8000) != 0);

    private static bool SendKey(nint window, bool down)
    {
        // Window-local Ctrl only: no global SendInput, movement, action, or window search.
        // The key remains down across framework frames; same-frame down/up can be missed.
        const uint keyDown = 0x100, keyUp = 0x101, abortIfHung = 0x2;
        nint keyBits = down ? 1 : unchecked((nint)0xC0000001);
        return SendMessageTimeout(window, down ? keyDown : keyUp, 0xA2, keyBits,
            abortIfHung, 100, out _) != 0;
    }

    private void ReleaseKey()
    {
        nint window = pressedWindow;
        if (window == 0) return;
        pressedWindow = 0;
        try
        {
            if (!SendKey(window, down: false))
                Log("[NewFolder3.AntiKick] Synchronous key-up timed out; posting key-up to the same game window.", true);
        }
        catch { }
        finally
        {
            // Redundant release is harmless and covers synchronous delivery failure.
            try { PostMessage(window, 0x101, 0xA2, unchecked((nint)0xC0000001)); } catch { }
        }
    }

    public void DrawSettings()
    {
        bool enabled = configuration.AntiKickEnabled;
        if (ImGui.Checkbox("防止深宫无操作踢出##antiKick", ref enabled))
        {
            configuration.AntiKickEnabled = enabled;
            nextObservation = 0;
            if (!enabled) { ReleaseKey(); scheduler.Reset(); verifyAt = 0; }
            configuration.Save(configuration.Fsd);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("勾选后在地宫内生效，不需启动 FSD；长时间无操作时轻按 Ctrl 尝试防踢。\n出本、过图或死亡时停用。");
    }

    private void Log(string message, bool warning = false)
    { try { if (warning) log.Warning(message); else log.Info(message); } catch { } }

    public void Dispose() { ReleaseKey(); scheduler.Reset(); verifyAt = 0; }

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    private static extern nint SendMessageTimeout(nint window, uint message, nuint wParam,
        nint lParam, uint flags, uint timeoutMs, out nuint result);
    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
}
