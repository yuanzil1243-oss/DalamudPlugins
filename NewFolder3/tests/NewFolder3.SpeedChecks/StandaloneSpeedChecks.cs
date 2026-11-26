using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using MoveSpeedLab;
using NewFolder3;

internal static unsafe class StandaloneSpeedChecks
{
    private static void Require(bool value) { if (!value) throw new Exception("Standalone speed regression"); }
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static float OriginalTarget(WalkController* _) => 6;
    private static void OriginalUpdate(WalkController* _) { }
    private sealed class NoNativeAccess : ISigScanner, IGameInteropProvider
    {
        public IEnumerable<nint> ScanAllText(string _) => throw new Exception("Unexpected native scan");
        public nint ScanText(string _) => throw new Exception("Unexpected native scan");
        public nint GetStaticAddressFromSig(string _) => throw new Exception("Unexpected native scan");
        public Dalamud.Hooking.Hook<T> HookFromAddress<T>(nint _, T detour) where T : Delegate =>
            throw new Exception("Unexpected native hook");
    }
    private static void InjectManagedHooks(NativeSpeedBackend backend)
    {
        foreach (var pair in new[] { ("hook", nameof(OriginalTarget)), ("updateHook", nameof(OriginalUpdate)) })
        {
            var field = typeof(NativeSpeedBackend).GetField(pair.Item1, Private)!;
            var method = typeof(StandaloneSpeedChecks).GetMethod(pair.Item2, BindingFlags.Static | BindingFlags.NonPublic)!;
            var hook = Activator.CreateInstance(field.FieldType, method.CreateDelegate(field.FieldType.GenericTypeArguments[0]))!;
            hook.GetType().GetMethod("Enable")!.Invoke(hook, null); field.SetValue(backend, hook);
        }
    }
    internal static void Run(Action<string, Action> check)
    {
        EventFramework.Allocate();
        try
        {
            var config = new Configuration(); var host = new StandaloneHost(); var noNative = new NoNativeAccess();
            using var controller = new LocalSpeedController(config, host, host, host, noNative, noNative, host);
            var backend = (NativeSpeedBackend)typeof(LocalSpeedController).GetField("backend", Private)!.GetValue(controller)!;
            var session = (SpeedSession)typeof(LocalSpeedController).GetField("session", Private)!.GetValue(controller)!;
            var reader = new GameContextReader(host, host, host);
            void Warm()
            {
                controller.Update(); session.ResetContext();
                session.Evaluate(reader.Read(), config.LocalSpeedMultiplier, true, Environment.TickCount64 - 600);
                controller.Update();
            }
            check("standalone-default-off-no-fsd-or-native-access", () => { controller.Update(); Require(backend.Released); });
            InjectManagedHooks(backend);
            check("standalone-enter-normal-dungeon-without-fsd-grants-speed", () =>
            { config.LocalSpeedEnabled = true; Warm(); Require(!backend.Released && session.Enabled); });
            check("standalone-boss-floor-releases-and-next-normal-resumes", () =>
            {
                EventFramework.Dungeon->Floor = 10; controller.Update(); Require(backend.Released && config.LocalSpeedEnabled);
                host.TerritoryType = 771; EventFramework.Dungeon->Floor = 11; Warm(); Require(!backend.Released);
            });
            check("standalone-final-reward-floor-without-fsd-grants-speed", () =>
            { host.TerritoryType = 785; EventFramework.Dungeon->Floor = 100; Warm(); Require(!backend.Released); });
            foreach (var flag in new[] { ConditionFlag.BetweenAreas, ConditionFlag.WatchingCutscene, ConditionFlag.Unconscious })
                check("standalone-transition-revokes-" + flag, () =>
                {
                    host.Flags.Add(flag); controller.Update(); Require(backend.Released);
                    host.Flags.Remove(flag); Warm(); Require(!backend.Released);
                });
            check("standalone-death-releases-and-return-resumes", () =>
            {
                host.LocalPlayer!.IsDead = true; controller.Update(); Require(backend.Released);
                host.LocalPlayer.IsDead = false; Warm(); Require(!backend.Released);
            });
            check("standalone-exit-or-other-duty-releases-with-switch-retained", () =>
            {
                host.TerritoryType = 129; controller.Update(); Require(backend.Released && config.LocalSpeedEnabled);
                host.TerritoryType = 785; host.Flags.Remove(ConditionFlag.BoundByDuty); controller.Update(); Require(backend.Released);
                host.Flags.Add(ConditionFlag.BoundByDuty); Warm(); Require(!backend.Released);
            });
            check("standalone-missing-native-scene-revokes", () =>
            {
                EventFramework.Available = false; controller.Update(); Require(backend.Released);
                EventFramework.Available = true; Warm(); Require(!backend.Released);
            });
            check("standalone-master-off-releases", () =>
            { config.LocalSpeedEnabled = false; controller.Update(); Require(backend.Released); });
            check("standalone-ui-switch-and-multiplier-share-row", () =>
            {
                var layout = Dalamud.Bindings.ImGui.ImGui.Layout; layout.Clear(); controller.DrawSettings();
                Require(layout.SequenceEqual(new[] { "checkbox:移速調整##localSpeed", "same-line", "input:倍率##localSpeedMultiplier" }));
            });
        }
        finally { EventFramework.Free(); }
    }
}
