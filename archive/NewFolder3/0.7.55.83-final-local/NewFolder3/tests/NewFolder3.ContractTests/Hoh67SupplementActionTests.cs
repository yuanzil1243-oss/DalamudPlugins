using DeepDungeon.Fsd.Core;

namespace NewFolder3;

// The .67 job-action adapter was retired in .81: HoH gathering belongs to rotation targeting.
internal static class Hoh67SupplementActionTests
{
    private static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);}
    internal static int Run()
    {
        (string,Action)[] tests=[
            ("hoh81-gathering-all-supported-ordinary-groups",()=>{
                foreach(int floor in Enumerable.Range(1,100))
                    Check(HohHighFloorPolicy.ControlsCombat(FarmingMode.FreeExploration,2,floor)==(floor is >=81 and <=89 or >=91 and <=99),"exact rotation-gather scope");}),
            ("hoh81-other-dungeons-retain-legacy-attract-executor",()=>{
                foreach(bool enabled in new[]{false,true})foreach(int floor in new[]{81,89,91,99})
                    Check(HohHighFloorPolicy.AllowsSupplementExecutor(FarmingMode.FreeExploration,4,floor,enabled)==enabled,"PT setting unchanged");}),
            ("hoh81-gathering-has-no-job-action-selector",()=>{
                Check(typeof(HohHighFloorPolicy).GetMethod("SupplementBaseAction")==null,"do not map SMN/MCH or any job to forced gathering actions");}),
            ("hoh81-gathering-independent-of-legacy-attract",()=>{
                foreach(int floor in new[]{81,89,91,99})
                    Check(HohHighFloorPolicy.AllowsSupplementExecutor(FarmingMode.FreeExploration,2,floor,false),"zero/disabled optional action does not disable rotation gathering");}),
            ("hoh81-non-free-mode-gathering-isolation",()=>{
                foreach(var mode in new FarmingMode?[]{null,FarmingMode.DeepProgression,FarmingMode.HoardDiscovery})
                    Check(!HohHighFloorPolicy.ControlsCombat(mode,2,91),"other modes unchanged");}),
        ];
        foreach(var(name,test)in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
}
