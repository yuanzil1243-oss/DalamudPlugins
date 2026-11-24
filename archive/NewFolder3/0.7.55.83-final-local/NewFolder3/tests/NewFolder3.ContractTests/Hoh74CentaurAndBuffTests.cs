using System.Collections;
using System.Numerics;
using System.Reflection;
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

namespace NewFolder3;

internal static class Hoh74CentaurAndBuffTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static readonly HohCentaurObstacle Horse = new(99, Vector3.Zero, 2);
    private static bool Pull(Vector3 p, Vector3 t, params HohCentaurObstacle[] horses) =>
        HohCentaurPullPolicy.AllowsPull(p, .5f, t, 1, horses, out _, out _);
    internal static int Run()
    {
        (string, Action)[] cases =
        [
            ("hoh74-81-to89-do-not-stop-exploration-on-first-hate", () => {
                foreach(int floor in Enumerable.Range(81,9)){
                    var state=new CombatConsolidationState();
                    int threshold=HohHighFloorPolicy.ConsolidationThreshold(FarmingMode.FreeExploration,2,floor,false);
                    for(int count=1;count<6;count++)Check(!state.Update(true,count,threshold),"continue advancing and dealing damage below group ceiling");
                    Check(state.Update(true,6,threshold),"clean the gathered group at the fixed ceiling");
                    Check(state.Update(true,2,threshold)&&!state.Update(true,0,threshold),"finish this group then continue");} }),
            ("hoh74-early-group-can-pull-within-full-action-range", () => {
                Check(HohHighFloorPolicy.CanSupplement(FarmingMode.FreeExploration,2,86,1,1,true,24,25,false,false,false),"not limited by deep-floor12m or configured2 ceiling");
                Check(!HohHighFloorPolicy.CanSupplement(FarmingMode.FreeExploration,2,86,6,1,true,24,25,false,false,false),"no unbounded accumulation");
                Check(!HohHighFloorPolicy.CanSupplement(FarmingMode.FreeExploration,2,86,1,1,true,26,25,false,false,false),"actual action range remains required"); }),
            ("hoh75-active-group-change-keeps-pt-and-boss-combat-isolated", () => {
                foreach(var(dungeon,floor)in new(uint,int)[]{(4,95),(2,90),(2,100),(2,79)})
                    Check(HohHighFloorPolicy.ConsolidationThreshold(FarmingMode.FreeExploration,dungeon,floor,false)==1,"PT, earlier floors and boss thresholds unchanged");
                Check(HohHighFloorPolicy.ConsolidationThreshold(FarmingMode.FreeExploration,2,86,true)==1,"open door immediately finishes hate without more pulling");
                Check(!HohHighFloorPolicy.CanSupplement(FarmingMode.FreeExploration,4,95,1,2,true,24,25,false,false,false),"PT still uses12m"); }),
            ("hoh75-all-deep-floors-actively-gather-ordinary-mobs", () => {
                foreach(int floor in Enumerable.Range(91,9)){
                    Check(HohHighFloorPolicy.ActiveGroup(FarmingMode.FreeExploration,2,floor),"explicit active group");
                    Check(HohHighFloorPolicy.ConsolidationThreshold(FarmingMode.FreeExploration,2,floor,false)==6,"no first-hate consolidation");
                    Check(HohHighFloorPolicy.CanSupplement(FarmingMode.FreeExploration,2,floor,4,2,true,24,25,false,false,false),"ignore cautious configured2 and12m");}
                foreach(int floor in new[]{79,80,90,100})Check(!HohHighFloorPolicy.ActiveGroup(FarmingMode.FreeExploration,2,floor),"earlier/boss/terminal isolation"); }),
            ("hoh75-active-group-capacity-cannot-override-centaur-exclusion", () => {
                foreach(int floor in Enumerable.Range(94,4)){
                    using var f=new Fixture(2,floor);f.EnemyPosition=new(3,0,0);
                    Check(HohHighFloorPolicy.CanSupplement(FarmingMode.FreeExploration,2,floor,4,2,true,24,25,false,false,false),"ordinary group may exceed old2 limit");
                    Check(!f.Context.IsNewCombatTargetAllowed(f.Enemy),"centaur-near target remains forbidden despite free group capacity");
                    Check(!f.Context.IsNewCombatTargetAllowed(f.HorseActor),"centaur itself remains forbidden");} }),
            ("hoh75-all-deep-floors-stop-supplementing-during-priority-fight", () => {
                foreach(int floor in Enumerable.Range(91,9))
                    Check(!HohHighFloorPolicy.CanSupplement(FarmingMode.FreeExploration,2,floor,1,2,true,10,25,false,true,false),"engaged centaur or stoneskin priority still stops ordinary supplement"); }),
            ("hoh74-combined-four-clear-reserve-prefers-magicite", () => {
                foreach(int magicite in Enumerable.Range(0,4))foreach(int stone in Enumerable.Range(0,4)){
                    Check(!HohItemPolicy.ClearStockAllowsUse(86,magicite,stone,true),"ordinary use never spends preferred magicite before91");
                    Check(HohItemPolicy.ClearStockAllowsUse(86,magicite,stone,false)==(magicite+stone>4&&stone>0),"only surplus stone curse may be spent");}
                Check(HohItemPolicy.AllowPetrification(86,false,false,false,false,false,2,3),"3magicite2stone may spend one stone");
                Check(!HohItemPolicy.AllowPetrification(86,false,false,false,false,false,1,3),"3magicite1stone is the reserve");
                Check(!HohItemPolicy.AllowPetrification(86,false,false,false,false,false,2,2),"2plus2 is also protected"); }),
            ("hoh74-reserve-keeps-emergency-and-confirmed-overcap-exceptions", () => {
                Check(!HohItemPolicy.ShouldUseMagicite(84,3,false,false,true,false),"do not spend magicite merely on a hard earlier floor");
                Check(HohItemPolicy.ShouldUseMagicite(84,1,false,true,true,false),"emergency may spend reserve");
                Check(HohItemPolicy.AllowPetrification(86,false,false,false,false,true,3,0),"confirmed replacement keeps existing full chest transaction");
                foreach(int floor in new[]{91,92,93})Check(!HohItemPolicy.AllowPetrification(floor,false,false,false,false,false,3,3),"save for94through97 even when total six"); }),
            ("hoh74-petrified-approach-gate-keeps-distant-engaged-target", () => {
                using var f=new Fixture();f.EnemyTargetsPlayer=true;f.Target=f.SoftTarget=f.Enemy;
                f.Context.HohPetrification.BeginFloor(2,97);f.Context.HohPetrification.ConfirmUse(DateTime.UtcNow);
                f.Context.EnforceWisdomTargetGate();
                Check(f.Target==f.Enemy&&f.SoftTarget==f.Enemy&&!f.Context.NormalCombatOutputSuppressed,"rotation enforcement no longer clears targets because approach is incomplete"); }),
            ("hoh74-player-by-centaur-cannot-pull-a-distant-ordinary-mob", () => {
                Check(!Pull(new(3,0,0),new(40,0,0),Horse),"unsafe pull origin must not be rescued by a distant target"); }),
            ("hoh74-ordinary-mob-by-centaur-is-not-a-voluntary-target", () => {
                Check(!Pull(new(-40,0,0),new(3,0,0),Horse),"not selecting the centaur itself was insufficient"); }),
            ("hoh74-pull-checks-all-centaurs-and-hitbox-edges", () => {
                Check(!Pull(new(-40,0,0),new(16,0,0),Horse),"center distance16 is not fifteen metres outside both hitboxes");
                Check(!Pull(new(-40,0,0),new(40,0,0),Horse,new(100,new(42,0,0),2)),"second centaur must also exclude the target"); }),
            ("hoh74-burrow-height-cannot-hide-centaur-proximity", () => {
                Check(!Pull(new(3,-7,0),new(40,0,0),Horse),"packet/burrow vertical offset is not horizontal safety"); }),
            ("hoh74-safe-pull-endpoints-do-not-forbid-transiting-past-centaur", () => {
                Check(Pull(new(-40,0,0),new(40,0,0),Horse),"this policy screens pulling positions, not the intervening route"); }),
            ("hoh74-finite-clear-pull-without-centaur-remains-allowed", () => {
                Check(Pull(Vector3.Zero,new(5,0,0)),"ordinary rooms retain existing pull rules");
                Check(!Pull(new(float.NaN,0,0),Vector3.Zero),"invalid position cannot authorize pulling"); }),
            ("hoh74-production-new-pull-gate-blocks-unsafe-origin", () => {
                using var f=new Fixture();f.PlayerPosition=new(3,0,0);
                Check(!f.Context.IsHohCentaurPullSafe(f.Enemy)&&!f.Context.IsNewCombatTargetAllowed(f.Enemy),"shared new-target entry must apply the origin guard"); }),
            ("hoh74-production-target-selection-cannot-bypass-centaur-exclusion", () => {
                using var f=new Fixture();f.EnemyPosition=new(3,0,0);f.Target=f.SoftTarget=f.Enemy;
                Check(!f.Context.EnsureHohCombatTarget(f.Enemy)&&f.Target==null&&f.SoftTarget==null,"selection rechecks and clears the rejected automatic target");
                Check(f.Context.CombatPreparationSequence==0&&!f.Context.NormalCombatOutputSuppressed,"no prepare, no AE stop"); }),
            ("hoh74-production-stale-hard-and-soft-targets-are-rechecked", () => {
                using var f=new Fixture();f.Target=f.SoftTarget=f.Enemy;Check(f.Context.IsHohCentaurPullSafe(f.Enemy),"initially safe");
                f.HorsePosition=f.EnemyPosition;f.Context.ClearUnsafeHohPullTargets();
                Check(f.Target==null&&f.SoftTarget==null,"moving centaur invalidates old unengaged selection"); }),
            ("hoh74-production-old-hate-remains-an-obligation-near-centaur", () => {
                using var f=new Fixture();f.EnemyPosition=Vector3.Zero;f.EnemyTargetsPlayer=true;f.Target=f.SoftTarget=f.Enemy;
                Check(f.Context.IsHohCentaurPullSafe(f.Enemy),"do not abandon existing hate");f.Context.ClearUnsafeHohPullTargets();
                Check(f.Target==f.Enemy&&f.SoftTarget==f.Enemy,"existing combat output must remain possible"); }),
            ("hoh74-production-dead-centaur-no-longer-excludes-pulls", () => {
                using var f=new Fixture();f.EnemyPosition=Vector3.Zero;f.HorseAlive=false;
                Check(f.Context.IsHohCentaurPullSafe(f.Enemy),"dead enemies have no pull exclusion"); }),
            ("hoh74-production-other-dungeons-and-earlier-hoh-are-isolated", () => {
                foreach(var(dungeon,floor)in new(uint,int)[]{(4,97),(1,97),(2,89)}){
                    using var f=new Fixture(dungeon,floor);f.PlayerPosition=f.EnemyPosition=Vector3.Zero;f.Target=f.Enemy;
                    Check(f.Context.IsHohCentaurPullSafe(f.Enemy),"scope must not leak");f.Context.ClearUnsafeHohPullTargets();
                    Check(dungeon==2 ? f.Target==null : f.Target==f.Enemy,"other dungeons untouched; earlier HoH now rejects an unknown room independently of centaur exclusion");} }),
            ("hoh74-steel-is-held-on91-and-until-a-centaur-is-engaged", () => {
                foreach(int floor in Enumerable.Range(91,8))foreach(int count in new[]{1,2,3})foreach(bool overflow in new[]{false,true})
                    Check(!HohItemPolicy.AllowPomander(floor,3,count,false,true,overflow),"even full inventory or low HP is not a centaur engagement"); }),
            ("hoh74-steel-reserve-is-available-immediately-to-engaged-centaur", () => {
                foreach(int floor in Enumerable.Range(91,9))
                    Check(HohItemPolicy.AllowPomander(floor,3,1,false,false,false,centaurEngaged:true),"native centaur hate releases last steel"); }),
            ("hoh74-boss-steel-and-earlier-reserves-stay-unchanged", () => {
                foreach(int floor in Enumerable.Range(1,9).Select(x=>x*10))Check(HohItemPolicy.AllowPomander(floor,3,1,true,false,false),"boss use kept");
                Check(!HohItemPolicy.AllowPomander(81,3,2,false,true,false)&&HohItemPolicy.AllowPomander(81,3,3,false,false,false),"earlier two-steel reserve retained"); }),
            ("hoh74-high-floor-strength-can-use-last-stock-during-combat", () => {
                foreach(int floor in Enumerable.Range(91,8)){
                    Check(HohItemPolicy.AllowPomander(floor,2,1,false,false,false),"no phantom99 boss reserve");
                    Check(HohItemPolicy.CombatBuffToMaintain(floor,false,true,3,false,1,false)==2,"combat maintenance chooses strength without spending steel");}
                Check(ProgressionItemPolicy.AllowsCombat(2,false,FloorItemUsePurpose.HighFloorBuff,2),"real executor purpose permits combat"); }),
            ("hoh74-centaur-steel-precedes-strength-and-can-be-used-in-combat", () => {
                Check(HohItemPolicy.CombatBuffToMaintain(95,true,true,1,false,1,false)==3,"steel first against ranged centaur");
                Check(ProgressionItemPolicy.AllowsCombat(3,false,FloorItemUsePurpose.HighFloorBuff,2),"combat item channel allowed"); }),
            ("hoh74-existing-buffs-and-recent-confirmations-are-not-overwritten", () => {
                Check(HohItemPolicy.CombatBuffToMaintain(95,true,true,1,true,1,false)==2,"active steel leaves room for missing strength");
                Check(HohItemPolicy.CombatBuffToMaintain(95,true,true,1,true,1,true)==null,"no overlap");
                Check(HohItemPolicy.CombatBuffToMaintain(95,false,false,1,false,1,false)==null,"open door without combat does not spend buffs"); }),
            ("hoh74-high-floor-buff-maintenance-does-not-claim-other-floors", () => {
                foreach(int floor in new[]{0,81,89,90,100})Check(HohItemPolicy.CombatBuffToMaintain(floor,true,true,3,false,3,false)==null,"keep existing boss/earlier paths");
                Check(HohItemPolicy.CombatBuffToMaintain(95,true,true,0,false,0,false)==null,"no inventory means no choice"); }),
            ("hoh74-final-combat-floor-releases-unused-steel-without-centaur", () => {
                Check(HohItemPolicy.AllowPomander(99,3,1,false,false,false),"no remaining99 boss reserve");
                Check(HohItemPolicy.CombatBuffToMaintain(99,false,true,1,false,0,false)==3,"final ordinary combat can use steel");
                Check(HohItemPolicy.CombatBuffToMaintain(99,false,false,1,false,0,false)==null,"already-open empty floor does not waste buff"); }),
            ("hoh74-serenity-final-floor-cleanses-real-harm-with-last-stock", () => {
                Check(Hoh70FloorPolicy.AllowsSerenity(99,1,false,true),"ability ban, gloom or other native harmful effect releases final stock");
                Check(!Hoh70FloorPolicy.AllowsSerenity(99,1,false,false)&&!Hoh70FloorPolicy.AllowsSerenity(99,0,true,true),"no effect or no item is not a use");
                foreach(int floor in Enumerable.Range(91,8))Check(!Hoh70FloorPolicy.AllowsSerenity(floor,1,false,true),"before99 preserve item-ban reserve"); }),
            ("hoh74-entrance-stops-are-inset-from-boundary-not-room-center", () => {
                var points=HohRoomEntryPolicy.Seeds(Vector3.Zero,new(60,0,0)).ToArray();
                Check(points.Length==15&&points.All(p=>p.X>=32&&p.X<=36&&p!=new Vector3(60,0,0)),"short entrance-side observation positions");
                Check(!HohRoomEntryPolicy.Seeds(new(float.NaN,0,0),Vector3.One).Any(),"invalid geometry must not plan"); }),
            ("hoh74-clean-room-chest-priority-is-scoped-and-never-interrupts-combat", () => {
                Check(HohRoomEntryPolicy.PreferLocalChests(true,false,false,false,false,true),"clear current room before moving on");
                Check(!HohRoomEntryPolicy.PreferLocalChests(false,false,false,false,false,true)&&
                    !HohRoomEntryPolicy.PreferLocalChests(true,false,true,false,false,true)&&
                    !HohRoomEntryPolicy.PreferLocalChests(true,false,false,true,false,true)&&
                    !HohRoomEntryPolicy.PreferLocalChests(true,false,false,false,true,true),"keep PT, combat, stone clear and pull obligations"); }),
            ("hoh74-local-chest-absent-from-plan-is-inserted-without-losing-other-rooms", () => {
                RoomPlanEntry[] route=[new(3,false,true),new(8,false,true)];
                var result=HohRoomEntryPolicy.PrioritizeLocalChests(route,9);
                Check(result.Length==3&&result[0].RoomIndex==9&&result[0].ShouldSearchChests&&result.Skip(1).SequenceEqual(route),"local flag must never execute remote head");
                result=HohRoomEntryPolicy.PrioritizeLocalChests(result,9);
                Check(result.Length==3&&result.Count(e=>e.RoomIndex==9)==1,"stable queue without duplicate visits"); }),
            ("hoh74-fake-door-switches-after-nine-real-arrival-seconds", () => {
                var state=new HohPassageCandidates();var now=DateTime.UtcNow;
                HohPassageCandidate[] doors=[new(1,Vector3.Zero,false),new(2,new(10,0,0),false)];
                state.TrySelect(doors,Vector3.Zero,now,out _,out _,out _);
                for(int i=1;i<=89;i++)state.TrySelect(doors,Vector3.Zero,now.AddMilliseconds(i*100),out _,out _,out _);
                Check(state.Current?.Id==1,"8.9sec still permits real activation");
                state.TrySelect(doors,Vector3.Zero,now.AddSeconds(9),out var selected,out _,out bool expired);
                Check(expired&&selected.Id==2,"9sec unresponsive candidate switches"); }),
            ("hoh74-recorded-real-seven-second-passage-is-not-a-fake", () => {
                var state=new HohPassageCandidates();var now=DateTime.UtcNow;
                HohPassageCandidate[] doors=[new(1,Vector3.Zero,true)];
                for(int i=0;i<=75;i++)state.TrySelect(doors,Vector3.Zero,now.AddMilliseconds(i*100),out _,out _,out _);
                Check(state.Current?.Id==1&&state.RejectedCount==0,"recorded97,98,99 real activation takes7.2–7.4sec"); }),
            ("hoh74-loading-and-combat-never-consume-door-activation-budget", () => {
                var state=new HohPassageCandidates();var now=DateTime.UtcNow;
                HohPassageCandidate[] doors=[new(1,Vector3.Zero,true)];
                state.TrySelect(doors,Vector3.Zero,now,out _,out _,out _);
                for(int i=1;i<20;i++)state.TrySelect(doors,Vector3.Zero,now.AddSeconds(i),out _,out _,out _,activationEligible:false);
                state.TrySelect(doors,Vector3.Zero,now.AddSeconds(30),out var selected,out _,out bool expired);
                Check(!expired&&selected.Id==1&&state.RejectedCount==0,"confirmed loading/combat pause cannot reject real portal"); }),
            ("hoh74-final-door-open-exits-without-more-chests-or-exploration", () => {
                var input=new ObjectiveArbiterSnapshot(false,true,true,true,false,true,false,false,true,true,
                    RequiredChestWork:true,RecoverActiveChest:true);
                var final=HohHighFloorPolicy.ApplyFinalFloorPassagePolicy(FarmingMode.FreeExploration,2,99,input);
                Check(ObjectiveArbiter.Decide(final).PrimaryObjective==FloorObjectiveKind.EnterPassage,"even pending chest must yield to final exit");
                Check(!final.RequiredChestWork&&!final.RecoverActiveChest&&!final.VisibleBandedChest&&final.MandatoryHoardTerminal,"no new optional supplies for100"); }),
            ("hoh74-final-door-open-still-finishes-existing-hate-first", () => {
                var input=new ObjectiveArbiterSnapshot(false,false,false,false,true,true,false,true,true,false,RequiredChestWork:true);
                var final=HohHighFloorPolicy.ApplyFinalFloorPassagePolicy(FarmingMode.FreeExploration,2,99,input);
                Check(ObjectiveArbiter.Decide(final).PrimaryObjective==FloorObjectiveKind.FinishCombatBeforePassage,"keep existing combat obligation"); }),
            ("hoh74-final-door-policy-does-not-change-other-floors-or-pt", () => {
                var input=new ObjectiveArbiterSnapshot(false,false,false,false,true,true,false,false,true,true,RequiredChestWork:true);
                foreach(var(dungeon,floor)in new(uint,int)[]{(4,99),(2,98),(2,90),(2,100)})
                    Check(HohHighFloorPolicy.ApplyFinalFloorPassagePolicy(FarmingMode.FreeExploration,dungeon,floor,input)==input,"local99 only");
                Check(HohHighFloorPolicy.ApplyFinalFloorPassagePolicy(FarmingMode.FreeExploration,2,99,input with {PassageOpen=false})==input with {PassageOpen=false},"closed door still needs work"); }),
            ("hoh74-world-room-mapping-uses-same-basis-as-enemy-at-entrance", WorldRoom),
            ("hoh74-petrification-reserved-until94-with-confirmed-overcap-exception", () => {
                foreach(int floor in Enumerable.Range(91,3)){
                    Check(!HohItemPolicy.AllowPomander(floor,15,3,false,false,false)&&!HohItemPolicy.AllowPetrification(floor,false,false,false,false,false,3),"not at entry even when full");
                    Check(HohItemPolicy.AllowPomander(floor,15,3,false,false,true)&&HohItemPolicy.AllowPetrification(floor,false,false,false,false,true,3),"confirmed new capped chest keeps replacement");}
                foreach(int floor in Enumerable.Range(94,6))Check(HohItemPolicy.AllowPetrification(floor,false,false,false,false,false,1),"last stock available from94"); }),
            ("hoh74-petrification-still-respects-door-boss-and-magicite", () => {
                Check(!HohItemPolicy.AllowPetrification(94,false,true,false,false,true,3),"door open");
                Check(!HohItemPolicy.AllowPetrification(94,false,false,true,false,true,3),"magicite already used");
                Check(!HohItemPolicy.AllowPetrification(90,true,false,false,false,true,3),"boss excluded"); }),
            ("hoh74-ineffective-witching-is-disabled-in-burrow-local-edition", () => {
                foreach(int floor in new[]{21,89,91,94,99})foreach(bool overflow in new[]{false,true})
                    Check(!HohItemPolicy.AllowPomander(floor,9,3,false,true,overflow),"no emergency or overcap entry may waste Witching"); }),
            ("hoh74-long-adjacent-retreat-keeps-default-local-recovery-bound", () => {
                Vector3[] path=[Vector3.Zero,new(25,0,0),new(55,0,0)];var end=path[^1];
                Check(!CombatPositionPolicy.ValidatePathEndpoint(Vector3.Zero,end,path,out float length,out var why)&&length==55&&why=="path-too-long","ordinary recovery still35");
                Check(CombatPositionPolicy.ValidatePathEndpoint(Vector3.Zero,end,path,out length,out why,HohCentaurRetreatPolicy.MaximumPathLength)&&length==55,"HoH centaur endpoint can use64");
                Check(HohCentaurRetreatPolicy.SafeRoute(path,Vector3.Zero,[],0,1,p=>p.X<27?0:1),"complete55m safe adjacent route accepted"); }),
            ("hoh74-centaur-retreat-still-rejects-too-long-or-partial-route", () => {
                Vector3[] path=[Vector3.Zero,new(65,0,0)];
                Check(!CombatPositionPolicy.ValidatePathEndpoint(Vector3.Zero,path[^1],path,out _,out var why,HohCentaurRetreatPolicy.MaximumPathLength)&&why=="path-too-long","not unbounded");
                Check(!CombatPositionPolicy.ValidatePathEndpoint(Vector3.Zero,new(60,0,0),new[]{Vector3.Zero,new Vector3(55,0,0)},out _,out why,HohCentaurRetreatPolicy.MaximumPathLength)&&why=="path-endpoint-mismatch","partial path still rejected"); }),
            ("hoh74-centaur-retreat-logs-distinct-unsafe-route-reasons", () => {
                Vector3[] path=[Vector3.Zero,new(25,0,0),new(55,0,0)];
                Check(!HohCentaurRetreatPolicy.TrySafeRoute(path,Vector3.Zero,[new(20,0,0)],0,1,p=>p.X<27?0:1,out var why)&&why=="unengaged-enemy-on-local-path","idle-mob reason retained");
                Check(!HohCentaurRetreatPolicy.TrySafeRoute(path,Vector3.Zero,[],0,1,p=>p.X>10&&p.X<15?2:p.X<27?0:1,out why)&&why=="retreat-crosses-third-room","sampled third-room reason retained"); }),
        ];
        foreach(var(name,test)in cases){test();Console.WriteLine("  ok  "+name);}return cases.Length;
    }

    private static unsafe void WorldRoom()
    {
        using var f=new Fixture();f.PlayerPosition=new(34,0,0);
        InstanceContentDeepDungeon dd=default;dd.DeepDungeonId=2;dd.Floor=97;
        dd.Party[0].EntityId=42;dd.Party[0].RoomIndex=8;
        var type=typeof(MapPos).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Map.MapPosGeneration")!;
        var session=(HohMapSession)type.GetField("_hohSession",BindingFlags.Static|BindingFlags.NonPublic)!.GetValue(null)!;
        session.Enter(97,dd.ActiveLayoutIndex);var centers=new Vector3?[25];centers[8]=Vector3.Zero;centers[9]=new(60,0,0);
        Check(session.Set(centers,f.PlayerPosition),"deterministic local geometry");
        try {
            Check(RoomGraph.GetLocalPlayerRoomIndex(&dd)==8,"native party can still report previous room");
            Check(f.Context.ResolveHohCombatRoom(&dd,new[]{8,9})==9,"same world position maps to entrance room9");
            dd.DeepDungeonId=4;Check(f.Context.ResolveHohCombatRoom(&dd,new[]{8,9})==8,"PT keeps native party room");
            dd.DeepDungeonId=2;dd.Floor=90;Check(f.Context.ResolveHohCombatRoom(&dd,new[]{8,9})==8,"boss room logic unchanged");
        } finally {session.Clear();}
    }
    private sealed unsafe class Fixture : IDisposable
    {
        internal readonly RunContext Context=new(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null),()=>0);
        internal Vector3 PlayerPosition=new(-40,0,0),EnemyPosition=new(40,0,0),HorsePosition;
        internal bool HorseAlive=true,EnemyTargetsPlayer;
        internal IGameObject? Target,SoftTarget;
        internal readonly IPlayerCharacter Player;
        internal readonly IBattleChara Enemy,HorseActor;
        private readonly List<(PropertyInfo,object?)> _services=[];
        private readonly nint _previousUi=UIState.Addresses.Instance.Value;
        private readonly UIState* _ui=(UIState*)NativeMemory.AllocZeroed((nuint)sizeof(UIState));
        internal Fixture(uint dungeon=2,int floor=97)
        {
            UIState.Addresses.Instance.Value=(nint)_ui;
            typeof(DutyState).GetProperty("DungeonId")!.SetValue(Context.Duty,dungeon);
            typeof(DutyState).GetProperty("Floor")!.SetValue(Context.Duty,(byte)floor);
            Context.FarmingPlan=new(FarmingMode.FreeExploration,SaveUse.Create,1,1,false,false,true,true,false);
            Player=Fake<IPlayerCharacter>((m,_)=>m.Name switch{"get_GameObjectId"=>42UL,"get_Position"=>PlayerPosition,"get_HitboxRadius"=>.5f,_=>Default(m)});
            Enemy=Fake<IBattleChara>((m,_)=>m.Name switch{"get_GameObjectId"=>7UL,"get_Position"=>EnemyPosition,"get_HitboxRadius"=>1f,"get_TargetObjectId"=>EnemyTargetsPlayer?42UL:0UL,"get_CurrentHp"=>100U,"get_IsTargetable"=>true,"get_ObjectKind"=>ObjectKind.BattleNpc,"get_SubKind"=>(byte)BattleNpcSubKind.Combatant,"get_BaseId"=>8996U,_=>Default(m)});
            HorseActor=Fake<IBattleChara>((m,_)=>m.Name switch{"get_GameObjectId"=>99UL,"get_Position"=>HorsePosition,"get_HitboxRadius"=>2f,"get_CurrentHp"=>HorseAlive?100U:0U,"get_IsDead"=>!HorseAlive,"get_IsTargetable"=>true,"get_ObjectKind"=>ObjectKind.BattleNpc,"get_SubKind"=>(byte)BattleNpcSubKind.Combatant,"get_BaseId"=>8995U,"get_NameId"=>7584U,_=>Default(m)});
            Service("Condition",Fake<ICondition>((m,_)=>m.Name=="get_Item"?false:Default(m)));
            Service("GameObjects",Fake<IObjectTable>((m,_)=>m.Name switch{"get_LocalPlayer"=>Player,"GetEnumerator"=>((IEnumerable<IGameObject>)new[]{(IGameObject)Enemy,HorseActor}).GetEnumerator(),_=>Default(m)}));
            Service("TargetManager",Fake<ITargetManager>((m,a)=>{switch(m.Name){case "get_Target":return Target;case "set_Target":Target=(IGameObject?)a![0];return null;case "get_SoftTarget":return SoftTarget;case "set_SoftTarget":SoftTarget=(IGameObject?)a![0];return null;default:return Default(m);}}));
        }
        private void Service(string name,object value)
        {var p=typeof(RunContext).Assembly.GetType("DeepDungeon.Fsd.Dalamud.Service")!.GetProperty(name,BindingFlags.Static|BindingFlags.NonPublic)!;_services.Add((p,p.GetValue(null)));p.SetValue(null,value);}
        public void Dispose(){Context.Dispose();foreach(var(p,v)in _services.AsEnumerable().Reverse())p.SetValue(null,v);UIState.Addresses.Instance.Value=_previousUi;NativeMemory.Free(_ui);}
    }
    private static object? Default(MethodInfo m)=>m.ReturnType.IsValueType?Activator.CreateInstance(m.ReturnType):null;
    private static T Fake<T>(Func<MethodInfo,object?[]?,object?> handler)where T:class
    {var p=DispatchProxy.Create<T,Proxy>();((Proxy)(object)p).Handler=handler;return p;}
    public class Proxy:DispatchProxy
    {internal Func<MethodInfo,object?[]?,object?> Handler=null!;protected override object? Invoke(MethodInfo? m,object?[]? a)=>Handler(m!,a);}
}
