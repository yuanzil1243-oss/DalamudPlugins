using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace NewFolder3;

internal static class Hoh64HighFloorTests
{
    private static readonly DateTime Now = new(2026,11,21,21,0,0,DateTimeKind.Utc);
    private static void Check(bool v,string why){if(!v)throw new Exception(why);}
    internal static int Run()
    {
        (string,Action)[] tests=
        [
            ("hoh64-controlled-pulling-is-only-hoh91-through99",()=>{
                foreach(int floor in Enumerable.Range(1,100))Check(HohHighFloorPolicy.Enabled(FarmingMode.FreeExploration,2,floor)==(floor is >=91 and <=99),"exact new scope");
                foreach(var mode in new FarmingMode?[]{null,FarmingMode.DeepProgression,FarmingMode.HoardDiscovery})Check(!HohHighFloorPolicy.Enabled(mode,2,91),"no unintended mode adoption");}),
            ("hoh64-pt-controlled-floor-range-remains91-through98",()=>{
                foreach(var mode in new[]{FarmingMode.FreeExploration,FarmingMode.DeepProgression})foreach(int floor in Enumerable.Range(1,100))
                    Check(MobCombatPolicy.UsesControlledHighFloor(mode,4,floor)==MobCombatPolicy.HighFloorEnabled(mode,4,floor),"PT result is unchanged");
                Check(!MobCombatPolicy.UsesControlledHighFloor(FarmingMode.FreeExploration,1,91),"other dungeons unchanged");}),
            ("hoh64-hoh-and-pt-limits-are-independent-settings",()=>{
                var s=new FsdCombatSettings();Check(s.GetHighFloorEnemyLimit(2)==3 && s.GetHighFloorEnemyLimit(4)==3,"default ceiling3");
                s.HohHighFloorEnemyLimit=1;Check(s.GetHighFloorEnemyLimit(2)==1 && s.GetHighFloorEnemyLimit(4)==3,"changing HoH must not change PT");
                s.HighFloorEnemyLimit=5;Check(s.GetHighFloorEnemyLimit(2)==1 && s.GetHighFloorEnemyLimit(4)==5,"changing PT must not change HoH");}),
            ("hoh64-loaded-invalid-limits-are-clamped-at-use",()=>{
                var s=new FsdCombatSettings{HohHighFloorEnemyLimit=100};Check(s.GetHighFloorEnemyLimit(2)==6,"upper bound");s.HohHighFloorEnemyLimit=-4;Check(s.GetHighFloorEnemyLimit(2)==1,"lower bound");}),
            ("hoh64-one-existing-enemy-pauses-exploration-before-ceiling",()=>{
                var s=new CombatConsolidationState();Check(s.Update(true,1,1),"reuse PT: any engagement holds movement, not only count3");
                Check(s.Update(true,2,1) && s.Update(true,1,1),"do not resume exploration when a group merely drops below3");Check(!s.Update(true,0,1),"only cleared engagement releases exploration");}),
            ("hoh64-open-door-does-not-drop-current-engagement",()=>{
                var s=new CombatConsolidationState();s.Update(true,2,1);
                Check(s.Update(true,1,1) && PassageCombatPolicy.AllowsNewTarget(false,true,true,1,true),"finish old hate after opening");
                Check(!PassageCombatPolicy.AllowsNewTarget(false,true,true,1,false,true),"door prohibits even an otherwise valid local supplement");}),
            ("hoh64-local-supplement-stops-at-limit-and-never-chases",()=>{
                Check(PassageCombatPolicy.CanSupplement(1,3,true,8,25,false,false,false),"stationary same-room supplement can use spare capacity");
                Check(!PassageCombatPolicy.CanSupplement(3,3,true,8,25,false,false,false) && !PassageCombatPolicy.CanSupplement(1,3,false,8,25,false,false,false) && !PassageCombatPolicy.CanSupplement(1,3,true,13,25,false,false,false),"ceiling, room boundary,12m no-movement limit");}),
            ("hoh64-limit-is-not-a-pull-quota",()=>{
                Check(!PassageCombatPolicy.CanSupplement(0,3,true,8,25,false,false,false),"local group supplements require existing hate");
                var s=new CombatConsolidationState();s.Update(true,1,1);Check(!s.Update(true,0,1),"do not wait for a full3-monster group");}),
            ("hoh64-priority-or-dangerous-fights-block-supplements",()=>{
                Check(!PassageCombatPolicy.CanSupplement(1,3,true,8,25,true,false,false) && !PassageCombatPolicy.CanSupplement(1,3,true,8,25,false,true,false) && !PassageCombatPolicy.CanSupplement(1,3,true,8,25,false,false,true),"reuse verified PT priority and solo gates");}),
            ("hoh64-room-bound-path-allows-nearby-reposition",()=>{
                Check(HohHighFloorPolicy.PathStaysInRoom(Vector3.Zero,new[]{new Vector3(4,0,0),new Vector3(8,0,2)},3,_=>3),"same room recovery still possible");}),
            ("hoh64-room-bound-path-rejects-leave-and-return-detour",()=>{
                Check(!HohHighFloorPolicy.PathStaysInRoom(Vector3.Zero,new[]{new Vector3(2,0,0)},3,p=>p.X is >.7f and <1.3f?4:3),"sample all segments, not just endpoints");}),
            ("hoh64-room-bound-path-rejects-other-or-unknown-room",()=>{
                Check(!HohHighFloorPolicy.PathStaysInRoom(Vector3.Zero,new[]{new Vector3(3,0,0)},-1,_=>3),"unknown engagement room");
                Check(!HohHighFloorPolicy.PathStaysInRoom(Vector3.Zero,new[]{new Vector3(3,0,0)},3,p=>p.X>1?4:3),"cannot chase old hate into next room");}),
            ("hoh64-missing-native-enemy-does-not-erase-tracked-hate",()=>{
                var s=new HohCombatTargetState();s.BeginFloor(FarmingMode.FreeExploration,2,91);s.Observe(new[]{new HohCombatCandidate(7,true,true,true,true,false,0,100,4)},true,Now);
                s.Observe(Array.Empty<HohCombatCandidate>(),true,Now.AddSeconds(1));Check(s.EngagedCount==1 && s.Select(Array.Empty<HohCombatCandidate>(),0,true)==0,"hold while reacquiring original entity");}),
            ("hoh64-native-deaths-release-group-one-at-a-time",()=>{
                var s=new HohCombatTargetState();s.BeginFloor(FarmingMode.FreeExploration,2,91);s.ObserveHit(7,Now,1,1,1);s.ObserveHit(8,Now,1,2,1);Check(s.EngagedCount==2,"two live obligations");
                s.Observe(new[]{new HohCombatCandidate(7,false,true,false,true,false,0,0,4)},true,Now);Check(s.EngagedCount==1,"one remains");
                s.Observe(new[]{new HohCombatCandidate(8,false,true,false,true,false,0,0,4)},false,Now);Check(s.EngagedCount==0,"cleared group");}),
            ("hoh64-controlled-combat-does-not-replace-original-hoh-chest-route",()=>{
                const BindingFlags flags=BindingFlags.Instance|BindingFlags.NonPublic;
                foreach(var (dungeon,expected)in new (uint,bool)[]{(2,false),(4,true)}) {
                    using var context=new RunContext(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
                    context.FarmingPlan=new(FarmingMode.FreeExploration,SaveUse.Create,1,1,false,false,true,true,false);
                    var t=typeof(FloorExplorationController);var c=RuntimeHelpers.GetUninitializedObject(t);
                    t.GetField("_ctx",flags)!.SetValue(c,context);t.GetField("<Kind>k__BackingField",flags)!.SetValue(c,FloorRuntimeKind.Normal);
                    t.GetField("<DungeonId>k__BackingField",flags)!.SetValue(c,dungeon);t.GetField("<Floor>k__BackingField",flags)!.SetValue(c,(byte)94);
                    bool passageFirst=(bool)t.GetProperty("PassageFirst",flags)!.GetValue(c)!;
                    Check(passageFirst==expected,"reuse combat control while leaving existing exploration/chest policy intact"); } }),
        ];
        foreach(var(name,test)in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
}
