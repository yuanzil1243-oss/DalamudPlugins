using System.Collections;
using System.Reflection;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace NewFolder3;

internal static class Hoh53PostClearCombatTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 7, 24, 41, DateTimeKind.Utc);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static HohPetrificationState Finished()
    {
        var state = new HohPetrificationState(); state.BeginFloor(2, 43); Finish(state); return state;
    }
    private static void Finish(HohPetrificationState state)
    {
        state.ConfirmUse(Now.AddSeconds(-31));
        state.Observe(true, true, false, Now.AddSeconds(-30));
        state.Observe(false, false, true, Now);
    }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh53-finished-stone-keeps-existing-hate-but-rejects-new-pulls", () => {
                var s = Finished();
                Check(s.AllowsTarget(false, true) && s.AllowsTarget(true, true), "finish already engaged enemies with or without StoneCurse");
                Check(!s.AllowsTarget(false, false) && !s.AllowsTarget(true, false), "no voluntary pulls after completion");
            }),
            ("hoh53-active-stone-still-clears-only-petrified-enemies", () => {
                var s = Finished(); s.ConfirmUse(Now); s.Observe(true, true, false, Now);
                Check(s.Clearing && !s.StopAttackingAfterClear && s.AllowsTarget(true, false) && !s.AllowsTarget(false, true), "active clear priority preserved");
            }),
            ("hoh64-late-native-stone-state-cannot-restart-pulls-after-door-open", () => {
                var s = Finished(); s.Observe(true, true, true, Now.AddSeconds(.1));
                Check(!s.Clearing && s.StopAttackingAfterClear && !s.AllowsTarget(true), "late active status cannot restart optional pulls after passage opens");
            }),
            ("hoh53-finished-stone-with-door-closed-restores-normal-targeting", () => {
                var s = Finished(); s.Observe(false, false, false, Now.AddSeconds(1));
                Check(!s.StopAttackingAfterClear && s.AllowsTarget(false, false), "ordinary opening combat resumes when gate is closed");
            }),
            ("hoh53-new-floor-resets-post-clear-restrictions", () => {
                var s = Finished(); s.BeginFloor(2, 44);
                Check(!s.StopAttackingAfterClear && !s.Clearing && s.AllowsTarget(false), "no next-floor inherited output stop");
            }),
            ("hoh53-real-context-native-combat-resumes-output-with-empty-pt-list", () => {
                using var f = new Fixture(); f.InCombat = true;
                Check(f.Context.MobCombat.Engaged.Count == 0, "HoH does not populate the PT controller list");
                Check(f.Context.HohCombatInProgress && !f.Context.NormalCombatOutputSuppressed, "native combat must release the stone output stop");
            }),
            ("hoh53-real-context-target-of-player-covers-delayed-combat-flag", () => {
                using var f = new Fixture(); f.Objects.Add(f.Engaged);
                Check(!f.InCombat && f.Context.HohCombatInProgress && !f.Context.NormalCombatOutputSuppressed, "live engaged enemy covers delayed condition flag");
            }),
            ("hoh53-real-target-gate-allows-existing-hate-and-blocks-idle-mob", () => {
                using var f = new Fixture();
                Check(f.Context.IsNewCombatTargetAllowed(f.Engaged), "existing hate is an obligation after passage opens");
                Check(!f.Context.IsNewCombatTargetAllowed(f.Idle), "do not initiate another enemy");
            }),
            ("hoh53-enforcement-keeps-engaged-hard-and-soft-targets", () => {
                using var f = new Fixture(); f.Objects.Add(f.Engaged); f.Target = f.SoftTarget = f.Engaged;
                f.Context.EnforceWisdomTargetGate();
                Check(ReferenceEquals(f.Target, f.Engaged) && ReferenceEquals(f.SoftTarget, f.Engaged), "do not repeatedly clear an engaged target or disable its autoattack");
            }),
            ("hoh53-no-hate-after-clear-suppresses-output-and-forbids-new-targets", () => {
                using var f = new Fixture(); f.Objects.Add(f.Idle);
                Check(!f.Context.HohCombatInProgress && f.Context.NormalCombatOutputSuppressed &&
                    !f.Context.IsHohPetrificationTargetAllowed(f.Idle), "idle monsters are not a reason to resume attacks");
            }),
            ("hoh53-real-combat-state-selects-finish-before-passage", () => {
                using var f = new Fixture(); f.Objects.Add(f.Engaged);
                var snapshot = new ObjectiveArbiterSnapshot(false, false, false, false, true, true, false,
                    f.Context.HohCombatInProgress, true, false);
                Check(ObjectiveArbiter.Decide(snapshot).PrimaryObjective == FloorObjectiveKind.FinishCombatBeforePassage, "do not pretend existing combat ended");
                f.Objects.Clear();
                Check(ObjectiveArbiter.Decide(snapshot with { CombatInProgress = f.Context.HohCombatInProgress }).PrimaryObjective == FloorObjectiveKind.EnterPassage, "resume exit after last hostile is cleared");
            }),
            ("hoh53-output-restricts-again-after-combat-ends", () => {
                using var f = new Fixture(); f.InCombat = true; Check(!f.Context.NormalCombatOutputSuppressed, "finish combat");
                f.InCombat = false; Check(f.Context.NormalCombatOutputSuppressed, "do not start a new encounter");
            }),
            ("hoh53-pending-exploration-yields-to-existing-combat-and-resumes-after", () => {
                using var f = new Fixture(); f.Objects.Add(f.Engaged);
                var snapshot = new ObjectiveArbiterSnapshot(false, false, false, false, true, true, false,
                    f.Context.HohCombatInProgress, true, false, RequiredChestWork: true,
                    PassageFirst: f.Context.Duty.DungeonId == 2 && f.Context.HohPetrification.StopAttackingAfterClear && f.Context.HohCombatInProgress);
                Check(ObjectiveArbiter.Decide(snapshot).PrimaryObjective == FloorObjectiveKind.FinishCombatBeforePassage,
                    "remaining exploration cannot take priority over finishing engaged enemies");
                f.Objects.Clear();
                Check(ObjectiveArbiter.Decide(snapshot with { CombatInProgress = false, PassageFirst = false }).PrimaryObjective == FloorObjectiveKind.OpenPlannedChest,
                    "remaining work becomes eligible again when combat ends");
            }),
            ("hoh53-pt-ignores-stale-hoh-petrification-state", () => {
                using var f = new Fixture(4);
                Check(!f.Context.HohCombatInProgress && !f.Context.NormalCombatOutputSuppressed &&
                    f.Context.IsHohPetrificationTargetAllowed(f.Idle), "PT uses its existing combat and Wisdom logic");
            }),
            ("hoh53-palace-ignores-stale-hoh-petrification-state", () => {
                using var f = new Fixture(1);
                Check(!f.Context.HohCombatInProgress && !f.Context.NormalCombatOutputSuppressed &&
                    f.Context.IsHohPetrificationTargetAllowed(f.Idle), "palace must not acquire HoH stop gates");
            }),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context = new(new FsdSettings(), new DutyState(), new(DetailedMapRuntimePolicy.PalacePalOnly, null, null));
        internal bool InCombat;
        internal readonly List<IGameObject> Objects = new();
        internal readonly IBattleChara Engaged;
        internal readonly IBattleChara Idle;
        internal IGameObject? Target, SoftTarget;
        private readonly List<(PropertyInfo, object?)> _services = new();
        internal Fixture(uint dungeon = 2)
        {
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty, dungeon);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty, (byte)43);
            typeof(DutyState).GetProperty(nameof(DutyState.PassageOpen))!.SetValue(Context.Duty, true);
            Context.HohPetrification.BeginFloor(2, 43); Finish(Context.HohPetrification);
            var player = Fake<IPlayerCharacter>((m, _) => m.Name == "get_GameObjectId" ? 42UL : Default(m));
            Engaged = Enemy(42); Idle = Enemy(0);
            Service("Condition", Fake<ICondition>((m, a) => m.Name == "get_Item" && (ConditionFlag)a![0]! == ConditionFlag.InCombat ? InCombat : Default(m)));
            Service("GameObjects", Fake<IObjectTable>((m, _) => m.Name switch {
                "get_LocalPlayer" => player, "GetEnumerator" => ((IEnumerable<IGameObject>)Objects).GetEnumerator(), _ => Default(m) }));
            Service("TargetManager", Fake<ITargetManager>((m, a) => {
                switch (m.Name) {
                    case "get_Target": return Target; case "set_Target": Target = (IGameObject?)a![0]; return null;
                    case "get_SoftTarget": return SoftTarget; case "set_SoftTarget": SoftTarget = (IGameObject?)a![0]; return null;
                    default: return Default(m);
                }
            }));
        }
        private static IBattleChara Enemy(ulong target) => Fake<IBattleChara>((m, _) => m.Name switch {
            "get_TargetObjectId" => target, "get_GameObjectId" => 0UL, // bypass native Hater; exercise the existing target-of-player evidence
            "get_IsTargetable" => true, "get_CurrentHp" => 100U, "get_ObjectKind" => ObjectKind.BattleNpc,
            "get_SubKind" => (byte)BattleNpcSubKind.Combatant, _ => Default(m) });
        private void Service(string name, object value)
        {
            var property = typeof(RunContext).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Service")!
                .GetProperty(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            _services.Add((property, property.GetValue(null))); property.SetValue(null, value);
        }
        public void Dispose() { Context.Dispose(); foreach (var (p, v) in _services.AsEnumerable().Reverse()) p.SetValue(null, v); }
    }
    private static object? Default(MethodInfo method) => method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
    private static T Fake<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, Proxy>(); ((Proxy)(object)proxy).Handler = handler; return proxy;
    }
    public class Proxy : DispatchProxy
    {
        internal Func<MethodInfo, object?[]?, object?> Handler = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Handler(method!, args);
    }
}
