using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using NewFolder3;

namespace ForcedMovementLab
{
    internal static class StandaloneAntiKickChecks
    {
        internal static unsafe void Run(Action<bool, string> check)
        {
            EventFramework.Allocate();
            try
            {
                var config = new Configuration(); var host = new FakeHost();
                var controller = new AntiKickController(config, host, host, host, new FakeLog());
                var eligible = typeof(AntiKickController).GetMethod("IsEligible", BindingFlags.Instance | BindingFlags.NonPublic)!;
                bool Allowed() => (bool)eligible.Invoke(controller, null)!;
                host.Flags.Add(ConditionFlag.BoundByDuty); host.Flags.Add(ConditionFlag.InDeepDungeon);
                check(!Allowed(), "antikick-default-off-without-fsd");
                config.AntiKickEnabled = true;
                foreach (var (territory, dungeon, floor) in new[] { (561u, 1u, 1), (607u, 1u, 200),
                    (770u, 2u, 1), (785u, 2u, 100), (1099u, 3u, 1), (1108u, 3u, 100),
                    (1281u, 4u, 1), (1290u, 4u, 100) })
                {
                    host.TerritoryType = territory; EventFramework.Dungeon->DeepDungeonId = dungeon;
                    EventFramework.Dungeon->Floor = floor;
                    check(Allowed(), $"antikick-independent-dungeon-{dungeon}-floor-{floor}");
                }
                foreach (var flag in new[] { ConditionFlag.BetweenAreas, ConditionFlag.BetweenAreas51,
                    ConditionFlag.WatchingCutscene, ConditionFlag.OccupiedInCutSceneEvent, ConditionFlag.Unconscious })
                {
                    host.Flags.Add(flag); check(!Allowed(), "antikick-transition-denied-" + flag);
                    host.Flags.Remove(flag); check(Allowed(), "antikick-transition-return-" + flag);
                }
                host.LocalPlayer!.IsDead = true; check(!Allowed(), "antikick-death-denied"); host.LocalPlayer.IsDead = false;
                host.IsLoggedIn = false; check(!Allowed(), "antikick-logout-denied"); host.IsLoggedIn = true;
                host.TerritoryType = 1; check(!Allowed(), "antikick-non-dungeon-duty-denied"); host.TerritoryType = 1290;
                EventFramework.Available = false; check(!Allowed(), "antikick-missing-native-controller-denied"); EventFramework.Available = true;
                EventFramework.Dungeon->Floor = 1; check(!Allowed(), "antikick-native-floor-territory-mismatch-denied"); EventFramework.Dungeon->Floor = 100;
                host.Flags.Remove(ConditionFlag.BoundByDuty); check(!Allowed(), "antikick-duty-exit-denied"); host.Flags.Add(ConditionFlag.BoundByDuty);
                config.AntiKickEnabled = false; check(!Allowed(), "antikick-master-off-denied");
                controller.Dispose();
            }
            finally { EventFramework.Free(); }
        }
    }
}
// Compile-time native API stand-ins; scope tests never call these or dispatch input.
namespace FFXIVClientStructs.FFXIV.Client.System.Framework
{
    internal unsafe struct Framework
    {
        internal static Framework* Instance() => null;
        internal GameWindow* GameWindow;
    }
    internal struct GameWindow { internal nint WindowHandle; }
}
namespace FFXIVClientStructs.FFXIV.Client.UI
{
    internal unsafe struct UIModule
    {
        internal static UIModule* Instance() => null;
        internal InputTimerModule* GetInputTimerModule() => null;
    }
    internal struct InputTimerModule { internal float AfkTimer, ContentInputTimer, InputTimer; }
}
