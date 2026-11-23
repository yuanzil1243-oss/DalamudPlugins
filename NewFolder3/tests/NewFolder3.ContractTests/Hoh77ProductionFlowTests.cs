using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Game.ClientState.Statuses;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace NewFolder3;

internal static unsafe class Hoh77ProductionFlowTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly DateTime Now = new(2026, 11, 22, 11, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh82-production-94through97-petrified-centaur-clear-preempts-ban", () => {
                foreach(byte floor in new byte[]{94,95,96,97}) {
                    using var f=new Fixture(floor:floor);f.Main.Hp=0;var horse=f.Add(8,new(5,0,0),centaur:true);horse.Petrified=true;
                    f.Context.HohPetrification.BeginFloor(2,floor);f.Context.HohPetrification.ConfirmUse(f.Time);
                    Check(RunContext.IsHohPetrified(horse.Actor),"fixture native437 must be readable through Dalamud StatusList: length="+horse.Actor.StatusList.Length+", native="+horse.Statuses->Status[0].StatusId+", read="+string.Join(',',horse.Actor.StatusList.Select(s=>s.StatusId)));
                    Check(f.Context.IsNewCombatTargetAllowed(horse.Actor),"stone clear owns target before ordinary centaur ban");
                    Check(f.Context.SelectHohPetrifiedTarget([horse.Actor],f.PlayerPosition)?.GameObjectId==horse.Id,"actual shared clear selector chooses centaur");
                    Check(f.Context.ResolveHohCombatTarget(0,true)?.GameObjectId==horse.Id,"combat resolver follows stone clear target");
                    f.ReadyTarget(horse);Check(f.Context.PrepareHohPetrifiedOutput(horse.Actor,f.PlayerPosition)&&f.Hard?.GameObjectId==horse.Id,"combat preparation no longer clears selection");
                    Check(f.Context.HohLiveCentaurObstacles().Length==0,"stone target cannot exclude itself as dangerous centaur");
                } }),
            ("hoh82-production-petrification-confirmation-alone-cannot-permit-centaur", () => {
                using var f=new Fixture(floor:96);var horse=f.Add(8,new(5,0,0),centaur:true);
                f.Context.HohPetrification.BeginFloor(2,96);f.Context.HohPetrification.ConfirmUse(f.Time);
                Check(!f.Context.IsNewCombatTargetAllowed(horse.Actor),"target must actually have437");
                horse.Petrified=true;f.Context.HohPetrification.BeginFloor(2,97);
                Check(!f.Context.IsNewCombatTargetAllowed(horse.Actor),"ordinary flow cannot use leftover status as blanket exception"); }),
            ("hoh82-production-petrified-centaur-clear-keeps-room-door-and-status-gates", () => {
                using var f=new Fixture(floor:95);var horse=f.Add(8,new(60,0,0),centaur:true);horse.Petrified=true;
                f.Context.HohPetrification.BeginFloor(2,95);f.Context.HohPetrification.ConfirmUse(f.Time);
                Check(!f.Context.IsNewCombatTargetAllowed(horse.Actor),"stone clear does not select across rooms");
                horse.Position=new(5,0,0);Check(f.Context.IsNewCombatTargetAllowed(horse.Actor),"same-room target allowed");
                horse.Petrified=false;f.Hard=horse.Actor;f.Context.ClearUnsafeHohPullTargets();
                Check(f.Hard==null&&!f.Context.IsNewCombatTargetAllowed(horse.Actor),"status loss immediately restores centaur ban");
                horse.Petrified=true;f.Context.HohPetrification.Observe(true,true,true,f.Time);
                Check(!f.Context.IsNewCombatTargetAllowed(horse.Actor),"open passage stops new stone target acquisition"); }),
            ("hoh82-production-stone-centaur-does-not-start-or-retain-retreat", () => {
                using var f=new Fixture(floor:96);f.Main.Hp=0;var horse=f.Add(8,new(5,0,0),centaur:true);horse.Engaged=true;horse.Petrified=true;
                f.Context.HohPetrification.BeginFloor(2,96);f.Context.HohPetrification.ConfirmUse(f.Time);
                Check(!f.Context.HohCentaurFightActive&&!f.Retreat(),"do not retreat instead of breaking stone target");
                Set(f.FloorDriver,"_hohCentaurRooms",new Queue<(int Room,Vector3 Position)>());
                f.Context.HohCentaurRetreatActive=true;
                Check(!f.Retreat()&&!f.Context.HohCentaurRetreatActive&&f.Events.Contains("hoh-centaur-retreat-ended"),"already owned retreat yields immediately to confirmed stone clear");
                horse.Petrified=false;Check(f.Context.HohCentaurFightActive,"loss of status restores dangerous engaged fight");
                Check(f.Context.IsNewCombatTargetAllowed(horse.Actor),"existing hate obligation survives stone expiry"); }),
            ("hoh82-production-magicite-ban-stops-all-requests-without-consuming-budget", () => {
                using var f=new Fixture(floor:96);f.Dd->DeepDungeonBanId=DeepDungeonFloorItemUsePolicy.HohMagiciteUseProhibitedBanId;
                using var items=new FloorItemExecutor(f.Context,new(),null,()=>FloorPhase.FloorActive,(_,_)=>{},(_,_)=>{});
                for(int tick=0;tick<300;tick++)foreach(byte stone in new byte[]{1,2,3,4})foreach(var purpose in new[]{FloorItemUsePurpose.HohMagicite,FloorItemUsePurpose.HohMagiciteOvercap}) {
                    Check(items.GetStoneCountAvailableForFloorUse(stone)==0,"ban reports no usable stock");
                    Check(!items.TryDispatchFloorStone(stone,f.Dd,"disabled magicite",purpose),"no native dispatch for clear or overcap");
                }
                Check(items.Session.Pending==null&&!items.Session.DispatchedThisUpdate&&!items.HohBudget.MagiciteConfirmed&&items.Session.NextUseAtUtc==DateTime.MinValue,"disabled is not a request, confirmation or retry-budget event"); }),
            ("hoh82-production-magicite-unknown-floor-does-not-advertise-usable-stock", () => {
                using var f=new Fixture(floor:95);f.Dd->Floor=96;
                using var items=new FloorItemExecutor(f.Context,new(),null,()=>FloorPhase.FloorActive,(_,_)=>{},(_,_)=>{});
                Check(items.GetStoneCountAvailableForFloorUse(1)==0,"mismatched native state cannot schedule magicite"); }),
            ("hoh82-production-magicite-ban-does-not-fake-pending-consumption", () => {
                using var f=new Fixture(floor:97);f.Dd->DeepDungeonBanId=5;
                using var items=new FloorItemExecutor(f.Context,new(),null,()=>FloorPhase.FloorActive,(_,_)=>{},(_,_)=>{});
                var pending=items.Session.Begin(new(FloorItemUseKind.Stone,1),FloorItemUsePurpose.HohMagicite,2,f.Time,"prior request",false,0,0,0,0);
                items.Session.BeginUpdate();
                for(int i=0;i<100;i++)Check(!items.TryDispatchFloorStone(2,f.Dd,"newly banned",FloorItemUsePurpose.HohMagiciteOvercap),"do not dispatch any other stone");
                Check(ReferenceEquals(items.Session.Pending,pending)&&!pending.AuthoritativeConfirmationObserved&&!items.HohBudget.MagiciteConfirmed&&!items.Session.DispatchedThisUpdate,"preserve actual pending evidence without claiming use or dispatch"); }),
            ("hoh82-production-passage-six-and-seven-reduce-cap-to-three", () => {
                foreach(byte floor in new byte[]{81,89,91,99}) foreach(byte progress in new byte[]{6,7}) {
                    using var f=new Fixture(floor:floor);f.Dd->PassageProgress=progress;
                    f.Main.Position=new(2,0,0);f.Main.Engaged=true;f.Add(10,new(3,0,0)).Engaged=true;
                    var next=f.Add(8,new(28,0,0));f.EnableMobTracking();f.TickMobs();
                    Check(f.Context.MobCombat.RotationPulling,"two held at middle stage may take third");
                    next.Engaged=true;f.TickMobs();
                    Check(!f.Context.MobCombat.RotationPulling&&f.Context.MobCombat.Consolidating&&f.Context.ControlledCombatEngagedCount==3,"three held means finish existing fight");
                } }),
            ("hoh82-production-near-open-progress-cancels-existing-pending-pull", () => {
                foreach(byte floor in new byte[]{81,89,91,99}) foreach(byte progress in new byte[]{8,10,11}) {
                    using var f=new Fixture(floor:floor);f.Main.Position=new(2,0,0);f.Main.Engaged=true;
                    f.Add(8,new(28,0,0));f.EnableMobTracking();f.TickMobs();
                    Check(f.Context.MobCombat.RotationPulling,"initial low-progress pending");
                    f.Dd->PassageProgress=progress;f.TickMobs();
                    Check(!f.Context.MobCombat.RotationPulling&&f.Context.MobCombat.OutputTarget?.GameObjectId==f.Main.Id&&f.Context.HohCombatTargets.IsEngaged(f.Main.Id),"new progress immediately returns to old hate without dropping target debt");
                } }),
            ("hoh82-production-passage-unknown-never-adds-pull", () => {
                using var f=new Fixture(floor:91);f.Main.Position=new(2,0,0);f.Main.Engaged=true;
                f.Add(8,new(28,0,0));f.EnableMobTracking();f.Dd->Floor=92;f.TickMobs();
                Check(f.Context.HohPassageProgress==null&&!f.Context.MobCombat.RotationPulling&&f.Context.MobCombat.OutputTarget?.GameObjectId==f.Main.Id,"mismatched native state clears existing fight only"); }),
            ("hoh82-production-consolidation-does-not-reopen-as-held-count-falls", () => {
                using var f=new Fixture(floor:91);f.Dd->PassageProgress=6;f.Main.Engaged=true;
                var a=f.Add(10,new(3,0,0));a.Engaged=true;var b=f.Add(11,new(4,0,0));b.Engaged=true;
                f.Add(8,new(28,0,0));f.EnableMobTracking();f.TickMobs();a.Hp=0;b.Hp=0;f.TickMobs();
                Check(f.Context.MobCombat.Consolidating&&!f.Context.MobCombat.RotationPulling,"do not refill to quota while clearing held group"); }),
            ("hoh82-production-progress-access-isolates-other-dungeons-and-floor-change", () => {
                using(var f=new Fixture(4,91)){f.Dd->PassageProgress=8;Check(f.Context.HohPassageProgress==null,"HoH native stages cannot leak into other dungeons");}
                using(var f=new Fixture(floor:91)){f.EnableMobTracking();f.Main.Engaged=true;f.Dd->PassageProgress=8;f.TickMobs();
                    typeof(DutyState).GetProperty("Floor")!.SetValue(f.Context.Duty,(byte)92);f.Dd->Floor=92;f.Dd->PassageProgress=0;f.Add(8,new(28,0,0));f.TickMobs();
                    f.Session.Enter(92,f.Dd->ActiveLayoutIndex);var centers=new Vector3?[25];centers[8]=Vector3.Zero;centers[9]=new(60,0,0);
                    Check(f.Session.Set(centers,f.PlayerPosition),"new floor map calibration");f.TickMobs();
                    Check(f.Context.MobCombat.RotationPulling,"new floor clears prior near-open consolidation");} }),
            ("hoh82-production-shield-hold-stops-owned-path-and-retains-failures", () => {
                using var f=new Fixture(floor:91);f.Main.Engaged=true;f.EnableMobTracking();f.TickMobs();f.Geometry.SightAt=p=>p.X>=3;f.RunUntil(()=>f.Geometry.Moves>0);
                f.Geometry.SightAt=null;f.Geometry.Clear=true;var failures=f.Context.CombatRecoveryBudget.TotalFailures;
                SetShieldHold(f);f.Update();
                Check(f.Geometry.Stops==1&&!f.Context.HohRecoveryMovementActive&&f.Controller.IsActive,"hold releases own movement while keeping bounded attempt");
                for(int i=0;i<10;i++){f.Time=f.Time.AddMilliseconds(200);SetShieldHold(f);f.Update();}
                Check(f.Geometry.Moves==1&&f.Context.CombatRecoveryBudget.TotalFailures==failures,"no repeated path dispatch or budget reset while holding"); }),
            ("hoh82-production-shield-hold-does-not-stop-blocked-target-approach", () => {
                using var f=new Fixture(floor:91);f.Main.Engaged=true;f.EnableMobTracking();f.TickMobs();f.Geometry.SightAt=p=>p.X>=3;f.RunUntil(()=>f.Geometry.Moves>0);SetShieldHold(f);f.Update();
                Check(f.Geometry.Stops==0&&f.Context.HohRecoveryMovementActive,"adapter lease alone cannot override actual blocked geometry"); }),
            ("hoh82-production-shield-and-mechanics-pauses-count-once", () => {
                using var f=new Fixture(floor:91);f.Main.Engaged=true;f.EnableMobTracking();f.TickMobs();f.Geometry.SightAt=p=>p.X>=3;f.RunUntil(()=>f.Geometry.Moves>0);
                var deadline=(DateTime)Get(f.Controller,"_deadline");f.Geometry.SightAt=null;f.Geometry.Clear=true;SetShieldHold(f);f.Update();
                f.Time=f.Time.AddSeconds(2);f.Controller.YieldMovement();f.Time=f.Time.AddSeconds(10);f.Context.HohShieldBreak.Reset();f.Update();
                Check((DateTime)Get(f.Controller,"_deadline")==deadline.AddSeconds(12),"2s shield plus10s mechanics cannot become24s"); }),
            ("hoh82-production-shield-expiry-resumes-bounded-recovery", () => {
                using var f=new Fixture(floor:91);f.Main.Engaged=true;f.EnableMobTracking();f.TickMobs();f.Geometry.SightAt=p=>p.X>=3;f.RunUntil(()=>f.Geometry.Moves>0);f.Geometry.SightAt=null;f.Geometry.Clear=true;
                SetShieldHold(f);f.Update();f.Time=f.Time.AddSeconds(1);f.Context.HohShieldBreak.Reset();f.Update();
                Check(Get(f.Controller,"_shieldHeldAt")==null,"expired lease releases hold");
                f.RunUntil(()=>f.Context.CombatRecoveryBudget.TotalFailures>0);
                Check(f.Events.Contains("held-target-recovery-attempt-failed"),"no damage after fallback still spends finite failure budget"); }),
            ("hoh81-production-low-floor-output-and-pursuit-share-switch-result", () => {
                using var f=new Fixture();f.Main.Position=new(40,0,0);f.Main.Engaged=true;
                var next=f.Add(8,new(5,0,0)); f.EnableMobTracking();f.TickMobs();
                var selection=Get(f.Context.MobCombat,"_outputSelection");
                Set(selection,"_unavailableAt",DateTime.UtcNow.AddSeconds(-2));
                f.TickMobs();
                Check(f.Context.MobCombat.OutputTarget?.GameObjectId==next.Id,"shared selector chooses legal hittable new target");
                Check(f.Context.ResolveHohCombatTarget(f.Main.Id,true)?.GameObjectId==next.Id && f.Context.HohCombatTargets.IsEngaged(f.Main.Id),"HoH must not override output or forget old hate");
                Check(f.Context.ResolveHohCombatTarget(next.Id,false)?.GameObjectId==f.Main.Id,"door-open existing hate wins"); }),
            ("hoh81-production-multipull-with-auto-attract-off-and-zero-skill", () => {
                foreach(byte floor in new byte[]{81,88,91,99}) {
                    using var f=new Fixture(floor:floor); f.Main.Position=new(2,0,0);f.Main.Engaged=true;
                    var next=f.Add(8,new(28,0,0)); f.EnableMobTracking();
                    f.Context.Configuration.NecromancerBandedAutoAttract=false;
                    f.Context.Configuration.NecromancerBandedAttractSkillId=0;
                    Set(f.Context.CombatAssist,"_cachedEngageRangeSkillId",0U);Set(f.Context.CombatAssist,"_cachedEngageRange",25f);
                    f.TickMobs();
                    Check(f.Context.MobCombat.RotationPulling && f.Context.MobCombat.OutputTarget?.GameObjectId==next.Id && !f.Context.MobCombat.OutputTargetHittable,"safe distant candidate must not require attract setting/action");
                    Check(f.Context.ResolveHohCombatTarget(f.Main.Id,true)?.GameObjectId==next.Id,"all consumers choose pending new target");
                    f.Context.MobCombat.TrySupplementLocalGroup(f.Context);
                    Check((ulong)Get(f.Context.MobCombat,"_pendingSupplement")==0,"legacy direct cast channel not entered");
                    next.Engaged=true;f.TickMobs();
                    Check(!f.Context.MobCombat.RotationPulling,"native hate confirms and releases pull");
                } }),
            ("hoh81-production-multipull-drives-validated-same-room-path", () => {
                using var f=new Fixture(floor:91);f.Main.Position=new(2,0,0);f.Main.Engaged=true;
                var next=f.Add(8,new(28,0,0));f.EnableMobTracking();f.TickMobs();
                f.ReadyTarget(next); f.Geometry.Target=next.Position;f.Geometry.Clear=true;
                for(int i=0;i<100 && f.Geometry.Moves==0;i++) {f.Time=f.Time.AddMilliseconds(200);f.Drive();}
                Check(f.Geometry.Moves>0 && f.Events.Contains("sight-recovery-path-started"),"actual DriveCombatTarget must dispatch validated path toward new target");
                Check(f.Context.ResolveHohCombatTarget(f.Main.Id,true)?.GameObjectId==next.Id,"approach does not restore stale old focus");
                Check(f.Geometry.LastPath.All(p=>RoomGraph.GetRoomIndexForPosition(f.Dd,p,[8,9],-1)==8),"no carrying old hate across rooms"); }),
            ("hoh81-production-multipull-respects-cap-door-priority-and-room", () => {
                using var f=new Fixture(floor:91);f.Main.Position=new(2,0,0);f.Main.Engaged=true;
                f.Add(8,new(60,0,0));f.EnableMobTracking();f.TickMobs();
                Check(!f.Context.MobCombat.RotationPulling,"remote room cannot create pull");
                var local=f.Add(9,new(25,0,0));f.TickMobs();Check(f.Context.MobCombat.RotationPulling,"same room may approach");
                typeof(DutyState).GetProperty("PassageOpen")!.SetValue(f.Context.Duty,true);f.TickMobs();
                Check(!f.Context.MobCombat.RotationPulling && f.Context.MobCombat.OutputTarget?.GameObjectId==f.Main.Id,"door immediately restores old hate");
                typeof(DutyState).GetProperty("PassageOpen")!.SetValue(f.Context.Duty,false);
                var mimic=f.Add(10,new(3,0,0),mimic:true);mimic.Engaged=true;f.TickMobs();
                Check(!f.Context.MobCombat.RotationPulling && f.Context.ResolveHohCombatTarget(local.Id,true)?.GameObjectId==mimic.Id,"priority preempts supplement");
                mimic.Hp=0;for(ulong id=20;id<25;id++)f.Add(id,new(4,0,0)).Engaged=true;
                f.Context.ObserveHohCombatTargets();f.TickMobs();
                Check(f.Context.ControlledCombatEngagedCount==6 && !f.Context.MobCombat.RotationPulling,"six held enemies close pending capacity"); }),
            ("hoh81-production-false-clear-no-damage-moves-and-spends-budget", () => {
                using var f=new Fixture(floor:75);f.Main.Position=new(20,0,0);f.Main.Engaged=true;f.Opportunity=true;f.Geometry.Clear=true;
                typeof(DutyState).GetProperty("IsInDuty")!.SetValue(f.Context.Duty,true);
                for(int i=0;i<250;i++)f.Step();
                Check(f.Geometry.Moves>0 && f.Context.CombatRecoveryBudget.TotalFailures>0,"50s clear geometry with zero damage cannot release and reset every frame");
                Check(f.Events.Contains("held-target-recovery-attempt-failed"),"unproductive attempts are charged");
                f.RunUntil(()=>f.Context.ExplorationRecovery.IsPaused);
                Check(f.Context.CombatRecoveryBudget.IsTargetExhausted(f.Main.Id,true,4) &&
                    f.Events.Contains("sight-recovery-stopped"),"four unproductive attempts must reach recoverable pause, preserving old hate"); }),
            ("hoh81-production-stop-at-range-keeps-real-output-verification", () => {
                using var f=new Fixture(floor:75);f.Main.Position=new(28,0,0);f.Main.Engaged=true;f.Geometry.Clear=true;
                f.RunUntil(()=>f.Geometry.Moves>0);f.PlayerPosition=new(3,0,0);f.Step();
                Check(f.Controller.IsActive && (Get(f.Controller,"_phase").ToString()=="Verifying"),"stop walking at usable range but keep attempt");
                f.Time=f.Time.AddMilliseconds(200);f.Context.HohCombatTargets.ObserveHit(f.Main.Id,f.Time,163,1,100);f.Update();
                Check(!f.Controller.IsActive && f.Events.Contains("sight-recovery-completed"),"native output completes recovery promptly"); }),
            ("hoh81-production-strength-gate-inventory-entry-and-steel-isolation", () => {
                foreach(uint dungeon in new uint[]{2,4}) {
                    using var f=new Fixture(dungeon,90);typeof(DutyState).GetProperty("IsBossFloor")!.SetValue(f.Context.Duty,true);
                    var items=new FloorItemExecutor(f.Context,new(),null,()=>FloorPhase.FloorActive,(_,_)=>{},(_,_)=>{});
                    foreach(uint hp in new uint[]{29,30,31}) {f.Main.Hp=hp;
                        Check(items.AllowsHohBossHp(FloorInitPlanner.StrengthPomanderSlotIndex)==(dungeon!=2 || hp>=30),"production HP gate has exact boundary and dungeon scope");
                        Check(items.AllowsHohBossHp(FloorInitPlanner.SteelPomanderSlotIndex),"steel has no boss-HP cutoff");
                        if(dungeon==2 && hp==29)Check(!items.IsPomanderAvailableForFloorUse(FloorInitPlanner.StrengthPomanderSlotIndex),"central item entry refuses before inventory dispatch");}
                    f.Actors.Clear();Check(items.AllowsHohBossHp(FloorInitPlanner.StrengthPomanderSlotIndex),"preparation before boss visible retained");
                    f.ForceNativeInCombat=true;
                    Check(dungeon!=2 || !items.AllowsHohBossHp(FloorInitPlanner.StrengthPomanderSlotIndex),"missing boss during combat fails closed");
                    Check(items.AllowsHohBossHp(FloorInitPlanner.SteelPomanderSlotIndex),"missing boss cannot block defence");
                    f.ForceNativeInCombat=false;
                    f.Main.Engaged=true;f.Actors.Add(f.Main);
                    f.Main.Hp=20;
                    Check(dungeon!=2 || HohRevivalBuffPolicy.ObserveItem(false,false,2,items.AllowsHohBossHp(FloorInitPlanner.StrengthPomanderSlotIndex),false,true,false)==HohRebuffItemState.Forbidden,"revival skips forbidden offensive refresh instead of waiting20s");
                    items.Dispose();
                } }),
            ("hoh80-production61-mimic-overrides-preferred-ordinary-target", () => {
                using var f = new Fixture(floor: 61); f.Main.Engaged = true;
                var mimic = f.Add(1073771921, new(2,0,0), mimic: true); mimic.Engaged = true; mimic.Hp = 1;
                Check(f.Context.ResolveHohCombatTarget(f.Main.Id, true)?.GameObjectId == mimic.Id,
                    "actual shared resolver cannot switch back to high-HP ordinary target");
                mimic.Hp = 0;
                Check(f.Context.ResolveHohCombatTarget(f.Main.Id, true)?.GameObjectId == f.Main.Id,
                    "release after death, continue existing hate"); }),
            ("hoh80-production61-tracks-priority-even-before-native-combat-flag", () => {
                using var f = new Fixture(floor: 61); f.Main.Engaged = true;
                var mimic = f.Add(1073771921, new(2,0,0), mimic: true); mimic.Engaged = true;
                // Target-of-player is authoritative engagement even before the native combat flag settles.
                f.ForceNativeOutOfCombat = true;
                typeof(DutyState).GetProperty("IsInDuty")!.SetValue(f.Context.Duty, true);
                Set(f.Context.MobCombat, "_dungeon", 2U); Set(f.Context.MobCombat, "_floor", 61);
                Set(f.Context.MobCombat, "<HohCombatRoom>k__BackingField", 8);
                f.Context.MobCombat.Tick(f.Context);
                Check(f.Context.MobCombat.Focus?.GameObjectId == mimic.Id && f.Context.MobCombat.PriorityFight &&
                    f.Context.MobCombat.MovementTaskPaused, "production tick activates fight and exploration suspension on61");
                Check(!f.Context.MobCombat.Consolidating, "tracking does not activate high-floor gathering");
                mimic.Hp = 0; Set(f.Context.MobCombat, "_nextScan", DateTime.MinValue);
                f.Context.MobCombat.Tick(f.Context);
                Check(!f.Context.MobCombat.PriorityFight && !f.Context.MobCombat.MovementTaskPaused,
                    "production tick releases task pause when mimic dies"); }),
            ("hoh77-room-position-ties-are-independent-of-reachable-list-order", () => {
                int Resolve(int[] rooms) { int best = -1; float distance = float.MaxValue;
                    foreach (int room in rooms) if (RoomGraph.IsBetterRoomCandidate(25, room, distance, best)) { best=room; distance=25; }
                    return best; }
                Check(Resolve([13,12]) == 12 && Resolve([12,13]) == 12, "entrance ties use a stable room index"); }),
            ("hoh77-production-low-floor-gates-chase-assist-and-selection-to-current-room", () => {
                using var f = new Fixture(); var remote = f.Add(8, new(60,0,0));
                Check(f.Context.IsNewCombatTargetAllowed(f.Enemy), "local target eligible");
                Check(!f.Context.IsNewCombatTargetAllowed(remote.Actor), "shared new-pull gate forbids remote room");
                f.Hard = f.Soft = remote.Actor;
                Check(!f.Context.EnsureHohCombatTarget(remote.Actor) && f.Hard == null && f.Soft == null, "selection cannot bypass room gate");
                f.Chase.CanSelectNewTarget = f.Context.IsNewCombatTargetAllowed;
                var chosen = f.Chase.GetClearingTarget(f.Dd, f.Graph, 8, f.PlayerPosition, out _, preferredTargetId: 8);
                Check(chosen?.GameObjectId == 7, "even remote preferred chase target is revalidated"); }),
            ("hoh77-production-entrance-mapping-works-before-deep-floors", () => {
                using var f = new Fixture(); f.PlayerPosition = new(34,0,0); f.Main.Position = new(40,0,0);
                Check(RoomGraph.GetLocalPlayerRoomIndex(f.Dd) == 8 && f.Context.ResolveHohCombatRoom(f.Dd, [8,9]) == 9,
                    "ordinary31+ uses the same mapped room as mobs at entrance");
                Check(f.Context.IsNewCombatTargetAllowed(f.Enemy), "pull begins at entrance, not center60"); }),
            ("hoh77-production-missing-room-map-fails-closed", () => {
                using var f = new Fixture(); f.Session.Clear(); f.PlayerPosition = new(float.NaN,0,0);
                Check(!f.Context.IsNewCombatTargetAllowed(f.Enemy), "no global fallback when mapping unavailable"); }),
            ("hoh77-production-existing-hate-survives-room-restriction-and-plan-deferral", () => {
                using var f = new Fixture(); f.Main.Position = new(60,0,0); f.Main.Engaged = true;
                f.Context.SyncHohCombatScope(); f.Context.HohApproachProgress.Defer(8, f.Time);
                Check(f.Context.IsNewCombatTargetAllowed(f.Enemy) && f.Context.EnsureHohCombatTarget(f.Enemy) == false && f.Hard == f.Enemy,
                    "existing hate selected across rooms; 250ms readback grace still applies"); }),
            ("hoh77-production-safe-ordinary-incidental-pull-reaches-native-dispatch", () => {
                using var f = new Fixture(); f.Add(8, new(10,0,0)); f.Geometry.Clear = false;
                f.RunUntil(() => f.Geometry.Moves > 0);
                Check(f.Events.Contains("sight-recovery-path-started") && f.Context.CombatRecoveryBudget.TotalFailures == 0,
                    "real Update-search-query-safety-move chain accepts ordinary blockers"); }),
            ("hoh77-production-centaur-route-remains-hard-protected", () => {
                using var f = new Fixture(); f.Add(8, new(10,0,0), centaur: true);
                f.RunUntil(() => f.Events.Contains("hoh-approach-plan-deferred"));
                Check(f.Geometry.Moves == 0 && f.Context.CombatRecoveryBudget.TotalFailures == 0 && f.Context.HohApproachProgress.RequiresPatrol(8),
                    "forbidden route becomes patrol handoff, not ignored safety"); }),
            ("hoh77-production-engaged-recovery-cannot-use-idle-incidental-pull-exemption", () => {
                using var f = new Fixture(); f.Main.Engaged = true; f.Add(8, new(10,0,0));
                f.RunUntil(() => f.Context.CombatRecoveryBudget.TotalFailures > 0);
                Check(f.Geometry.Commands.All(p => PassageCombatPolicy.SafeHohRecoveryPath(p,Vector3.Zero,[new(10,0,0)],160)) && !f.Events.Contains("hoh-approach-plan-deferred"), "old hate retains strict safety and real failure budget"); }),
            ("hoh77-production-invalid-plans-force-room-patrol-before-another-idle-target", () => {
                using var f = new Fixture(); f.Add(8, new(12,0,5)); f.Geometry.ValidPaths = false;
                f.RunUntil(() => f.Events.Contains("hoh-approach-plan-deferred"));
                Check(!f.Context.IsNewCombatTargetAllowed(f.Enemy), "same-room retry blocked beyond suppression timer");
                f.Time = f.Time.AddSeconds(30); f.Context.HohApproachProgress.Tick(8, f.Time);
                Check(f.Context.HohApproachProgress.RequiresPatrol(8), "changing target / wall time cannot renew pull permission");
                f.PlayerPosition = new(34,0,0); f.Main.Position = new(40,0,0); f.Context.HohApproachProgress.Tick(9, f.Time);
                Check(f.Context.IsNewCombatTargetAllowed(f.Enemy), "a real next-room entry allows rescan"); }),
            ("hoh77-production-native-command-failure-is-not-a-free-plan", () => {
                using var f = new Fixture(); f.Geometry.ThrowOnMove = true;
                f.RunUntil(() => f.Context.CombatRecoveryBudget.TotalFailures > 0);
                Check(!f.Events.Contains("hoh-approach-plan-deferred"), "dispatch failure still charged"); }),
            ("hoh77-production-command-return-without-native-start-is-charged", () => {
                using var f = new Fixture(); f.Geometry.ReportRunning = false;
                f.RunUntil(() => f.Geometry.Moves > 0); f.Time = f.Time.AddSeconds(1.1); f.Update();
                Check(f.Context.CombatRecoveryBudget.TotalFailures == 1 && !f.Events.Contains("hoh-approach-plan-deferred"), "dispatch is not proof native path started"); }),
            ("hoh77-production-path-running-is-confirmed-separately", () => {
                using var f = new Fixture(); f.RunUntil(() => f.Geometry.Moves > 0); f.Update();
                Check(f.Events.Contains("hoh-approach-navigation-confirmed"), "native acknowledgement recorded independently"); }),
            ("hoh77-production-older-simplemove-query-must-finish-before-dispatch", () => {
                using var f = new Fixture(); f.Geometry.SimpleMovePending = true;
                for (int i=0; i<30; i++) f.Step();
                Check(f.Geometry.Moves == 0 && f.Controller.IsActive, "retain verified query while other owner finishes");
                f.Geometry.SimpleMovePending = false; f.RunUntil(() => f.Geometry.Moves > 0);
                Check(f.Geometry.Moves == 1, "one final movement command"); }),
            ("hoh77-production-output-prefix-keeps-detour-in-native-command", () => {
                using var f = new Fixture(); f.Main.Position = new(28,0,0);
                f.Geometry.BuildPath = (_, end) => [new(0,0,20),new(end.X,0,20),end];
                f.Geometry.SightAt = p => p.X >= 18 && p.Z == 20;
                f.RunUntil(() => f.Geometry.Moves > 0);
                Check(f.Geometry.LastPath[0] == new Vector3(0,0,20) && f.Geometry.LastPath[^1].Z == 20 && f.Geometry.LastPath[^1].X >= 18,
                    "truncate route at first firing point without shortcutting the wall"); }),
            ("hoh77-production-protected-cast-stops-active-path", () => {
                using var f = new Fixture(); f.RunUntil(() => f.Geometry.Moves > 0);
                f.Casting = true; f.Update();
                Check(f.Geometry.Stops == 1 && f.Controller.HoldsProtectedCast && !f.Context.HohRecoveryMovementActive,
                    "cast cannot be interrupted by the previously issued path");
                for(int i=0;i<8;i++){f.CastElapsed += .2f;f.Step();}
                Check(f.Geometry.Moves == 1 && f.Context.CombatRecoveryBudget.TotalFailures == 0, "do not reissue path during first protected cast"); }),
            ("hoh77-production-protected-cast-stops-query-dispatch", () => {
                using var f = new Fixture(); f.RunUntil(() => f.Geometry.Queries > 0);
                f.Casting = true; f.Update();
                Check(f.Geometry.Moves == 0 && f.Controller.HoldsProtectedCast, "completed path query cannot move during first cast"); }),
            ("hoh77-production-cast-completion-rechecks-and-resumes-bounded-approach", () => {
                using var f = new Fixture(); f.RunUntil(() => f.Geometry.Moves > 0);
                f.Casting = true; f.Update(); for(int i=0;i<10;i++){f.CastElapsed+=.2f;f.Step();}
                f.Casting = false; f.Step(); for(int i=0;i<8;i++)f.Step();
                f.RunUntil(() => f.Geometry.Moves > 1);
                Check(!f.Controller.HoldsProtectedCast && f.Context.CombatRecoveryBudget.TotalFailures == 0,
                    "after output grace, revalidate rather than reuse stale movement"); }),
            ("hoh77-production-first-unconfirmed-selection-cannot-blindly-chase-or-break-cast", () => {
                using var f=new Fixture();f.Casting=true;f.Drive();
                Check(f.Navigator.Moves==0&&f.Geometry.Moves==0&&f.Hard==f.Enemy,
                    "selection readback retains cast; no unverified fallback chase"); }),
            ("hoh77-production-confirmed-cast-hold-survives-final-chase-branch", () => {
                using var f=new Fixture();f.Casting=true;f.ReadySelection();f.Drive();
                Check(f.Controller.HoldsProtectedCast&&f.Navigator.Moves==0&&f.Geometry.Moves==0,
                    "the final hold/chase decision honors the controller's cast protection"); }),
            ("hoh77-production-blocked-selected-target-enters-validated-approach", () => {
                using var f=new Fixture();f.ReadySelection();f.Drive();
                Check(f.Controller.IsActive&&f.Navigator.Moves==0,"wall cannot send target down the direct unverified chase fallback"); }),
            ("hoh77-production-low-floor-patrol-plans-next-entrance-not-center", () => {
                using var f=new Fixture();f.Context.HohApproachProgress.Defer(8,f.Time);
                Check(f.Patrol(),"ordinary floor handles no-target patrol");
                Set(Get(f.FloorDriver,"_hohRoomAdvance"),"_enteredAt",DateTime.UtcNow.AddSeconds(-2));
                Check(f.Patrol()&&(int)Get(f.FloorDriver,"_hohEntryTo")==9,"connected next room selected");
                var endpoint=(Vector3)Get(f.FloorDriver,"_hohEntryDestination");
                Check(endpoint.X is >=32 and <=36 && endpoint!=new Vector3(60,0,0),"entry-side observation point"); }),
            ("hoh77-production-mechanics-and-cast-pause-do-not-double-extend-deadline", () => {
                using var f=new Fixture();f.RunUntil(()=>f.Geometry.Moves>0);
                var deadline=(DateTime)Get(f.Controller,"_deadline");
                f.Casting=true;f.Update();f.Time=f.Time.AddSeconds(2);f.Controller.YieldMovement();
                f.Time=f.Time.AddSeconds(10);f.Casting=false;f.Update();
                Check((DateTime)Get(f.Controller,"_deadline")==deadline.AddSeconds(12),"2sec cast plus10sec mechanics, counted once"); }),
            ("hoh77-production-throwing-path-query-is-a-bounded-planning-deferral", () => {
                using var f=new Fixture();f.Geometry.ThrowOnQuery=true;
                f.RunUntil(()=>f.Events.Contains("hoh-approach-plan-deferred"));
                Check(f.Geometry.Moves==0&&f.Context.CombatRecoveryBudget.TotalFailures==0,"query failure stays on the planning side of dispatch boundary"); }),
            ("hoh77-production-repeated-offensive-casts-do-not-hold-forever", () => {
                using var f = new Fixture(); f.Casting=true;
                for(int i=0;i<35;i++){f.CastElapsed=(i%10)*.2f;f.Step();}
                Check(!f.Controller.HoldsProtectedCast && f.Controller.IsActive, "two unconfirmed casts still allow bounded repair"); }),
            ("hoh77-production-verified-native-hit-ends-recovery-and-patrol-debt", () => {
                using var f = new Fixture(); f.RunUntil(() => f.Geometry.Moves > 0);
                f.Context.HohApproachProgress.Defer(8,f.Time);
                f.Context.HohCombatTargets.ObserveHit(7,f.Time,163,1,0); f.Update();
                Check(!f.Controller.IsActive && !f.Context.HohApproachProgress.RequiresPatrol(8) && f.Geometry.Stops == 1,
                    "native direct zero-damage hit remains valid shield evidence"); }),
            ("hoh77-production-same-room-eligibility-does-not-change-pt-or-boss", () => {
                foreach(var (d,n) in new(uint,byte)[]{(4,36),(2,90),(1,36)}) {
                    using var f=new Fixture(d,n);f.Main.Position=new(60,0,0);
                    Check(f.Context.IsNewCombatTargetAllowed(f.Enemy),"legacy voluntary-target gate untouched");} }),
            ("hoh77-plan-watchdog-stops-a-stationary-free-retry-loop", () => {
                var s=Progress();s.Defer(8,Now);
                for(int i=0;i<=300;i++){s.Tick(8,Now.AddMilliseconds(i*200));if(i%10==0)s.Defer(8,Now.AddMilliseconds(i*200));}
                Check(s.Exhausted,"target changes cannot refresh60 active seconds"); }),
            ("hoh77-plan-watchdog-does-not-charge-time-paused", () => {
                var s=Progress();s.Defer(8,Now);s.Tick(8,Now);s.Tick(8,Now.AddHours(1));
                Check(s.ActiveNoProgress==TimeSpan.Zero&&!s.Exhausted,"manual pause is not active patrol time"); }),
            ("hoh77-plan-watchdog-revisited-rooms-do-not-create-infinite-progress", () => {
                var s=Progress();s.Tick(8,Now);s.Defer(8,Now);s.Tick(9,Now.AddSeconds(.2));
                for(int i=1;i<=300;i++)s.Tick(i%2==0?8:9,Now.AddSeconds(.2+i*.2));
                Check(s.Exhausted,"only a previously unvisited room or verified output renews progress"); }),
            ("hoh77-plan-watchdog-new-floor-and-verified-output-clear-debt", () => {
                var s=Progress();s.Defer(8,Now);s.ObserveOutput();Check(!s.RequiresPatrol(8),"real output resets");
                s.Defer(8,Now);s.BeginFloor(FarmingMode.FreeExploration,2,37);Check(!s.RequiresPatrol(8),"floor reset"); }),
            ("hoh77-production-36-eight-targets-and-four-continues-never-charge-planning", Replay36),
        ];
        foreach(var(name,test)in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
    private static HohApproachProgressState Progress()
    {var s=new HohApproachProgressState();s.BeginFloor(FarmingMode.FreeExploration,2,36);return s;}

    private static void SetShieldHold(Fixture f)
    {
        f.Context.HohShieldBreak.BeginFloor(FarmingMode.FreeExploration,2,f.Context.Duty.Floor,FsdRotationProvider.AeAssist);
        var frame=new HohShieldBreakFrame(f.Main.Id,true,true,true,true,true,false,true,false,false);
        f.Context.HohShieldBreak.Update(frame,f.Time.AddSeconds(-3));
        f.Context.HohShieldBreak.Update(frame,f.Time);
    }

    private static void Replay36()
    {
        // Recorded first-sweep IDs from18:58:33–39+08; timing and geometry below are synthetic.
        // Run the actual Update/search/endpoint-rejection/escalation/Fail chain, not Fail alone.
        using var f=new Fixture();f.Geometry.ValidPaths=false;
        ulong[] targets=[1073765651,1073765643,1073765652,1073765650,1073765644,1073765642,1073765648,1073765654];
        for(int resume=0;resume<4;resume++) {
            f.Context.CombatRecoveryBudget.ResumeManually();
            foreach(var id in targets) {
                f.Main.Id=id;f.Controller.ResetEngagedTargetProgress();
                // Each synthetic plan starts from a fresh room in order to stress the failure
                // pipeline even though real same-room acquisition is now blocked after the first.
                f.Context.HohApproachProgress.ObserveOutput();
                int count=f.Events.Count(e=>e=="hoh-approach-plan-deferred");
                f.RunUntil(()=>f.Events.Count(e=>e=="hoh-approach-plan-deferred")>count);
            }
            Check(f.Context.CombatRecoveryBudget.TotalFailures==0&&!f.Context.CombatRecoveryBudget.Exhausted,"no eight-plan floor exhaustion after continue");
        }
        Check(f.Geometry.Moves==0&&f.Events.Count(e=>e=="hoh-approach-plan-deferred")==32,"all pre-dispatch rejections are bounded plans");
    }

    private sealed class ActorState(ulong id,Vector3 position)
    {
        internal ulong Id=id;internal Vector3 Position=position;internal bool Engaged,Centaur,Mimic;internal uint Hp=100;
        internal readonly FFXIVClientStructs.FFXIV.Client.Game.StatusManager* Statuses=
            (FFXIVClientStructs.FFXIV.Client.Game.StatusManager*)NativeMemory.AllocZeroed(8192);
        internal bool Petrified {get=>Statuses->Status[0].StatusId==HohPetrificationState.StoneCurseStatus;set {Statuses->Status[0].StatusId=(ushort)(value?HohPetrificationState.StoneCurseStatus:0);Statuses->NumValidStatuses=(byte)(value?1:0);}}
        internal IBattleChara ActorObject=null!;internal IBattleChara Actor=>ActorObject;
    }

    private sealed class Geometry : ICombatPositionNavigation
    {
        public bool Ready=>true;
        public bool SimpleMovePending{get;set;}
        public bool PathRunning=>ReportRunning&&Moves>Stops;
        internal bool ReportRunning=true,ValidPaths=true,ThrowOnMove,ThrowOnQuery,Clear;
        internal int Moves,Queries,Stops;
        internal List<Vector3> LastPath=[];
        internal readonly List<List<Vector3>> Commands=[];
        internal Vector3 Target=new(24,0,0);
        internal Func<Vector3,bool>? SightAt;
        internal Func<Vector3,Vector3,List<Vector3>>? BuildPath;
        public Vector3? GroundPoint(Vector3 p,float h=.4f,float v=1.6f)=>p;
        public Task<List<Vector3>> FindPath(Vector3 a,Vector3 b,CancellationToken c)
        {Queries++;if(ThrowOnQuery)throw new InvalidOperationException("offline query failure");return Task.FromResult(ValidPaths?(BuildPath?.Invoke(a,b)??[b]):new List<Vector3>());}
        public void Move(List<Vector3> path){Moves++;if(ThrowOnMove)throw new InvalidOperationException("offline native dispatch failure");LastPath=path.ToList();Commands.Add(LastPath);}
        public void Stop(){Stops++;}
        public CombatSight TraceSight(Vector3 a,Vector3 b,out Vector3 hit){hit=default;return (b!=Target || (SightAt?.Invoke(a)??Clear))?CombatSight.Clear:CombatSight.Blocked;}
        public bool SafeAgainstMechanics(IReadOnlyList<Vector3> p,Vector3 s)=>true;
        public bool HasStandingMargin(Vector3 p,Vector3 t,float r,float a,float b)=>true;
        public bool HasGroundStandingMargin(Vector3 p,float m=CombatPositionPolicy.StandingMargin)=>true;
    }
    private sealed class Fixture : IDisposable
    {
        internal readonly InstanceContentDeepDungeon* Dd=(InstanceContentDeepDungeon*)NativeMemory.AllocZeroed((nuint)sizeof(InstanceContentDeepDungeon));
        private readonly UIState* _ui=(UIState*)NativeMemory.AllocZeroed((nuint)sizeof(UIState));
        private readonly nint _previousUi=UIState.Addresses.Instance.Value;
        private readonly void* _statuses=NativeMemory.AllocZeroed(8192);
        private readonly List<(PropertyInfo,object?)> _services=[];
        internal readonly RunContext Context;
        internal readonly IPlayerCharacter Player;
        internal readonly ActorState Main;
        internal IBattleChara Enemy=>Main.Actor;
        internal readonly List<ActorState> Actors=[];
        internal readonly Geometry Geometry=new();
        internal readonly EnemyChaseHelper Chase=new();
        internal readonly NormalFloorGraphSnapshot Graph;
        internal readonly CombatPositionRecoveryController Controller;
        internal readonly FloorExplorationController FloorDriver;
        internal readonly NullNavigator Navigator=new();
        internal readonly HohMapSession Session;
        internal readonly List<string> Events=[];
        internal readonly List<string> Evidence=[];
        internal DateTime Time=Now;
        internal Vector3 PlayerPosition;
        internal bool Casting, ForceNativeOutOfCombat, ForceNativeInCombat, Opportunity;
        internal float CastElapsed;
        internal IGameObject? Hard,Soft;
        internal Fixture(uint dungeon=2,byte floor=36)
        {
            UIState.Addresses.Instance.Value=(nint)_ui;
            Dd->DeepDungeonId=(byte)dungeon;Dd->Floor=floor;
            Dd->Party[0].EntityId=42;Dd->Party[0].RoomIndex=8;
            Dd->MapData[8]=InstanceContentDeepDungeon.RoomFlags.ConnectionE;
            Dd->MapData[9]=InstanceContentDeepDungeon.RoomFlags.ConnectionW;
            Context=new(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null),()=>(nint)Dd);
            Context.FarmingPlan=new(FarmingMode.FreeExploration,SaveUse.Create,1,1,false,false,true,true,false);
            typeof(DutyState).GetProperty("DungeonId")!.SetValue(Context.Duty,dungeon);
            typeof(DutyState).GetProperty("Floor")!.SetValue(Context.Duty,floor);
            var statusList=(StatusList)Activator.CreateInstance(typeof(StatusList),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[(nint)_statuses],null)!;
            Player=Fake<IPlayerCharacter>((m,_)=>m.Name switch {
                "get_GameObjectId"=>42UL,"get_Position"=>PlayerPosition,"get_HitboxRadius"=>.5f,
                "get_IsCasting"=>Casting,"get_CastActionId"=>Casting?163U:0U,"get_CastTargetObjectId"=>Main?.Id??7UL,
                "get_CurrentCastTime"=>CastElapsed,"get_StatusList"=>statusList,_=>Default(m)});
            Main=Add(7,new(24,0,0));
            Service("Condition",Fake<ICondition>((m,a)=>m.Name=="get_Item"?(ConditionFlag)a![0]! switch {
                ConditionFlag.Casting=>Casting,ConditionFlag.InCombat=>ForceNativeInCombat || !ForceNativeOutOfCombat && Actors.Any(e=>e.Engaged),_=>false}:Default(m)));
            Service("GameObjects",Fake<IObjectTable>((m,_)=>m.Name switch {"get_LocalPlayer"=>Player,
                "GetEnumerator"=>Actors.Select(e=>(IGameObject)e.Actor).GetEnumerator(),_=>Default(m)}));
            Service("TargetManager",Fake<ITargetManager>((m,a)=>{switch(m.Name){
                case "get_Target":return Hard;case "set_Target":Hard=(IGameObject?)a![0];return null;
                case "get_SoftTarget":return Soft;case "set_SoftTarget":Soft=(IGameObject?)a![0];return null;default:return Default(m);}}));
            Set(Context.MobCombat,"_outputSight",(Func<Vector3,Vector3,bool>)((_,_)=>true));
            // Actor status managers below provide real native status evidence for clear-flow tests.
            var type=typeof(MapPos).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Map.MapPosGeneration")!;
            Session=(HohMapSession)type.GetField("_hohSession",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
            Session.Enter(floor,Dd->ActiveLayoutIndex);var centers=new Vector3?[25];centers[8]=Vector3.Zero;centers[9]=new(60,0,0);
            Check(Session.Set(centers,PlayerPosition),"test map");
            var hops=new int[25,25];hops[8,9]=hops[9,8]=1;Graph=new(8,8,[8,9],hops);
            var nav=new NavigationHelper(Navigator,()=>false,()=>Time,_=>{},_=>{},()=>false);
            Controller=new(Context,nav,Chase,Graph,Context.CombatRecoveryBudget,null,_=>{},(n,d)=>{Events.Add(n);if(Evidence.Count<80)Evidence.Add(n+":"+System.Text.Json.JsonSerializer.Serialize(d));},Geometry,()=>Time);
            Set(Context.CombatAssist,"_cachedEngageRangeSkillId",Context.Configuration.NecromancerBandedAttractSkillId);
            Set(Context.CombatAssist,"_cachedEngageRange",25f);
            ((Dictionary<uint,bool>)Get(Controller,"_offensiveActions"))[163]=true;
            Context.SyncHohCombatScope();
            FloorDriver=(FloorExplorationController)RuntimeHelpers.GetUninitializedObject(typeof(FloorExplorationController));
            Set(FloorDriver,"_ctx",Context);Set(FloorDriver,"_navHelper",nav);Set(FloorDriver,"_chaseHelper",Chase);
            Set(FloorDriver,"_setStatus",(Action<string>)(_=>{}));Set(FloorDriver,"RecordReplayEvent",(Action<string,object>)((n,_)=>Events.Add(n)));
            Set(FloorDriver,"<DungeonId>k__BackingField",dungeon);Set(FloorDriver,"<Floor>k__BackingField",floor);
            Set(FloorDriver,"<Kind>k__BackingField",FloorRuntimeKind.Normal);Set(FloorDriver,"<NormalGraph>k__BackingField",Graph);
            Set(FloorDriver,"_hohRoomAdvance",new HohRoomAdvanceState());Set(FloorDriver,"_hohEntryGeometry",Geometry);
            Set(FloorDriver,"_hohEntryProgress",new HohRoomProgressWatch());Set(FloorDriver,"_hohRejectedEntrances",new List<Vector3>());
            Set(FloorDriver,"_hohEntryFrom",-1);Set(FloorDriver,"_hohEntryTo",-1);
            var driver=new NavigationDriver(nav);
            Set(driver,"_hoh",new HohValidatedNavigation(()=>true,p=>p,Geometry.FindPath,Geometry.Move,Geometry.Stop,_=>{},()=>Time,()=>false));
            Set(FloorDriver,"_navDriver",driver);
        }
        internal ActorState Add(ulong id,Vector3 p,bool centaur=false,bool mimic=false)
        {
            var a=new ActorState(id,p){Centaur=centaur,Mimic=mimic};
            var statuses=(StatusList)Activator.CreateInstance(typeof(StatusList),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[(nint)a.Statuses],null)!;
            a.ActorObject=Fake<IBattleChara>((m,_)=>m.Name switch {
                "get_GameObjectId"=>a.Id,"get_Position"=>a.Position,"get_CurrentHp"=>a.Hp,"get_MaxHp"=>100U,
                "get_IsTargetable"=>true,"get_HitboxRadius"=>1f,"get_TargetObjectId"=>a.Engaged?42UL:0UL,
                "get_ObjectKind"=>ObjectKind.BattleNpc,"get_SubKind"=>(byte)BattleNpcSubKind.Combatant,
                "get_BaseId"=>a.Centaur?8995U:8996U,"get_NameId"=>a.Mimic?7394U:a.Centaur?7584U:0U,"get_StatusList"=>statuses,_=>Default(m)});
            Actors.Add(a);return a;
        }
        internal void Update(){Geometry.Target=Main.Position;Controller.Update(Dd,new(Main.Position,Main.Position,1,Main.Id,Main.Engaged?EnemyChaseTargetReason.Aggro:EnemyChaseTargetReason.Nearest,8,8,0),Enemy,Player,Main.Engaged,Opportunity);}
        internal void EnableMobTracking(){typeof(DutyState).GetProperty("IsInDuty")!.SetValue(Context.Duty,true);Set(Context.MobCombat,"_outputSight",(Func<Vector3,Vector3,bool>)((_,_)=>true));}
        internal void TickMobs(){Set(Context.MobCombat,"_nextScan",DateTime.MinValue);Context.ObserveHohCombatTargets();Context.MobCombat.Tick(Context);}
        internal void ReadyTarget(ActorState target){Hard=target.Actor;Context.HohCombatTargets.ObserveSelection(DateTime.UtcNow.AddSeconds(-1),target.Id,target.Id,0);}
        internal void ReadySelection(){Context.HohCombatTargets.ObserveSelection(DateTime.UtcNow.AddSeconds(-1),Main.Id,Main.Id,0);}
        internal void Drive()=>typeof(FloorExplorationController).GetMethod("DriveCombatTarget",Private)!.Invoke(FloorDriver,[Pointer.Box(Dd,typeof(InstanceContentDeepDungeon*)),new EnemyChaseTarget(Main.Position,Main.Position,1,Main.Id,EnemyChaseTargetReason.Nearest,8,8,0),Player,Controller]);
        internal bool Patrol()=>(bool)typeof(FloorExplorationController).GetMethod("TryUpdateHohAdjacentRoomPatrol",Private)!.Invoke(FloorDriver,[Pointer.Box(Dd,typeof(InstanceContentDeepDungeon*)),Player])!;
        internal bool Retreat()=>(bool)typeof(FloorExplorationController).GetMethod("TryUpdateHohCentaurRetreat",Private)!.Invoke(FloorDriver,[Pointer.Box(Dd,typeof(InstanceContentDeepDungeon*)),false])!;
        internal void Step(){Time=Time.AddMilliseconds(200);Update();}
        internal void RunUntil(Func<bool> done){for(int i=0;i<600&&!done();i++)Step();Check(done(),"production pipeline did not reach expected bounded state: "+string.Join(',',Events.TakeLast(6))+"; "+string.Join(';',Evidence.Take(12)));}
        private void Service(string n,object v){var p=typeof(RunContext).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Service")!.GetProperty(n,BindingFlags.Static|BindingFlags.NonPublic)!;_services.Add((p,p.GetValue(null)));p.SetValue(null,v);}
        public void Dispose(){Controller.Dispose();Context.Dispose();Session.Clear();foreach(var(p,v)in _services.AsEnumerable().Reverse())p.SetValue(null,v);UIState.Addresses.Instance.Value=_previousUi;NativeMemory.Free(_ui);NativeMemory.Free(_statuses);foreach(var a in Actors)NativeMemory.Free(a.Statuses);NativeMemory.Free(Dd);}
    }
    private sealed class NullNavigator : INavigator {internal int Moves;public bool PathfindAndMoveTo(Vector3 p,bool a){Moves++;return true;}public bool RepathTo(Vector3 p,bool a)=>true;public void CancelAll(){} }
    private static void Set(object o,string n,object v)=>o.GetType().GetField(n,Private)!.SetValue(o,v);
    private static object Get(object o,string n)=>o.GetType().GetField(n,Private)!.GetValue(o)!;
    private static object? Default(MethodInfo m)=>m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
    private static T Fake<T>(Func<MethodInfo,object?[]?,object?> h)where T:class{var p=DispatchProxy.Create<T,Proxy>();((Proxy)(object)p).Handler=h;return p;}
    public class Proxy:DispatchProxy{internal Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);}
}
