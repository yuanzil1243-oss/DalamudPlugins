using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;

namespace NewFolder3;

internal static class Hoh76DoorwayApproachTests
{
    private static void Check(bool v, string why) { if (!v) throw new Exception(why); }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh76-recorded-doorway-detour-is-pursuit-not-local-repair", () => {
                var start = new Vector3(-298.05588f, .01832962f, 231.15236f);
                var goal = new Vector3(-295.1858f, 0, 205.61743f);
                Vector3[] path = [new(-278, 0, 231.15f), new(-278, 0, 205.62f), goal];
                Check(!CombatPositionPolicy.ValidatePathEndpoint(start, goal, path, out var length, out var reason) &&
                    reason == "path-too-long" && length > 35, "reproduce old rejection around wall");
                Check(CombatPositionPolicy.ValidatePathEndpoint(start, goal, path, out _, out _, HohTargetApproachPolicy.MaximumPathLength),
                    "same complete detour is legal only for target approach"); }),
            ("hoh76-both-endpoint-and-enemy-safety-use-pursuit-bound", () => {
                Vector3[] path = [new(0,0,20), new(25,0,20), new(25,0,0)];
                Check(PassageCombatPolicy.SafeHohRecoveryPath(path, Vector3.Zero, [], HohTargetApproachPolicy.MaximumPathLength), "safe wall detour");
                Check(!PassageCombatPolicy.SafeHohRecoveryPath(path, Vector3.Zero, [new(0,0,12)], HohTargetApproachPolicy.MaximumPathLength),
                    "longer bound cannot bypass incidental enemy protection"); }),
            ("hoh76-directed-path-remains-bounded", () => {
                Check(!CombatPositionPolicy.ValidatePathEndpoint(Vector3.Zero, new(161,0,0), [new(161,0,0)], out _, out _,
                    HohTargetApproachPolicy.MaximumPathLength), "not an unlimited route"); }),
            ("hoh76-detour-cannot-accept-wrong-floor-or-partial-endpoint", () => {
                Check(!CombatPositionPolicy.ValidatePathEndpoint(Vector3.Zero,new(40,0,0),[new(40,2,0)],out _,out _,HohTargetApproachPolicy.MaximumPathLength),"height still checked");
                Check(!CombatPositionPolicy.ValidatePathEndpoint(Vector3.Zero,new(40,0,0),[new(20,0,0)],out _,out _,HohTargetApproachPolicy.MaximumPathLength),"partial path still rejected"); }),
            ("hoh76-close-target-through-wall-is-not-an-output-position", () => {
                Check(!HohTargetApproachPolicy.CanHoldOutput(true,false,true) && !HohTargetApproachPolicy.CanHoldOutput(true,true,false),
                    "both actual and packet-height sight must be clear"); }),
            ("hoh76-out-of-range-target-never-justifies-holding", () => {
                Check(!HohTargetApproachPolicy.CanHoldOutput(false,true,true) && HohTargetApproachPolicy.NeedsApproach(false,false,false,true),
                    "do not wait at far side of corridor with no attack opportunity"); }),
            ("hoh76-clear-ranged-position-ends-approach-before-feet", () => {
                Check(HohTargetApproachPolicy.CanHoldOutput(true,true,true) && !HohTargetApproachPolicy.NeedsApproach(true,false,false,true),
                    "usable range is enough, no mandatory melee arrival"); }),
            ("hoh76-target-across-corridor-may-be-more-than-one-room-hop", () => {
                Check(HohTargetApproachPolicy.AllowsRoom(2,false,12,-1) && HohTargetApproachPolicy.AllowsRoom(3,false,12,-1),
                    "reachable topology, not adjacent-room-only local repair"); }),
            ("hoh76-unreachable-room-remains-forbidden", () => {
                Check(!HohTargetApproachPolicy.AllowsRoom(-1,false,12,-1) && !HohTargetApproachPolicy.AllowsRoom(0,false,-1,-1),"no invented connectivity"); }),
            ("hoh76-deep-engaged-group-must-stay-in-its-combat-room", () => {
                Check(!HohTargetApproachPolicy.AllowsRoom(1,true,12,7) && HohTargetApproachPolicy.AllowsRoom(0,true,7,7),
                    "91through99 combat room restriction retained"); }),
            ("hoh76-open-passage-cannot-create-new-target-approach", () => {
                Check(!HohTargetApproachPolicy.NeedsApproach(false,false,true,true) && !HohTargetApproachPolicy.NeedsApproach(false,false,false,false),
                    "door gate and target eligibility retained"); }),
            ("hoh76-existing-hate-can-still-be-resolved-after-door-opens", () => {
                Check(HohTargetApproachPolicy.NeedsApproach(false,true,true,false),"existing obligation is not discarded"); }),
            ("hoh76-unengaged-failure-does-not-expand-away-from-target", () => {
                Check(!HohTargetApproachPolicy.AllowsUndirectedFallback(false,false),"no safe-exit spiral for unpulled mob");
                Check(HohTargetApproachPolicy.AllowsUndirectedFallback(true,false) && HohTargetApproachPolicy.AllowsUndirectedFallback(false,true),
                    "existing fight and stone curse recovery remain"); }),
            ("hoh76-production-controller-applies-directed-bound-only-while-directed", () => {
                using var f=new Fixture();f.Approach.Begin(Vector3.Zero,new(28,0,0));Check(f.PathLimit==160,"directed bound used by endpoint and safety validation");
                Check(f.Advance() && f.PathLimit==160,"opposite side is also pursuit");
                Check(!f.Advance() && f.PathLimit==35,"finished stage cannot reenter local search as directed"); }),
            ("hoh76-production-controller-keeps-pt-boss-and-mode-isolation", () => {
                foreach(var(dungeon,floor,mode)in new(uint,byte,FarmingMode)[]{(4,94,FarmingMode.FreeExploration),(2,90,FarmingMode.FreeExploration),(2,10,FarmingMode.FreeExploration),(2,7,FarmingMode.Hoard)}) {
                    using var f=new Fixture(dungeon,floor,mode);f.Approach.Begin(Vector3.Zero,new(28,0,0));Check(f.PathLimit==35&&!f.Advance(),"new bounds are HoH ordinary free-exploration only");} }),
            ("hoh76-verified-output-keeps-real-gcd-window", () => {
                using var f=new Fixture();var now=DateTime.UtcNow;f.VerifyAt(now);
                Check(f.Recent(Vector3.Zero,new(20,0,0),now.AddSeconds(2.5)),"real successful hit outranks uncertain geometry between casts");
                Check(!f.Recent(Vector3.Zero,new(20,0,0),now.AddSeconds(4)),"no infinite hold after output stops"); }),
            ("hoh76-old-output-cannot-justify-remaining-out-of-range", () => {
                using var f=new Fixture();var now=DateTime.UtcNow;f.VerifyAt(now);
                Check(!f.Recent(Vector3.Zero,new(40,0,0),now.AddSeconds(1)) && !f.Recent(Vector3.Zero,new(20,0,0),now.AddSeconds(-1)),
                    "range change and clock reversal cannot validate current output"); }),
        ];
        foreach(var(name,test)in tests){test();Console.WriteLine("  ok  "+name);}return tests.Length;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly RunContext _ctx=new(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
        private readonly CombatPositionRecoveryController _controller;
        internal Fixture(uint dungeon=2,byte floor=7,FarmingMode mode=FarmingMode.FreeExploration)
        {
            _ctx.FarmingPlan=new(mode,SaveUse.Create,1,1,false,false,true,true,false);
            typeof(DutyState).GetProperty(nameof(DutyState.DungeonId))!.SetValue(_ctx.Duty,dungeon);
            typeof(DutyState).GetProperty(nameof(DutyState.Floor))!.SetValue(_ctx.Duty,floor);
            _controller=new(_ctx,null!,null!,null,_ctx.CombatRecoveryBudget,null,_=>{},(_,_)=>{});
        }
        internal HohTargetApproachState Approach=>(HohTargetApproachState)typeof(CombatPositionRecoveryController).GetField("_targetApproach",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_controller)!;
        internal float PathLimit=>(float)typeof(CombatPositionRecoveryController).GetProperty("MaximumRecoveryPathLength",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(_controller)!;
        internal bool Advance()=>(bool)typeof(CombatPositionRecoveryController).GetMethod("TryAdvanceTargetApproach",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(_controller,["test"])!;
        internal void VerifyAt(DateTime now)
        {
            typeof(CombatPositionRecoveryController).GetField("_lastDamageAt",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(_controller,now);
            typeof(CombatPositionRecoveryController).GetField("_actionRange",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(_controller,25f);
        }
        internal bool Recent(Vector3 player,Vector3 target,DateTime now)=>(bool)typeof(CombatPositionRecoveryController)
            .GetMethod("HasRecentVerifiedOutput",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(_controller,[player,target,now])!;
        public void Dispose(){_controller.Dispose();_ctx.Dispose();}
    }
}
