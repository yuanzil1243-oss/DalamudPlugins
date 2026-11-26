// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using NewFolder3;

namespace ForcedMovementLab;

internal static class TriggerCounterIntegrationChecks
{
    internal static unsafe void Run(Action<bool, string> check)
    {
        var config = new Configuration { ForcedMovementEnabled = true };
        var host = new FakeHost();
        host.Flags.Add(ConditionFlag.BoundByDuty);
        var interop = new FakeInterop();
        EventFramework.Allocate();
        try
        {
            using var controller = new ForcedMovementController(config, host, host, host, new FakeScanner(), interop, new FakeLog());
            controller.Update();
            var callback = Fixture.Callback(interop.Detour!);
            Fixture.OriginalValue = 0;
            Fixture.OriginalCalls = 0;
            // Movement before the denied permission is not evidence of bypassing a restriction.
            host.LocalPlayer!.Position += new Vector3(.2f, 0, 0);
            check(callback(0x2000, 96, 37, 41) == 1 && Fixture.OriginalCalls == 1 && Fixture.Last == (0x2000, 96u, 37, 41),
                "counter-controller-original-once-and-exclusions-preserved");
            controller.Update();
            check(config.TriggerCounters.Count(FeatureTriggerKind.ForcedMovement) == 0, "counter-controller-preexisting-movement-not-counted");
            host.LocalPlayer.Position += new Vector3(.2f, 0, 0);
            controller.Update();
            check(config.TriggerCounters.Count(FeatureTriggerKind.ForcedMovement) == 1, "counter-controller-real-release-and-motion-counted");
            for (int i = 0; i < 20; i++)
            {
                callback(0x2000, 96, 0, 0);
                host.LocalPlayer.Position += new Vector3(.01f, 0, 0);
                controller.Update();
            }
            check(config.TriggerCounters.Count(FeatureTriggerKind.ForcedMovement) == 1, "counter-controller-repeated-frame-calls-not-counted");
            config.ForcedMovementEnabled = false;
            controller.Update();
            host.LocalPlayer.Position += new Vector3(.2f, 0, 0);
            controller.Update();
            check(config.TriggerCounters.Count(FeatureTriggerKind.ForcedMovement) == 1, "counter-controller-off-retains-total");
            config.ForcedMovementEnabled = true;
            controller.Update();
            callback(0x2000, 96, 0, 0);
            EventFramework.Dungeon->Floor = 2;
            host.LocalPlayer.Position += new Vector3(.2f, 0, 0);
            controller.Update();
            check(config.TriggerCounters.Count(FeatureTriggerKind.ForcedMovement) == 1, "counter-controller-new-floor-discards-old-evidence");

            ImGui.CounterDraws.Clear();
            controller.DrawSettings();
            check(ImGui.CounterDraws.SequenceEqual(new[] { "SameLine", "TextDisabled:触发次数：1" }), "counter-ui-disabled-text-on-same-line");
        }
        finally { EventFramework.Free(); }

        int observations = 0;
        var gate = new MovementGate();
        var ip = new FakeInterop();
        using var backend = new NativeBackend(new FakeScanner(), ip, gate, _ => true,
            (_, _, _, _, _) => { observations++; throw new InvalidOperationException("fixture observer error"); });
        check(backend.Prepare(out _), "counter-native-observer-fixture-ready");
        gate.Publish(new(Environment.TickCount64, 561, 7, 0x123456, 0x2000, Vector3.Zero, true, "valid"));
        gate.Request(true);
        var native = Fixture.Callback(ip.Detour!);
        Fixture.OriginalValue = 0;
        Fixture.OriginalCalls = 0;
        check(native(0x2000, 96, 3, 4) == 1 && observations == 1 && Fixture.OriginalCalls == 1,
            "counter-observer-error-cannot-change-native-result");
        // Exercise the production Probe guard through a managed callback; never execute synthetic memory.
        var probing = typeof(NativeBackend).GetField("probing", BindingFlags.NonPublic | BindingFlags.Static)!;
        try
        {
            probing.SetValue(null, true);
            check(native(0x2000, 96, 3, 4) == 1 && observations == 1 && Fixture.OriginalCalls == 2,
                "counter-native-probe-path-excluded-from-observation");
        }
        finally { probing.SetValue(null, false); }
    }
}
