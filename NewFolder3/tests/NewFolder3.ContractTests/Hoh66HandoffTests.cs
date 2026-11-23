using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class Hoh66HandoffTests
{
    private static void Check(bool v,string why){if(!v)throw new Exception(why);}
    internal static int Run()
    {
        (string,Action)[] tests = [
            ("hoh66-supplement-scope-excludes-all-bosses-and-other-modes",()=>{
                foreach(int floor in Enumerable.Range(1,100))
                    Check(HohHighFloorPolicy.ControlsCombat(FarmingMode.FreeExploration,2,floor)==(floor is >=81 and <=89 or >=91 and <=99),"exact scope");
                foreach(var mode in new FarmingMode?[]{null,FarmingMode.DeepProgression,FarmingMode.HoardDiscovery})
                    Check(!HohHighFloorPolicy.ControlsCombat(mode,2,86),"mode isolation");}),
            ("hoh75-hoh-active-pulls-ignore-obsolete-configured-ceiling",()=>{
                foreach(int configured in Enumerable.Range(1,6)) {
                    Check(HohHighFloorPolicy.SupplementLimit(FarmingMode.FreeExploration,2,86,configured)==6,"earlier fixed rolling ceiling");
                    Check(HohHighFloorPolicy.SupplementLimit(FarmingMode.FreeExploration,2,94,configured)==6,"94-97 ordinary mobs use active grouping too");
                    Check(HohHighFloorPolicy.SupplementLimit(FarmingMode.FreeExploration,4,94,configured)==configured,"PT configuration remains intact");}}),
            ("hoh66-hoh-supplements-no-longer-depend-on-legacy-pull-switch",()=>{
                foreach(int f in new[]{81,89,91,99})Check(HohHighFloorPolicy.AllowsSupplementExecutor(FarmingMode.FreeExploration,2,f,false),"new executor enabled independently");
                Check(!HohHighFloorPolicy.AllowsSupplementExecutor(FarmingMode.FreeExploration,2,90,false),"boss excluded");}),
            ("hoh66-pt-executor-still-obeys-original-switch",()=>{
                foreach(bool old in new[]{false,true})foreach(int f in Enumerable.Range(1,100))
                    Check(HohHighFloorPolicy.AllowsSupplementExecutor(FarmingMode.FreeExploration,4,f,old)==old,"PT switch unchanged");}),
            ("hoh66-earlier-combat-does-not-adopt91-door-preparation",()=>{
                foreach(int f in Enumerable.Range(81,9)) {
                    Check(MobCombatPolicy.UsesControlledHighFloor(FarmingMode.FreeExploration,2,f),"hold current room");
                    Check(!HohHighFloorPolicy.WaitsForPassage(FarmingMode.FreeExploration,2,f,false),"original exploration order");}}),
            ("hoh66-door-open-blocks-new-81plus-pulls-but-keeps-existing-hate",()=>{
                foreach(int f in new[]{81,89,91,99}) {
                    bool controlled=MobCombatPolicy.UsesControlledHighFloor(FarmingMode.FreeExploration,2,f);
                    Check(!PassageCombatPolicy.AllowsNewTarget(false,controlled,true,1,false,true),"no new pulls after open");
                    Check(PassageCombatPolicy.AllowsNewTarget(false,controlled,true,1,true),"finish existing enemy");}}),
            ("hoh66-rolling-six-is-ceiling-not-quota-and-does-not-allow-other-room",()=>{
                Check(PassageCombatPolicy.CanSupplement(5,6,true,8,25,false,false,false),"same room spare slot");
                Check(!PassageCombatPolicy.CanSupplement(6,6,true,8,25,false,false,false),"ceiling");
                Check(!PassageCombatPolicy.CanSupplement(1,6,false,8,25,false,false,false),"no cross-room");
                Check(!PassageCombatPolicy.CanSupplement(0,6,true,8,25,false,false,false),"never wait for quota");}),
            ("hoh69-retired-meteor-takeover-is-absent-from-production",()=>{
                var host=typeof(DeepDungeon.Fsd.Dalamud.FsdApplication).Assembly.GetType("DeepDungeon.Fsd.Dalamud.FsdEngine")!;
                var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
                Check(host.GetMethod("UpdateHoh90Meteor",flags)==null,"old takeover adapter removed");
                Check(host.GetField("_hoh90Hook",flags)==null,"old receive hook removed");
                Check(typeof(DeepDungeon.Fsd.Runtime.FsdRotationController).Assembly.GetType("DeepDungeon.Fsd.Runtime.Hoh90MeteorController")==null,"old strategy exists only in replay tests");}),
        ];
        foreach(var(name,test)in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
    private static string FindRoot(){for(var p=new DirectoryInfo(AppContext.BaseDirectory);p!=null;p=p.Parent)if(File.Exists(Path.Combine(p.FullName,"NewFolder3.csproj")))return p.FullName;throw new Exception("source root not found");}
}
