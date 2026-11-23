using System.Collections;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using DeepDungeon.Fsd.Dalamud.Map;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace NewFolder3;

internal static class Hoh72ResourceAndContactTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTime Now = new(2026, 11, 22, 7, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh72-stone-contact-rejects-ranged-and-vertical-nearness", () => {
                Check(!HohPetrifiedContactState.AtFeet(Vector3.Zero,new(20,0,0)),"ranged cast range is not arrival");
                Check(!HohPetrifiedContactState.AtFeet(Vector3.Zero,new(0,3,0)),"overlapping XZ on another height is not feet");
                Check(HohPetrifiedContactState.AtFeet(Vector3.Zero,new(1,0,0)),"actual feet position accepted"); }),
            ("hoh72-stone-contact-allows-walking-longer-than-three-seconds", () => {
                var s=new HohPetrifiedContactState();
                for(int i=0;i<8;i++) Check(s.Update(7,new(i,0,0),new(20,0,0),100,0,Now.AddSeconds(i))==HohPetrifiedContactStep.Approach,"real walking must keep route instead of consuming retries"); }),
            ("hoh72-stone-contact-no-movement-has-a-short-bounded-end", () => {
                var s=new HohPetrifiedContactState();s.Update(7,Vector3.Zero,new(20,0,0),100,0,Now);
                Check(s.Update(7,Vector3.Zero,new(20,0,0),100,0,Now.AddSeconds(3))==HohPetrifiedContactStep.ApproachStalled,"do not spend the whole stone curse waiting on a bad path"); }),
            ("hoh72-stone-contact-output-clock-starts-only-after-arrival", () => {
                var s=new HohPetrifiedContactState();s.Update(7,Vector3.Zero,new(20,0,0),100,0,Now);
                Check(s.Update(7,new(20,0,0),new(20,0,0),100,0,Now.AddSeconds(10))==HohPetrifiedContactStep.Attack,"travel is not output timeout");
                Check(s.Update(7,new(20,0,0),new(20,0,0),100,0,Now.AddSeconds(14))==HohPetrifiedContactStep.OutputStalled,"bounded output after feet"); }),
            ("hoh72-stone-contact-native-hit-prevents-false-immediate-recovery", () => {
                var s=new HohPetrifiedContactState();s.Update(7,Vector3.Zero,Vector3.Zero,100,0,Now);
                Check(s.Update(7,Vector3.Zero,Vector3.Zero,100,1,Now.AddSeconds(3.9))==HohPetrifiedContactStep.Attack,"hit is valid despite HP acknowledgement delay");
                Check(s.Update(7,Vector3.Zero,Vector3.Zero,100,1,Now.AddSeconds(4.1))==HohPetrifiedContactStep.Attack,"same-frame HP delay must not start recovery"); }),
            ("hoh72-stone-contact-next-target-gets-a-new-output-window", () => {
                var s=new HohPetrifiedContactState();s.Update(7,Vector3.Zero,Vector3.Zero,100,0,Now);
                Check(s.Update(8,Vector3.Zero,Vector3.Zero,100,0,Now.AddSeconds(20))==HohPetrifiedContactStep.Attack,"no old target timer leakage");s.Reset();Check(s.TargetId==0,"floor release resets identity"); }),
            ("hoh72-production-contact-drives-to-feet-despite-current-cast", () => {
                using var f=new Fixture();f.PlayerCasting=true;f.Target=f.Enemy;f.SoftTarget=f.Enemy;f.Drive();
                Check(f.Nav.Target==f.EnemyPosition&&f.Nav.Moves==1,"full native navigation destination is actor feet");
                Check(f.Target==f.Enemy&&f.SoftTarget==f.Enemy,"keep the selected target while already casting and approaching");
                f.ReadySelection();f.Drive();
                Check(f.Context.CombatPreparationSequence==1&&f.Navigation.HasActiveTarget,"prepare output on the route without waiting for arrival");
                Check(!f.Context.NormalCombatOutputSuppressed,"AE is not switched off for movement"); }),
            ("hoh72-production-contact-handoff-selects-and-prepares-at-feet", () => {
                using var f=new Fixture();f.Drive();f.PlayerPosition=f.EnemyPosition;f.Drive();f.ReadySelection();f.Drive();
                Check(f.Target==f.Enemy&&f.Context.CombatPreparationSequence==1,"at feet must actually select and prepare output");
                Check(!f.Navigation.HasActiveTarget&&f.Nav.Moves==1,"no ranged hold or repeated path after arrival"); }),
            ("hoh74-assist-and-shared-selection-allow-output-on-stone-approach", () => {
                using var f=new Fixture();f.Target=f.Enemy;
                f.ReadySelection();
                Check(f.Context.PrepareHohPetrifiedOutput(f.Enemy,f.PlayerPosition)&&f.Target==f.Enemy,"assist prepares distant stone target immediately");
                Check(f.Context.EnsureHohCombatTarget(f.Enemy)&&f.Target==f.Enemy,"shared selection has no arrival prerequisite");
                Check(!f.Context.NormalCombatOutputSuppressed,"approach never disables rotation"); }),
            ("hoh72-stone-contact-gate-releases-when-effect-ends", () => {
                using var f=new Fixture();f.Context.HohPetrification.Observe(false,false,false,Now.AddSeconds(40));
                f.ReadySelection();Check(f.Context.EnsureHohCombatTarget(f.Enemy)&&f.Target==f.Enemy,"normal ranged output restores after stone ends"); }),
            ("hoh72-centaur-retreat-prefers-previous-confirmed-empty-room", () => {
                HohRetreatRoom[] rooms=[new(1,true,1,0,100),new(2,true,1,0,4),new(3,false,1,0,1),new(4,true,1,1,1),new(5,true,2,0,1)];
                Check(HohCentaurRetreatPolicy.OrderRooms(rooms,1).SequenceEqual(new[]{1,2}),"no unknown, occupied or remote room; backtrack first"); }),
            ("hoh72-centaur-retreat-no-empty-room-is-explicitly-unavailable", () => {
                Check(!HohCentaurRetreatPolicy.OrderRooms(new[]{new HohRetreatRoom(1,true,1,1,1)},1).Any(),"never label an occupied room empty"); }),
            ("hoh72-centaur-retreat-route-rejects-extra-mobs-and-third-room", () => {
                Vector3[] path=[Vector3.Zero,new(20,0,0)];int Room(Vector3 p)=>p.X<10?0:1;
                Check(HohCentaurRetreatPolicy.SafeRoute(path,Vector3.Zero,Array.Empty<Vector3>(),0,1,Room),"adjacent empty-room path accepted");
                Check(!HohCentaurRetreatPolicy.SafeRoute(path,Vector3.Zero,new[]{new Vector3(10,0,1)},0,1,Room),"do not approach idle enemies on the way");
                Check(!HohCentaurRetreatPolicy.SafeRoute(path,Vector3.Zero,Array.Empty<Vector3>(),0,1,p=>p.X>5&&p.X<8?2:Room(p)),"sample the whole path, not just endpoints"); }),
            ("hoh72-engaged-centaur-preempts-other-petrified-preferred", () => {
                var c=new Choice(1,true,false,true,1);var horse=new Choice(2,true,true,false,100);
                Check(HohPetrifiedTargetPolicy.Select(new[]{c,horse},x=>x.Engaged,_=>false,x=>x.Distance,x=>x.Preferred,x=>x.Centaur)==horse,"centaur priority during stone clear too"); }),
            ("hoh72-centaur-engagement-blocks-new-pulls-without-dropping-hate", () => {
                using var f=new Fixture();f.Centaur=true;f.EnemyTargetsPlayer=true;f.Context.HohPetrification.Observe(false,false,false,Now.AddSeconds(40));
                Check(f.Context.HohCentaurFightActive,"native target-of-player confirms centaur hate");
                Check(!f.Context.IsNewCombatTargetAllowed(f.Idle),"no voluntary new pull until centaur handled");
                Check(f.Context.IsNewCombatTargetAllowed(f.Enemy),"the centaur remains a combat obligation"); }),
            ("hoh72-pt-targets-do-not-inherit-stone-feet-rule", () => {
                using var f=new Fixture(4);Check(f.Context.EnsureHohCombatTarget(f.Enemy),"PT selection keeps original path");
                Check(!f.Context.HohCentaurFightActive&&!f.Context.NormalCombatOutputSuppressed,"no HoH movement or output gate leakage"); }),
            ("hoh73-centaur-direct-path-stop-does-not-depend-on-adapter-dispatch", () => {
                using var f=new Fixture();int stops=0;f.Set("_stopHohCentaurNativePath",(Action)(()=>stops++));
                f.Set("_hohCentaurMovingPath",new List<Vector3>{Vector3.Zero,new(10,0,0)});
                f.Call("StopHohCentaurOwnedPath");f.Call("StopHohCentaurOwnedPath");
                Check(stops==1&&f.Nav.Moves==0,"native-owned path stops once even though INavigator never issued it"); }),
            ("hoh73-centaur-path-query-does-not-stop-unowned-bmr-path", () => {
                using var f=new Fixture();int stops=0;f.Set("_stopHohCentaurNativePath",(Action)(()=>stops++));
                f.Call("StopHohCentaurOwnedPath");Check(stops==0,"querying or idle is not native path ownership"); }),
            ("hoh73-centaur-dispose-stops-direct-path-and-restores-bmr-after-stop-error", () => {
                using var f=new Fixture();int stops=0,releases=0;
                f.Context.HohCentaurRetreatActive=true;f.Context.SetBossMovementOverride=active=>{if(!active)releases++;return true;};
                f.Set("_hohCentaurMovementOwned",true);f.Set("_hohCentaurRooms",new Queue<(int Room,Vector3 Position)>());
                f.Set("_hohCentaurMovingPath",new List<Vector3>{Vector3.Zero,new(10,0,0)});
                f.Set("_stopHohCentaurNativePath",(Action)(()=>{stops++;throw new InvalidOperationException("native stop unavailable");}));
                f.Call("DisposeHohCentaurRetreat");f.Call("DisposeHohCentaurRetreat");
                Check(stops==1&&releases==1&&!f.Context.HohCentaurRetreatActive,"bounded cleanup releases ownership and BMR even if native stop throws"); }),
        ];
        foreach(var(name,test)in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
    private sealed record Choice(ulong Id,bool Engaged,bool Centaur,bool Preferred,float Distance);
    private sealed unsafe class Fixture : IDisposable
    {
        private readonly InstanceContentDeepDungeon* _dd=(InstanceContentDeepDungeon*)NativeMemory.AllocZeroed((nuint)sizeof(InstanceContentDeepDungeon));
        private readonly UIState* _ui=(UIState*)NativeMemory.AllocZeroed((nuint)sizeof(UIState));
        private readonly nint _previousUi=UIState.Addresses.Instance.Value;
        internal readonly RunContext Context;
        internal readonly NavigationHelper Navigation;
        internal readonly Navigator Nav=new();
        internal readonly IBattleChara Enemy,Idle;
        internal readonly IPlayerCharacter Player;
        internal IGameObject? Target,SoftTarget;
        internal Vector3 PlayerPosition,EnemyPosition=new(20,0,0);
        internal bool PlayerCasting,Centaur,EnemyTargetsPlayer;
        private readonly List<(PropertyInfo,object?)> _services=[];
        private readonly object _floor;
        internal Fixture(uint dungeon=2)
        {
            UIState.Addresses.Instance.Value=(nint)_ui;
            _dd->DeepDungeonId=(byte)dungeon;_dd->Floor=58;_dd->Party[0].EntityId=42;_dd->Party[0].RoomIndex=0;
            Context=new(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null),()=>(nint)_dd);
            typeof(DutyState).GetProperty("DungeonId")!.SetValue(Context.Duty,dungeon);
            typeof(DutyState).GetProperty("Floor")!.SetValue(Context.Duty,(byte)58);
            Context.FarmingPlan=new(FarmingMode.FreeExploration,SaveUse.Create,1,1,false,false,true,true,false);
            Context.HohPetrification.BeginFloor(2,58);Context.HohPetrification.ConfirmUse(Now);
            Player=Fake<IPlayerCharacter>((m,_)=>m.Name switch{"get_Position"=>PlayerPosition,"get_GameObjectId"=>42UL,"get_IsCasting"=>PlayerCasting,_=>Default(m)});
            Enemy=Fake<IBattleChara>((m,_)=>m.Name switch{"get_Position"=>EnemyPosition,"get_GameObjectId"=>7UL,"get_CurrentHp"=>100U,"get_IsTargetable"=>true,"get_BaseId"=>Centaur?8995U:8996U,"get_TargetObjectId"=>EnemyTargetsPlayer?42UL:0UL,"get_ObjectKind"=>ObjectKind.BattleNpc,"get_SubKind"=>(byte)BattleNpcSubKind.Combatant,_=>Default(m)});
            Idle=Fake<IBattleChara>((m,_)=>m.Name switch{"get_GameObjectId"=>0UL,"get_CurrentHp"=>100U,"get_IsTargetable"=>true,_=>Default(m)});
            Service("Condition",Fake<ICondition>((m,_)=>m.Name=="get_Item"?false:Default(m)));
            Service("GameObjects",Fake<IObjectTable>((m,_)=>m.Name switch{"get_LocalPlayer"=>Player,"GetEnumerator"=>((IEnumerable<IGameObject>)new[]{(IGameObject)Enemy}).GetEnumerator(),_=>Default(m)}));
            Service("TargetManager",Fake<ITargetManager>((m,a)=>{switch(m.Name){case "get_Target":return Target;case "set_Target":Target=(IGameObject?)a![0];return null;case "get_SoftTarget":return SoftTarget;case "set_SoftTarget":SoftTarget=(IGameObject?)a![0];return null;default:return Default(m);}}));
            if(dungeon==2) Check(MapPosGeneration.ApplyGeneratedCenters(_dd,[Vector3.Zero,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null]),"local stone-target room");
            Navigation=new(Nav,()=>false,()=>Now,_=>{},_=>{},()=>false);
            _floor=RuntimeHelpers.GetUninitializedObject(typeof(FloorExplorationController));
            Set("_ctx",Context);Set("_navHelper",Navigation);Set("_hohPetrifiedContact",new HohPetrifiedContactState());
            Set("_setStatus",(Action<string>)(_=>{}));Set("RecordReplayEvent",(Action<string,object>)((_,_)=>{}));
        }
        internal void Set(string name,object value)=>_floor.GetType().GetField(name,Private)!.SetValue(_floor,value);
        internal void Call(string name)=>_floor.GetType().GetMethod(name,Private)!.Invoke(_floor,Array.Empty<object>());
        internal void Drive()=>_floor.GetType().GetMethod("DriveHohPetrifiedContact",Private)!.Invoke(_floor,new object[]{Enemy,Player,Now});
        internal void ReadySelection(){Context.SyncHohCombatScope();Context.HohCombatTargets.ObserveSelection(DateTime.UtcNow.AddSeconds(-1),7,7,0);}
        private void Service(string name,object value)
        {var p=typeof(RunContext).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Service")!.GetProperty(name,BindingFlags.Static|BindingFlags.NonPublic)!;_services.Add((p,p.GetValue(null)));p.SetValue(null,value);}
        public void Dispose(){Context.Dispose();foreach(var(p,v)in _services.AsEnumerable().Reverse())p.SetValue(null,v);UIState.Addresses.Instance.Value=_previousUi;NativeMemory.Free(_ui);NativeMemory.Free(_dd);}
    }
    private sealed class Navigator : INavigator
    {internal int Moves;internal Vector3 Target;public bool PathfindAndMoveTo(Vector3 p,bool a){Target=p;Moves++;return true;}public bool RepathTo(Vector3 p,bool a)=>true;public void CancelAll(){} }
    private static object? Default(MethodInfo m)=>m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
    private static T Fake<T>(Func<MethodInfo,object?[]?,object?> handler)where T:class
    {var p=DispatchProxy.Create<T,Proxy>();((Proxy)(object)p).Handler=handler;return p;}
    public class Proxy:DispatchProxy
    {internal Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);}
}
