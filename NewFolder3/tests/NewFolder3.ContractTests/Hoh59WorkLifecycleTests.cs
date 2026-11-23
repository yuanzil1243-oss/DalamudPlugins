using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Dalamud.Runtime.Search;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace NewFolder3;

internal static unsafe class Hoh59WorkLifecycleTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly DateTime Now = new(2026, 11, 21, 14, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    internal static int Run()
    {
        (string, Action)[] cases = [
            ("hoh59-floor38-streamed-native-silver-and-gold-reach-shared-room-builder", () => {
                using var f = new Fixture();
                f.Native(0, 2, 0); f.Native(1, 3, 1); f.Native(2, 2, 2);
                var first = Chest(1, FloorChestKind.Silver, 5);
                f.Scan([first], setup: false);
                var gold = Chest(2, FloorChestKind.Gold, 55); var silver = Chest(3, FloorChestKind.Silver, 105);
                f.Route(1, 2); f.Scan([first, gold, silver]);
                Check(f.Executor.LiveChestFilter!(gold) && f.Executor.LiveChestFilter(silver), "late native candidates cannot be removed as backward drops");
                f.BuildRoom(1); Check(f.Executor.RoomContext!.Waypoints.Any(w => w.Position == gold.Object.Position), "actual shared builder must receive the gold entity");
                f.Executor.ClearRoomContext(); f.Route(2); f.BuildRoom(2);
                Check(f.Executor.RoomContext!.Waypoints.Any(w => w.Position == silver.Object.Position), "actual shared builder must receive the silver entity");
            }),
            ("hoh59-new-drop-with-no-native-room-type-evidence-keeps-forward-only-policy", () => {
                using var f = new Fixture(); f.Native(0, 3, 1); f.Scan([], setup: false); f.Route(1);
                var drop = Chest(7, FloorChestKind.Gold, -10); f.Scan([drop]);
                Check(!f.Executor.LiveChestFilter!(drop), "a backward box without native room/type evidence stays excluded");
            }),
            ("hoh59-native-room-type-ambiguity-is-not-called-a-confirmed-drop", () => {
                using var f = new Fixture(); f.Native(0, 3, 0); f.Scan([], setup: false); f.Route(0);
                var unknown = Chest(8, FloorChestKind.Gold, -10); f.Scan([unknown]);
                Check(f.Executor.LiveChestFilter!(unknown), "native table has no entity id: do not invent drop certainty from elapsed time");
            }),
            ("hoh59-disabled-gold-does-not-create-extra-work", () => {
                using var f = new Fixture(); f.Context.RunOptions.Update(o => o.OpenGold = false);
                f.Executor.ApplyRunOptions(f.Context.RunOptions.Current); f.Native(0, 3, 1); f.Route(1);
                f.Scan([Chest(9, FloorChestKind.Gold, 55)]);
                Check(!f.ActualWork && f.Executor.PlannedRouteCount == 0, "retain user options; disabled boxes cannot keep a floor alive");
            }),
            ("hoh59-no-box-work-removes-residual-intel-route-and-does-not-search-for-plan-count", () => {
                using var f = new Fixture(); f.Executor.ApplyRetainedRoute([new(1, false, false, true, HoardEvidenceState.Disabled)]);
                f.Scan([]); Check(!f.ActualWork && f.Executor.PlannedRouteCount == 0, "room count alone is not work");
                Check(!(bool)Call(f.Controller, "ShouldContinuePlannedRouteForPassageActivation", FloorObjectiveKind.ActivatePassage), "opening must use clearing/patrol instead of search");
            }),
            ("hoh59-actual-native-box-task-remains-after-door-opens", () => {
                using var f = new Fixture(); f.Native(0, 3, 1); f.Route(1); f.Scan([Chest(10, FloorChestKind.Gold, 55)]);
                Check(f.ActualWork, "native chest is actual work");
                for (int i=0; i<40; i++) {
                    f.PlayerPosition = new(i % 2 == 0 ? -2 : 2, 0, 0); f.Scan([Chest(10, FloorChestKind.Gold, 55)]);
                    var decision = ObjectiveArbiter.Decide(new(false,false,false,false,true,true,false,false,false,false,RequiredChestWork:f.ActualWork));
                    Check(decision.PrimaryObjective == FloorObjectiveKind.OpenPlannedChest && decision.Channels.Transition == CommandChannelPermission.Blocked, "floor36: forward geometry changes cannot repeatedly grant passage");
                }
            }),
            ("hoh59-door-open-finishes-old-engagement-before-retained-chest", () => {
                using var f = new Fixture(); f.Native(0, 3, 1); f.Route(1); f.Scan([Chest(11, FloorChestKind.Gold, 55)]);
                var fighting = ObjectiveArbiter.Decide(new(false,false,false,false,true,true,false,true,true,true,RequiredChestWork:f.ActualWork,PassageFirst:true));
                var clear = ObjectiveArbiter.Decide(new(false,false,false,false,true,true,false,false,false,true,RequiredChestWork:f.ActualWork));
                Check(fighting.PrimaryObjective == FloorObjectiveKind.FinishCombatBeforePassage && clear.PrimaryObjective == FloorObjectiveKind.OpenPlannedChest, "reuse arbiter finish-combat ordering, then resume the preserved box");
            }),
            ("hoh59-late-native-box-appends-to-the-existing-room-task", () => {
                using var f = new Fixture(); f.Native(0, 3, 1); f.Native(1, 3, 1);
                var a=Chest(12,FloorChestKind.Gold,52); var b=Chest(13,FloorChestKind.Gold,58);
                f.Route(1); f.Scan([a]); f.BuildRoom(1); var original=f.Executor.RoomContext;
                f.Scan([a,b]); f.Scan([a,b]);
                Check(ReferenceEquals(original,f.Executor.RoomContext) && original!.Waypoints.Count==2 && original.BuildOutcomeSnapshot(false).Chests.RequiredCount==2,
                    "append once without replacing shared task or losing its progress");
            }),
            ("hoh59-native-entity-missing-waits-bounded-and-cannot-report-empty-success", () => {
                using var f=new Fixture(); f.Native(0,3,1); f.Route(1); f.Scan([]);
                Check(f.Wait(1,Now) && f.Wait(1,Now.AddSeconds(2)) && !f.Wait(1,Now.AddSeconds(3)), "rescan window is bounded");
                var empty=RoomObjectiveOutcomePlanner.Decide(new(default,new(true,0,0,0,false,false,false,false),default));
                var protectedOutcome=(RoomObjectiveOutcomeResult)Call(f.Controller,"ProtectHohChestOutcome",1,empty);
                Check(protectedOutcome.Chests==ObjectiveOutcomeKind.Deferred && !protectedOutcome.MarkChestsSearched,
                    "native evidence must survive an empty filtered list; skip requires explicit retry policy");
            }),
            ("hoh59-native-streaming-resolves-the-same-wait-without-a-new-timer", () => {
                using var f=new Fixture(); f.Native(0,3,1); f.Route(1); f.Scan([]); Check(f.Wait(1,Now),"wait starts");
                f.Scan([Chest(14,FloorChestKind.Gold,55)]);
                Check(!f.Wait(1,Now.AddSeconds(1)),"actual entity resolves missing native evidence");
                var outcome=new RoomObjectiveOutcomeResult(ObjectiveOutcomeKind.NotRequested,ObjectiveOutcomeKind.Succeeded,ObjectiveOutcomeKind.NotRequested);
                Check(((RoomObjectiveOutcomeResult)Call(f.Controller,"ProtectHohChestOutcome",1,outcome)).Chests==ObjectiveOutcomeKind.Succeeded,"real completion is allowed after evidence arrives");
            }),
            ("hoh59-native-table-room-needs-all-enabled-box-evidence", () => {
                using var f=new Fixture(); f.Native(0,3,1); f.Native(1,2,1); f.Route(1);
                f.Scan([Chest(15,FloorChestKind.Gold,55)]); Check(f.Wait(1,Now),"one loaded box cannot resolve an unobserved enabled silver box");
                f.Scan([Chest(15,FloorChestKind.Gold,55),Chest(16,FloorChestKind.Silver,59)]);
                Check(!f.Wait(1,Now.AddSeconds(1)),"both enabled types observed");
            }),
            ("hoh59-present-but-not-targetable-box-is-not-empty-room-success", () => {
                using var f=new Fixture();f.Native(0,3,1);f.Route(1);var box=Chest(21,FloorChestKind.Gold,55);
                f.Scan([box with {Object=box.Object with {IsTargetable=false}}]);
                Check(f.Wait(1,Now),"an unusable streaming placeholder is still unresolved native evidence");
                f.Scan([box]);Check(!f.Wait(1,Now.AddSeconds(1)),"real targetable evidence resolves the pending room");
            }),
            ("hoh59-opened-native-object-cannot-recreate-an-optional-task", () => {
                using var f=new Fixture(); f.Native(0,3,1); f.Route(1);
                var opened=Chest(17,FloorChestKind.Gold,55) with {NativeStateAvailable=true,NativeCompletionKind=NativeTreasureCompletionKind.TreasureState,
                    State=(FFXIVClientStructs.FFXIV.Client.Game.Object.Treasure.TreasureState)1};
                f.Scan([opened]); Check(!f.Executor.HasExtraChestRoom(1),"opened is native state, not a new optional batch");
            }),
            ("hoh59-host-stops-ticking-for-22-minutes-resume-does-not-spend-box-budget", () => {
                using var f=new Fixture(); var budget=(OptionalChestBudget)Field(f.Controller,"_optionalChestBudget");
                var positions=(Dictionary<ulong,Vector3>)Field(f.Controller,"_optionalChestPositions");positions[1]=Vector3.One;
                budget.Select(1,10,Now);budget.Tick(1,Now.AddSeconds(20),false,10);
                f.Controller.SuspendHohOptionalChestClock(Now.AddMinutes(22));
                budget.Tick(1,Now.AddMinutes(22).AddSeconds(1),false,10);
                Check(budget.Reason(1)==null,"floor36: missing paused ticks are not active time");
                budget.Tick(1,Now.AddMinutes(22).AddSeconds(11),false,10);
                Check(budget.Reason(1)=="no-real-progress","resume does not reset the already consumed active budget");
            }),
            ("hoh59-ordinary-recovery-movement-does-not-suppress-output", () => {
                using var f=new Fixture();var owner=new object(); f.Context.SetHohRecoveryMovement(owner,true);
                Check(f.Context.HohRecoveryMovementActive && !f.Context.NormalCombatOutputSuppressed,"search/query/move retains AE output");
                f.Context.SetHohRecoveryMovement(owner,false);Check(!f.Context.HohRecoveryMovementActive,"ownership is still released");
            }),
            ("hoh59-real-optional-room-navigation-failure-skips-box-without-pausing-floor", () => {
                using var f=new Fixture();f.Native(0,3,1);var box=Chest(18,FloorChestKind.Gold,55);f.Route(1);f.Scan([box]);
                Check((bool)Call(f.Controller,"EnsureObjectiveExecution",FloorObjectiveKind.OpenPlannedChest),"execution starts");
                ((HashSet<int>)Field(f.Controller,"_hohRegeneratedRooms")).Add(1);
                f.CallNative("HandleHohRoomFailure",1);
                Check(!f.Context.StatusIsError && f.Executor.IsChestRoomSearched(1) && !f.Executor.LiveChestFilter!(box),
                    "the actual controller/ledger records optional skip and excludes the failed entity, not whole-floor pause");
            }),
            ("hoh59-required-room-navigation-failure-retains-explicit-recoverable-stop", () => {
                using var f=new Fixture();f.Context.RunOptions.Update(o=>o.HarvestChestsRequired=true);f.Native(0,3,1);f.Route(1);f.Scan([Chest(19,FloorChestKind.Gold,55)]);
                ((HashSet<int>)Field(f.Controller,"_hohRegeneratedRooms")).Add(1);f.CallNative("HandleHohRoomFailure",1);
                Check(f.Context.StatusIsError && f.Executor.PlannedRouteCount==1,"mandatory policy is not silently downgraded with optional boxes");
            }),
            ("hoh59-actual-shared-task-completion-releases-chest-work-to-passage", () => {
                using var f=new Fixture();f.Native(0,3,1);var box=Chest(20,FloorChestKind.Gold,55);f.Route(1);f.Scan([box]);
                Call(f.Controller,"EnsureObjectiveExecution",FloorObjectiveKind.OpenPlannedChest);f.BuildRoom(1);
                Check((bool)Call(f.Controller,"BeginRoomObjectiveExecutions",1,f.Executor.CurrentPlanEntry!.Value),"real ledger begins");
                var room=f.Executor.RoomContext!;var waypoint=room.CurrentWaypoint!.Value;
                Call(f.Controller,"FinishSupplyChest",waypoint);room.RecordWaypointOutcome(waypoint,WaypointOutcomeKind.Completed,"NativeCompletion");room.AdvanceWaypoint();
                f.CallNative("FinalizeRoomSearch",f.Player,1,false,false,null!,"RoomExecutionFinished");f.CallNative("RefreshHohActualChestWork");
                Check(f.Executor.IsChestRoomSearched(1)&&!f.ActualWork,"confirmed completion must clear actual work without a new batch");
                Check(ObjectiveArbiter.Decide(new(false,false,false,false,true,true,false,false,false,false,RequiredChestWork:f.ActualWork)).PrimaryObjective==FloorObjectiveKind.EnterPassage,"transition is released after real work finishes");
            }),
            ("hoh59-pt-controller-keeps-existing-plan-and-missing-evidence-behavior", () => {
                using var f=new Fixture(4);f.Executor.ApplyRetainedRoute([new(1,false,false,true,HoardEvidenceState.Disabled)]);
                fixed(InstanceContentDeepDungeon* dd=&f.Dungeon) Call(f.Controller,"RefreshHohActualChestWork",Pointer.Box(dd,typeof(InstanceContentDeepDungeon*)));
                Check(f.Executor.PlannedRouteCount==1 && !f.Wait(1,Now),"HoH adaptation is inert in PT");
            }),
            ("hoh77-no-chest-clearing-keeps-actual-player-origin", () => {
                using var f=new Fixture();var (room,position)=f.SearchOrigin();
                Check(room==0 && position==Vector3.Zero,"current room eligibility and topology ranking must use the actual player, not a remote passage room");
            }),
            ("hoh59-door-open-or-unknown-does-not-pull-from-a-fake-passage-origin", () => {
                using var f=new Fixture();typeof(DutyState).GetProperty(nameof(DutyState.PassageOpen))!.SetValue(f.Context.Duty,true);
                Check(f.SearchOrigin()==(0,Vector3.Zero),"open door keeps original combat cleanup position");
                typeof(DutyState).GetProperty(nameof(DutyState.PassageOpen))!.SetValue(f.Context.Duty,false);
                f.Dungeon.MapData[2]&=~InstanceContentDeepDungeon.RoomFlags.Passage;
                Check(f.SearchOrigin()==(0,Vector3.Zero),"unknown passage retains reachable-room search instead of waiting forever");
            }),
            ("hoh59-existing-engagement-prevents-new-passage-area-selection", () => {
                using var f=new Fixture();f.Context.HohCombatTargets.BeginFloor(FarmingMode.FreeExploration,2,38);
                f.Context.HohCombatTargets.Observe([new(91,true,true,true,true,false,0,100,1)],true,Now);
                Check(f.SearchOrigin()==(0,Vector3.Zero),"old hate stays before any new passage-area target");
            }),
            ("hoh59-pt-and-other-dungeon-combat-item-slots-keep-their-original-semantics", () => {
                foreach(uint dungeon in new uint[]{1,3,4})foreach(uint slot in new uint[]{11,15})
                {
                    Check(ProgressionItemPolicy.AllowsCombat(slot,false,FloorItemUsePurpose.GoldChestOvercap,dungeon)==(slot==FloorInitPlanner.HastePomanderSlotIndex),"PT slot11 is haste and must retain its original permission; slot15 must not inherit HoH petrification");
                    Check(!ProgressionItemPolicy.AllowsCombat(slot,false,FloorItemUsePurpose.HohFrailty,dungeon),"HoH-specific purpose cannot enter another dungeon");
                }
                foreach(uint slot in new uint[]{11,15})Check(ProgressionItemPolicy.AllowsCombat(slot,false,FloorItemUsePurpose.GoldChestOvercap,2),"allowed HoH cap relief remains combat capable");
            }),
        ];
        foreach(var (name,test) in cases){test();Console.WriteLine("  ok  "+name);} return cases.Length;
    }

    private static FloorChestEvidence Chest(ulong id,FloorChestKind kind,float x)=>new(new(1,id,(uint)id,1,new(x,0,0),true,"Treasure",0),kind,false,NativeTreasureCompletionKind.Unavailable,0,0);
    private static object Call(object o,string method,params object[] args)=>o.GetType().GetMethod(method,Instance)!.Invoke(o,args)!;
    private static object Field(object o,string name)=>o.GetType().GetField(name,Instance)!.GetValue(o)!;
    private static void Set(object o,string name,object value)=>o.GetType().GetField(name,Instance)!.SetValue(o,value);
    private sealed class Fixture:IDisposable
    {
        internal InstanceContentDeepDungeon Dungeon;
        internal readonly RunContext Context=new(new(),new(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
        internal readonly FloorExplorationController Controller;
        internal AutoPilotExecutor Executor=>Controller.Executor!;
        internal Vector3 PlayerPosition;
        internal readonly IPlayerCharacter Player;
        internal bool ActualWork=>(bool)Field(Controller,"_hohActualChestWork");
        private readonly List<(PropertyInfo Property,object? Previous)> _services=[];
        internal Fixture(uint dungeon=2)
        {
            Dungeon.DeepDungeonId=(byte)dungeon;Dungeon.Floor=38;Dungeon.ContentTimeLeft=1800;
            Dungeon.Party[0].EntityId=42;Dungeon.Party[0].RoomIndex=0;
            var flags=InstanceContentDeepDungeon.RoomFlags.Revealed;
            Dungeon.MapData[0]=flags|InstanceContentDeepDungeon.RoomFlags.Home|InstanceContentDeepDungeon.RoomFlags.ConnectionE;
            Dungeon.MapData[1]=flags|InstanceContentDeepDungeon.RoomFlags.ConnectionW|InstanceContentDeepDungeon.RoomFlags.ConnectionE;
            Dungeon.MapData[2]=flags|InstanceContentDeepDungeon.RoomFlags.Passage|InstanceContentDeepDungeon.RoomFlags.ConnectionW;
            Context.FarmingPlan=new(FarmingMode.FreeExploration,SaveUse.Create,1,1,false,false,true,true,false);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(Context.Duty,dungeon);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(Context.Duty,(byte)38);
            Context.RunOptions.Set(new(){OpenGold=true,OpenSilver=true,BandedEnabled=false});
            Player=Fake<IPlayerCharacter>((m,_)=>m.Name switch {"get_GameObjectId"=>42UL,"get_Position"=>PlayerPosition,_=>Default(m)});
            Service("GameObjects",Fake<IObjectTable>((m,_)=>m.Name switch {"get_LocalPlayer"=>Player,"GetEnumerator"=>Enumerable.Empty<IGameObject>().GetEnumerator(),_=>Default(m)}));
            Service("Condition",Fake<ICondition>((m,_)=>Default(m)));
            var detail=new DetailedMapRunSnapshot(DetailedMapRuntimePolicy.PalacePalOnly,null,null);
            var helper=new NavigationHelper(new Navigator(),()=>false,()=>Now,_=>{},_=>{},()=>false);
            var pomanders=new PomanderManager();var items=new FloorItemExecutor(Context,pomanders,null,()=>FloorPhase.FloorActive,(_,_)=>{},(_,_)=>{});
            var graph=new NormalFloorGraphSnapshot(0,0,[0,1,2],new int[,]{{0,1,2},{1,0,1},{2,1,0}});
            Controller=new(1,dungeon,38,FloorRuntimeKind.Normal,Now,graph,false,detail,null,items,Context,helper,new(helper),new(),pomanders,null,null,new(),
                ()=>FloorPhase.FloorActive,_=>{},()=>"",_=>{},()=>false,()=>{},(_,_)=>{},(_,_)=>{},(_,_)=>{},(_,_)=>{},()=>Context.RunOptions.Current,
                ()=>1,()=>0,_=>new{},_=>{},(_,_)=>{},(_,_)=>{},(_,_,_,_,_)=>{},()=>{},(_,_,_,_)=>{});
            Executor.ApplyRunOptions(Context.RunOptions.Current);
            if(dungeon==2)fixed(InstanceContentDeepDungeon* dd=&Dungeon)
                Check(MapPosGeneration.ApplyGeneratedCenters(dd,[Vector3.Zero,new(50,0,0),new(100,0,0),null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]),"test runtime centers");
        }
        internal void Native(int index,byte type,byte room){Dungeon.Chests[index].ChestType=type;Dungeon.Chests[index].RoomIndex=(sbyte)room;}
        internal void Route(params int[] rooms)=>Executor.ApplyRetainedRoute(rooms.Select(r=>new RoomPlanEntry(r,false,true)).ToArray());
        internal void Scan(IReadOnlyList<FloorChestEvidence> chests,bool setup=true)
        {
            typeof(FloorObjectEvidenceTracker).GetProperty("Current")!.SetValue(Controller.ObjectEvidence,new FloorObjectEvidenceSnapshot(true,1,1,Now,PlayerPosition,chests.Count,chests,[],[],[],[]));
            Controller.PlanningState.MarkSetupPlanGenerated(setup);Set(Controller,"_nextSupplyScan",DateTime.MinValue);
            fixed(InstanceContentDeepDungeon* dd=&Dungeon)Call(Controller,"UpdateSupplyCandidates",Pointer.Box(dd,typeof(InstanceContentDeepDungeon*)));
        }
        internal void BuildRoom(int room)
        {
            Route(room);fixed(InstanceContentDeepDungeon* dd=&Dungeon)
                Check(Executor.StartCurrentPlanRoomSearch(dd,Controller.ObjectEvidence.Current!,PlayerPosition,out bool unavailable,out _)&&!unavailable,"shared room build");
        }
        internal bool Wait(int room,DateTime now){fixed(InstanceContentDeepDungeon* dd=&Dungeon)return (bool)Call(Controller,"WaitForHohNativeChestEvidence",Pointer.Box(dd,typeof(InstanceContentDeepDungeon*)),room,now);}
        internal object CallNative(string method,params object[] args){fixed(InstanceContentDeepDungeon* dd=&Dungeon)return Call(Controller,method,new object[]{Pointer.Box(dd,typeof(InstanceContentDeepDungeon*))}.Concat(args).ToArray());}
        internal (int,Vector3) SearchOrigin(){fixed(InstanceContentDeepDungeon* dd=&Dungeon){return ((int,Vector3))Call(Controller,"ResolveClearingSearchOrigin",Pointer.Box(dd,typeof(InstanceContentDeepDungeon*)),Player);}}
        private void Service(string name,object value){var p=typeof(RunContext).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Service")!.GetProperty(name,BindingFlags.Static|BindingFlags.NonPublic)!;_services.Add((p,p.GetValue(null)));p.SetValue(null,value);}
        public void Dispose(){Context.Dispose();foreach(var(p,previous)in _services.AsEnumerable().Reverse())p.SetValue(null,previous);}
    }
    private sealed class Navigator:INavigator { public bool PathfindAndMoveTo(Vector3 dest,bool align)=>true;public bool RepathTo(Vector3 dest,bool align)=>true;public void CancelAll(){} }
    private static object? Default(MethodInfo m)=>m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
    private static T Fake<T>(Func<MethodInfo,object?[]?,object?> handler)where T:class{var p=DispatchProxy.Create<T,Proxy>();((Proxy)(object)p).Handler=handler;return p;}
    public class Proxy:DispatchProxy { internal Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? method,object?[]? args)=>Handler(method!,args); }
}
