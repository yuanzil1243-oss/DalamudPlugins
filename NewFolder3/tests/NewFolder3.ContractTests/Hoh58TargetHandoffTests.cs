using System.Reflection;
using System.Collections;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace NewFolder3;

internal static class Hoh58TargetHandoffTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 11, 0, 0, DateTimeKind.Utc);
    private static readonly HohCombatCandidate A = new(101, true, true, true, true, true, 0, 100, 4);
    private static readonly HohCombatCandidate B = new(102, true, true, true, true, true, 0, 200, 9);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static HohCombatTargetState State()
    { var s = new HohCombatTargetState(); s.BeginFloor(FarmingMode.FreeExploration, 2, 61); return s; }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh58-designated-idle-mob-allowed-after-old-combat-clears", () => {
                var s = State(); var a = A with { Engaged = false }; s.Observe([a], true, Now);
                Check(s.Select([a], a.Id, true) == a.Id, "stale global combat flag cannot reject an otherwise permitted designated enemy"); }),
            ("hoh58-existing-hate-precedes-designated-new-pull", () => {
                var s = State(); var a = A with { Engaged = false }; s.Observe([a, B], true, Now);
                Check(s.Select([a, B], a.Id, true) == B.Id, "finish real hate before a voluntary pull"); }),
            ("hoh58-shared-preferred-beats-another-engaged-monsters-hp", () => {
                var s = State(); s.Observe([A, B], true, Now);
                Check(s.Select([A, B], A.Id, true) == A.Id, "navigation and output must not disagree because another monster has more HP"); }),
            ("hoh58-stoneskin-species-remains-priority", () => {
                var s = State(); var b = B with { StoneskinPriority = 1 }; s.Observe([A, b], true, Now);
                Check(s.Select([A, b], A.Id, true) == b.Id, "existing caster priority retained"); }),
            ("hoh58-active-stoneskin-cast-precedes-other-caster", () => {
                var s = State(); var a = A with { StoneskinPriority = 2 }; var b = B with { StoneskinPriority = 1 };
                s.Observe([a, b], true, Now); Check(s.Select([a, b], b.Id, true) == a.Id, "interrupt opportunity priority retained"); }),
            ("hoh58-unengaged-stoneskin-caster-does-not-start-extra-fight", () => {
                var s = State(); var b = B with { Engaged = false, StoneskinPriority = 2 }; s.Observe([A, b], true, Now);
                Check(s.Select([A, b], b.Id, true) == A.Id, "species priority cannot create hate"); }),
            ("hoh58-door-open-rejects-new-pull", () => {
                var s = State(); var a = A with { Engaged = false }; s.Observe([a], true, Now);
                Check(s.Select([a], a.Id, false) == 0, "door-open new-pull gate retained"); }),
            ("hoh58-dangerous-new-pull-still-rejected", () => {
                var s = State(); var a = A with { Engaged = false, NewPullAllowed = false };
                Check(s.Select([a], a.Id, true) == 0, "original voluntary pull restrictions remain authoritative"); }),
            ("hoh58-side-monster-killed-old-hate-selected-across-room", () => {
                var s = State(); s.Observe([A, B], true, Now);
                Check(s.Select([A, B], B.Id, true) == B.Id, "start on B while A is also engaged");
                var farA = A with { Engaged = false, DistanceSquared = 900, NewPullAllowed = false };
                var deadB = B with { Alive = false, Targetable = false, Hp = 0 };
                s.Observe([farA, deadB], true, Now.AddSeconds(1));
                Check(s.Select([farA, deadB], 0, false) == A.Id && s.HasEngagement, "B dying or crossing a room does not erase old A");
                Check(!s.ObserveSelection(Now, A.Id, 0, 0), "missing actual selection is not an attack window");
                Check(!s.ObserveSelection(Now.AddMilliseconds(100), A.Id, A.Id, 0), "verify read-back briefly");
                Check(s.ObserveSelection(Now.AddMilliseconds(400), A.Id, A.Id, 0), "automatically reselect A");
                s.Observe([farA with { Alive = false, Hp = 0 }, deadB], false, Now.AddSeconds(2));
                Check(!s.HasEngagement && s.Select([], 0, false) == 0, "only real death completes A's obligation"); }),
            ("hoh58-one-missed-native-engagement-scan-retains-old-hate", () => {
                var s = State(); s.Observe([A], true, Now); s.Observe([A with { Engaged = false }], true, Now.AddSeconds(20));
                Check(s.IsEngaged(A.Id), "in-combat transient target/hate loss is not disengagement"); }),
            ("hoh58-missing-engaged-actor-is-not-replaced-by-idle-target", () => {
                var s = State(); s.Observe([A], true, Now); var b = B with { Engaged = false };
                s.Observe([b], true, Now.AddSeconds(1)); Check(s.Select([b], b.Id, true) == 0 && s.HasEngagement, "reacquire old actor before exploring"); }),
            ("hoh58-confirmed-out-of-combat-clears-stale-obligation", () => {
                var s = State(); s.Observe([A], true, Now); var a = A with { Engaged = false };
                s.Observe([a], false, Now.AddSeconds(1)); Check(s.HasEngagement, "one late condition update insufficient");
                s.Observe([a], false, Now.AddSeconds(3)); Check(!s.HasEngagement, "genuine leash/reset must not latch combat forever"); }),
            ("hoh58-selection-alone-does-not-create-hate", () => {
                var s = State(); var a = A with { Engaged = false }; s.Select([a], a.Id, true);
                s.ObserveSelection(Now, a.Id, a.Id, 0); Check(!s.HasEngagement, "selected, casting and stone-curse are not native engagement evidence"); }),
            ("hoh58-targeting-reset-on-new-floor", () => {
                var s = State(); s.Observe([A], true, Now); s.ObserveHit(A.Id, Now, 123, 1, 0);
                s.BeginFloor(FarmingMode.FreeExploration, 2, 62);
                Check(!s.HasEngagement && !s.TryGetHit(A.Id, out _) && s.ChosenId == 0, "floor identity reset"); }),
            ("hoh58-targeting-and-hits-never-enabled-for-other-dungeons-or-boss", () => {
                foreach (var (mode,dungeon,floor) in new (FarmingMode?,uint,int)[] {
                    (FarmingMode.FreeExploration,4,61),(FarmingMode.FreeExploration,1,61),
                    (FarmingMode.FreeExploration,3,61),(FarmingMode.FreeExploration,2,70),(null,2,61) })
                {
                    var s = State(); s.BeginFloor(mode,dungeon,floor); s.Observe([A],true,Now); s.ObserveHit(A.Id,Now,123,1,0);
                    Check(!s.Enabled && !s.HasEngagement && !s.TryGetHit(A.Id,out _) && s.Select([A],A.Id,true)==0, "HoH normal floors only");
                } }),
            ("hoh58-hard-target-empty-never-confirms-selection", () => {
                var s = State(); Check(!s.ObserveSelection(Now,A.Id,0,0) && !s.ObserveSelection(Now.AddSeconds(4),A.Id,0,0), "no phantom selected target"); }),
            ("hoh58-wrong-hard-target-never-confirms-selection", () => {
                var s = State(); Check(!s.ObserveSelection(Now,A.Id,B.Id,0), "B does not count as A"); }),
            ("hoh58-wrong-soft-target-never-confirms-selection", () => {
                var s = State(); Check(!s.ObserveSelection(Now,A.Id,A.Id,B.Id), "rotation soft target must also agree"); }),
            ("hoh58-selection-grace-and-soft-target-readback", () => {
                var s = State(); Check(!s.ObserveSelection(Now,A.Id,A.Id,A.Id), "not ready on first assignment");
                Check(s.ObserveSelection(Now.AddMilliseconds(250),A.Id,A.Id,A.Id), "matching hard/soft observed across frames"); }),
            ("hoh58-selection-timeout-is-separate-from-geometry-budget", () => {
                var s = State(); s.ObserveSelection(Now,A.Id,0,0); s.ObserveSelection(Now.AddSeconds(6),A.Id,0,0);
                Check(s.SelectionTimedOut, "bounded wait for actual game target assignment");
                s.ObserveSelection(Now.AddSeconds(6.1),A.Id,A.Id,0); Check(!s.SelectionTimedOut, "successful read-back clears timeout"); }),
            ("hoh58-target-switch-cannot-inherit-selection-confirmation", () => {
                var s = State(); s.ObserveSelection(Now,A.Id,A.Id,0); s.ObserveSelection(Now.AddSeconds(1),A.Id,A.Id,0);
                Check(!s.ObserveSelection(Now.AddSeconds(2),B.Id,B.Id,0), "new enemy needs its own read-back"); }),
            ("hoh58-native-zero-damage-hit-counts-through-stoneskin", () => {
                var s = State(); s.ObserveHit(A.Id,Now,123,77,0);
                Check(s.TryGetHit(A.Id,out var hit) && hit.Damage==0 && hit.ActionId==123 && s.IsEngaged(A.Id), "fully absorbed native damage still proves an attack resolved"); }),
            ("hoh58-native-hit-progress-is-target-specific", () => {
                var s = State(); s.ObserveHit(B.Id,Now,123,77,10); Check(!s.TryGetHit(A.Id,out _), "hitting B cannot verify A"); }),
            ("hoh58-native-hit-duplicate-does-not-renew-output", () => {
                var s = State(); s.ObserveHit(A.Id,Now,123,77,0); s.TryGetHit(A.Id,out var before);
                s.ObserveHit(A.Id,Now.AddSeconds(3),123,77,0); s.TryGetHit(A.Id,out var after);
                Check(before==after, "same packet sequence is not another hit"); }),
            ("hoh58-next-real-hit-advances-revision-with-flat-hp", () => {
                var s = State(); s.ObserveHit(A.Id,Now,123,77,0); s.TryGetHit(A.Id,out var before);
                s.ObserveHit(A.Id,Now.AddSeconds(2),123,78,0); s.TryGetHit(A.Id,out var after);
                Check(after.Revision>before.Revision, "same skill through shield is still output progress"); }),
            ("hoh58-fresh-native-hit-is-consumed-only-once", () => {
                var s=State(); long revision=0; s.ObserveHit(A.Id,Now,123,1,0);
                Check(s.ConsumeNewHit(A.Id,ref revision,Now.AddSeconds(1)) && !s.ConsumeNewHit(A.Id,ref revision,Now.AddSeconds(1.1)), "one packet is one output event"); }),
            ("hoh58-stale-native-hit-cannot-verify-a-new-position", () => {
                var s=State(); long revision=0; s.ObserveHit(A.Id,Now,123,1,0);
                Check(!s.ConsumeNewHit(A.Id,ref revision,Now.AddSeconds(3)), "stale output rejected"); }),
            ("hoh58-native-hit-from-future-clock-is-not-output", () => {
                var s=State(); long revision=0; s.ObserveHit(A.Id,Now,123,1,0);
                Check(!s.ConsumeNewHit(A.Id,ref revision,Now.AddSeconds(-1)), "clock rollback cannot create evidence"); }),
            ("hoh58-native-damage-filter-retains-zero-absorbed-hits", () => {
                foreach(byte type in new byte[]{3,5,6}) Check(HohCombatTargetState.IsDirectPlayerDamage(true,42,42,1,type,0), "damage value is deliberately not required to be positive"); }),
            ("hoh58-native-miss-heal-and-status-cannot-prove-output", () => {
                foreach(byte type in new byte[]{0,1,2,4,14,27}) Check(!HohCombatTargetState.IsDirectPlayerDamage(true,42,42,1,type,0), "only resolved direct damage effects"); }),
            ("hoh58-native-pet-and-other-player-cannot-verify-player-position", () => {
                Check(!HohCombatTargetState.IsDirectPlayerDamage(true,42,43,1,3,0), "pet or teammate hit does not prove the player's own firing position"); }),
            ("hoh58-native-filter-excludes-items-reflections-and-other-dungeons", () => {
                Check(!HohCombatTargetState.IsDirectPlayerDamage(true,42,42,2,3,0) &&
                    !HohCombatTargetState.IsDirectPlayerDamage(true,42,42,1,3,0x80) &&
                    !HohCombatTargetState.IsDirectPlayerDamage(false,42,42,1,3,0), "no item, source-side effect or scope leakage"); }),
            ("hoh58-idle-between-ranged-casts-remains-a-real-attack-window", () => {
                Check(HohCombatTargetState.HasAttackOpportunity(true,false,true,false,0,A.Id),"within action range, not restricted to the three-meter navigation hold radius"); }),
            ("hoh58-wrong-cast-target-or-selection-does-not-build-failure-evidence", () => {
                Check(!HohCombatTargetState.HasAttackOpportunity(false,false,true,false,0,A.Id) &&
                    !HohCombatTargetState.HasAttackOpportunity(true,false,true,true,B.Id,A.Id),"actual target must agree"); }),
            ("hoh58-output-suppression-and-out-of-range-do-not-build-failure-evidence", () => {
                Check(!HohCombatTargetState.HasAttackOpportunity(true,true,true,false,0,A.Id) &&
                    !HohCombatTargetState.HasAttackOpportunity(true,false,false,false,0,A.Id),"recovery movement and approach are not failed attack opportunities"); }),
            ("hoh58-real-selection-retries-clears-conflicting-soft-target", () => {
                using var f = new Fixture(); f.Hard=f.Other; f.Soft=f.Other;
                Check(!f.Context.EnsureHohCombatTarget(f.Desired) && f.Hard==f.Desired && f.Soft==null,
                    "actual context writes and reads the intended game target, retaining the confirmation grace"); }),
            ("hoh58-real-rejected-target-assignment-stays-unconfirmed", () => {
                using var f = new Fixture(); f.Reject=true; f.Hard=f.Other;
                Check(!f.Context.EnsureHohCombatTarget(f.Desired) && f.Hard==f.Other, "failed setter cannot be advertised as an attack opportunity"); }),
            ("hoh58-real-selection-does-not-write-other-dungeons-or-bosses", () => {
                foreach(var (dungeon,floor) in new (uint,byte)[]{(4,61),(1,61),(3,61),(2,70)})
                {
                    using var f=new Fixture(); f.Hard=f.Other; f.Soft=f.Other;
                    typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(f.Context.Duty,dungeon);
                    typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(f.Context.Duty,floor);
                    Check(f.Context.EnsureHohCombatTarget(f.Desired) && f.Hard==f.Other && f.Soft==f.Other,"runtime hard/soft writes isolated to HoH normal floors");
                } }),
            ("hoh58-real-old-hate-is-not-suppressed-by-an-earlier-recovery-failure", () => {
                using var f=new Fixture(); f.Context.SyncHohCombatScope();
                f.Context.HohCombatTargets.Observe([A],true,Now);
                f.Context.SuppressCombatTarget(A.Id);
                Check(!f.Context.IsCombatTargetSuppressed(A.Id),"a temporary unreachable suppression cannot silently discard remembered real hate"); }),
            ("hoh58-real-combat-assist-keeps-shared-preferred-over-highest-hp", () => {
                using var f=new Fixture(); f.InstallCombatServices(); f.Objects.AddRange([f.Desired,f.Other]);
                f.Context.SetPreferredAggroTarget(101,System.Numerics.Vector3.Zero);
                f.Context.CombatAssist.Tick(f.Context.Configuration,f.Context,false,false,out _,out _,out _);
                Check(f.Hard==f.Desired,"real combat-assist branch selects the shared navigation target instead of higher-HP B"); }),
            ("hoh58-real-side-kill-automatically-reselects-old-opponent", () => {
                using var f=new Fixture(); f.InstallCombatServices(); f.Objects.AddRange([f.Desired,f.Other]);
                f.Context.ObserveHohCombatTargets(); f.Hard=f.Other; f.OtherDead=true;
                f.Context.SetPreferredAggroTarget(102,System.Numerics.Vector3.Zero);
                f.Context.CombatAssist.Tick(f.Context.Configuration,f.Context,false,true,out _,out _,out _);
                Check(f.Hard==f.Desired && f.Context.HohCombatTargets.IsEngaged(101), "B dead, door open, A still alive: output automatically returns to A"); }),
            ("hoh58-real-missing-selection-releases-gate-without-charging-failure", () => {
                using var f = new Fixture(); using var r = f.Recovery();
                f.Context.SetHohRecoveryMovement(r,true); var budget=f.Context.CombatRecoveryBudget.TotalFailures;
                r.SuspendForTargetSelection();
                Check(!f.Context.HohRecoveryMovementActive && f.Context.CombatRecoveryBudget.TotalFailures==budget && !r.IsActive,
                    "selection retry neither spends position retries nor leaves output locked"); }),
            ("hoh58-real-aggro-alone-is-not-an-attack-opportunity", () => {
                using var f = new Fixture(); using var r=f.Recovery();
                for(int ms=0;ms<=10000;ms+=200)
                    Check(!(bool)Call(r,"ShouldStartRecovery",Now.AddMilliseconds(ms),true,CombatSight.Blocked,true,false,false),
                        "engagement without an actual selected attack opportunity cannot spend geometry retries"); }),
            ("hoh58-real-shield-observer-unavailable-does-not-trigger-hp-only-recovery", () => {
                using var f=new Fixture(); using var r=f.Recovery(); Set(r,"_shieldWithoutObserver",true);
                for(int ms=0;ms<=10000;ms+=200)
                    Check(!(bool)Call(r,"ShouldStartRecovery",Now.AddMilliseconds(ms),true,CombatSight.Clear,true,true,false),
                        "degraded observer must not interpret an absorption shield as output failure"); }),
            ("hoh58-real-shield-does-not-disable-confirmed-obstruction-recovery", () => {
                using var f=new Fixture(); using var r=f.Recovery(); Set(r,"_shieldWithoutObserver",true); bool started=false;
                for(int ms=0;ms<=1000;ms+=200)
                    started|=(bool)Call(r,"ShouldStartRecovery",Now.AddMilliseconds(ms),true,CombatSight.Blocked,true,true,false);
                Check(started,"positive obstruction still uses the bounded safe path recovery"); }),
        ];
        foreach(var (name,test) in tests) { test(); Console.WriteLine("  ok  "+name); }
        return tests.Length;
    }
    private static object Call(object o,string n,params object[] a) => o.GetType().GetMethod(n,BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(o,a)!;
    private static void Set(object o,string n,object v) => o.GetType().GetField(n,BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(o,v);
    private sealed class Fixture : IDisposable
    {
        internal readonly RunContext Context=new(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
        internal readonly IBattleChara Desired,Other;
        internal readonly List<IGameObject> Objects=new();
        internal bool OtherDead;
        internal IGameObject? Hard,Soft;
        internal bool Reject;
        private readonly PropertyInfo _targetService;
        private readonly object? _previous;
        private readonly List<(PropertyInfo Property,object? Previous)> _otherServices=new();
        internal Fixture()
        {
            Desired=Enemy(101,()=>false); Other=Enemy(102,()=>OtherDead);
            Context.FarmingPlan=new(FarmingMode.FreeExploration,SaveUse.Create,1,1,false,false,true,true,false);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty,2U);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty,(byte)61);
            _targetService=typeof(RunContext).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Service")!.GetProperty("TargetManager",BindingFlags.Static|BindingFlags.NonPublic)!;
            _previous=_targetService.GetValue(null);
            _targetService.SetValue(null,Fake<ITargetManager>((m,a)=> {
                switch(m.Name) {
                    case "get_Target":return Hard; case "get_SoftTarget":return Soft;
                    case "set_Target":if(!Reject) Hard=(IGameObject?)a![0];return null;
                    case "set_SoftTarget":Soft=(IGameObject?)a![0];return null;
                    default:return Default(m);
                }
            }));
            InstallCombatServices(); // Selection now also rechecks existing hate / current-room eligibility.
        }
        internal void InstallCombatServices()
        {
            var player=Fake<IPlayerCharacter>((m,_)=>m.Name=="get_GameObjectId"?42UL:Default(m));
            Service("GameObjects",Fake<IObjectTable>((m,_)=>m.Name switch {
                "get_LocalPlayer"=>player,"GetEnumerator"=>((IEnumerable<IGameObject>)Objects).GetEnumerator(),_=>Default(m)}));
            Service("Condition",Fake<ICondition>((m,a)=>m.Name=="get_Item" && (ConditionFlag)a![0]! == ConditionFlag.InCombat?true:Default(m)));
            Context.Configuration.NecromancerBandedAutoSelect=true;
            Set(Context.CombatAssist,"_cachedEngageRangeSkillId",Context.Configuration.NecromancerBandedAttractSkillId);
            Set(Context.CombatAssist,"_cachedEngageRange",25f);
        }
        private void Service(string name,object value)
        {
            var p=typeof(RunContext).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Service")!.GetProperty(name,BindingFlags.Static|BindingFlags.NonPublic)!;
            _otherServices.Add((p,p.GetValue(null)));p.SetValue(null,value);
        }
        internal CombatPositionRecoveryController Recovery()=>new(Context,null!,null!,null,Context.CombatRecoveryBudget,null,_=>{},(_,_)=>{});
        public void Dispose(){Context.Dispose();_targetService.SetValue(null,_previous);foreach(var (p,v) in _otherServices.AsEnumerable().Reverse())p.SetValue(null,v);}
    }
    private static IBattleChara Enemy(ulong id,Func<bool> dead)=>Fake<IBattleChara>((m,_)=>m.Name switch {
        "get_GameObjectId"=>id,"get_TargetObjectId"=>42UL,"get_IsTargetable"=>!dead(),"get_IsDead"=>dead(),
        "get_CurrentHp"=>dead()?0U:id==102?200U:100U,"get_ObjectKind"=>ObjectKind.BattleNpc,"get_SubKind"=>(byte)5,_=>Default(m)});
    private static object? Default(MethodInfo m)=>m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
    private static T Fake<T>(Func<MethodInfo,object?[]?,object?> handler) where T:class
    {var p=DispatchProxy.Create<T,Proxy>();((Proxy)(object)p).Handler=handler;return p;}
    public class Proxy:DispatchProxy
    {internal Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);}
}
