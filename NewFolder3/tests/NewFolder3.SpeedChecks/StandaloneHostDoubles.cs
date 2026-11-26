// Test-only host services. No game, native hooks, executable memory, or input dispatch.
using System.Runtime.InteropServices;

namespace NewFolder3
{
    internal sealed class Configuration
    {
        public bool LocalSpeedEnabled { get; set; }
        public float LocalSpeedMultiplier { get; set; } = 2;
        public object Fsd { get; } = new();
        public void Save(object _) { }
    }
}
namespace Dalamud.Game.ClientState.Conditions
{
    public enum ConditionFlag
    {
        BoundByDuty, BoundByDuty56, BoundByDuty95, InDeepDungeon, BetweenAreas, BetweenAreas51,
        WatchingCutscene, WatchingCutscene78, OccupiedInCutSceneEvent, Unconscious,
        Mounted, RidingPillion, InFlight, Swimming, Diving, Jumping, Jumping61
    }
}
namespace Dalamud.Plugin.Services
{
    using Dalamud.Game.ClientState.Conditions;
    public interface IClientState { uint TerritoryType { get; } bool IsLoggedIn { get; } }
    public interface ICondition { bool this[ConditionFlag flag] { get; } }
    public interface IObjectTable { StandalonePlayer? LocalPlayer { get; } }
    public interface IPluginLog { void Warning(string message); }
    public sealed class StandalonePlayer { public bool IsDead { get; set; } public bool IsCasting { get; set; } }
    public sealed class StandaloneHost : IClientState, ICondition, IObjectTable, IPluginLog
    {
        public uint TerritoryType { get; set; } = 770;
        public bool IsLoggedIn { get; set; } = true;
        public StandalonePlayer? LocalPlayer { get; set; } = new();
        public HashSet<ConditionFlag> Flags { get; } = [ConditionFlag.BoundByDuty, ConditionFlag.InDeepDungeon];
        public bool this[ConditionFlag flag] => Flags.Contains(flag);
        public void Warning(string message) { }
    }
}
namespace FFXIVClientStructs.FFXIV.Client.Game.Event
{
    public unsafe struct StandaloneDungeon { public uint DeepDungeonId; public int Floor; }
    public unsafe struct EventFramework
    {
        private static EventFramework* instance;
        public static StandaloneDungeon* Dungeon;
        public static bool Available;
        public static EventFramework* Instance() => Available ? instance : null;
        public StandaloneDungeon* GetInstanceContentDeepDungeon() => Available ? Dungeon : null;
        public static void Allocate()
        {
            instance = (EventFramework*)NativeMemory.AllocZeroed((nuint)sizeof(EventFramework));
            Dungeon = (StandaloneDungeon*)NativeMemory.AllocZeroed((nuint)sizeof(StandaloneDungeon));
            *Dungeon = new() { DeepDungeonId = 2, Floor = 1 }; Available = true;
        }
        public static void Free()
        { Available = false; NativeMemory.Free(instance); NativeMemory.Free(Dungeon); instance = null; Dungeon = null; }
    }
}
namespace Dalamud.Bindings.ImGui
{
    public static class ImGui
    {
        public static List<string> Layout { get; } = [];
        public static void Spacing() { }
        public static void Separator() { }
        public static bool Checkbox(string name, ref bool value) { Layout.Add("checkbox:" + name); return false; }
        public static bool IsItemHovered() => false;
        public static void SetTooltip(string text) { }
        public static void SameLine() => Layout.Add("same-line");
        public static void SetNextItemWidth(float width) { }
        public static bool InputFloat(string name, ref float value, float step, float fast, string format)
        { Layout.Add("input:" + name); return false; }
    }
}
