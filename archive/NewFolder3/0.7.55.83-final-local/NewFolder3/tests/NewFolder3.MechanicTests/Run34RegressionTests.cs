using System.Numerics;
using DeepDungeon.Fsd.Core;

internal static class Run34RegressionTests
{
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    public static IEnumerable<(string, Action)> Cases()
    {
        yield return ("pt99-18-and-24-exact-directional-boundaries", () => {
            foreach (bool light in new[] { true, false })
                foreach (int gap in new[] { 179, 180, 181, 239, 240, 241 })
                {
                    var d = Pt99TargetPolicy.Decide(light, !light, light ? 500u : (uint)(500+gap), 1000,
                        light ? (uint)(500+gap) : 500u, 1000, false, true);
                    Check(d.SuppressOutput == (gap >= 240), "24 is the stop boundary in either direction");
                    Check(d.RequestedColor == (gap >= 180 ? light ? Pt99Color.Dark : Pt99Color.Light : Pt99Color.None),
                        "18 prepares while 17.9 does not");
                }
        });
        yield return ("pt99-large-gap-in-correct-direction-neither-stops-nor-reverses", () => {
            var light = Pt99TargetPolicy.Decide(true,false,999,1000,100,1000,false,true);
            var dark = Pt99TargetPolicy.Decide(false,true,100,1000,999,1000,false,true);
            Check(!light.SuppressOutput && !dark.SuppressOutput && light.RequestedColor == Pt99Color.None && dark.RequestedColor == Pt99Color.None,
                "keep reducing an existing gap");
        });
        yield return ("pt99-prepull-no-color-cannot-release-boss-output", () => {
            var d = Pt99TargetPolicy.Decide(false,false,1000,1000,1000,1000,false);
            Check(d.SuppressOutput && d.Target == Pt99TargetKind.WaitForColor && d.RequestedColor == Pt99Color.Light,
                "request initial color without attacking");
            Check(!Pt99TargetPolicy.IsOutputTargetCompatible(false,false,Pt99TargetPolicy.GriefBaseId), "engine pre-tick gate");
            Check(!Pt99TargetPolicy.IsOutputTargetCompatible(true,true,Pt99TargetPolicy.GriefBaseId), "ambiguous color cannot enable");
        });
        yield return ("pt99-color-target-handoff-has-no-timed-output-delay", () => {
            Check(!Pt99TargetPolicy.IsOutputTargetCompatible(false,true,Pt99TargetPolicy.GriefBaseId), "old target is not eligible");
            Check(Pt99TargetPolicy.IsOutputTargetCompatible(false,true,Pt99TargetPolicy.EaterBaseId), "new actual color and target release immediately");
            Check(Pt99TargetPolicy.IsOutputTargetCompatible(true,false,Pt99TargetPolicy.GriefBaseId), "reverse mapping");
            Check(!Pt99TargetPolicy.IsOutputTargetCompatible(true,false,0), "cleared target cannot enable rotation");
        });
        yield return ("pt99-minion-exception-requires-current-minion-decision", () => {
            Check(Pt99TargetPolicy.IsOutputTargetCompatible(false,false,Pt99TargetPolicy.MinionBaseId,true), "minion does not need boss color");
            Check(!Pt99TargetPolicy.IsOutputTargetCompatible(false,false,Pt99TargetPolicy.MinionBaseId), "stale target alone cannot authorize");
            Check(!Pt99TargetPolicy.IsOutputTargetCompatible(false,false,Pt99TargetPolicy.GriefBaseId,true), "exception never authorizes a boss");
        });
        yield return ("pt99-prepull-static-route-requires-untouched-idle-boss-pair", () => {
            Check(Pt99TargetPolicy.CanUsePrepullFloorRoute(false,1000,1000,2000,2000,false), "known pre-pull pair");
            Check(!Pt99TargetPolicy.CanUsePrepullFloorRoute(true,1000,1000,2000,2000,false), "combat requires live safety");
            Check(!Pt99TargetPolicy.CanUsePrepullFloorRoute(false,1000,1000,2000,2000,true), "cast cannot use static route");
            Check(!Pt99TargetPolicy.CanUsePrepullFloorRoute(false,999,1000,2000,2000,false), "damaged boss cannot use static route");
            Check(!Pt99TargetPolicy.CanUsePrepullFloorRoute(false,0,0,2000,2000,false), "missing boss cannot use static route");
        });
        yield return ("pt99-color-candidates-stay-in-pinned-color-floor-interiors", () => {
            foreach (var color in new[] { Pt99Color.Light, Pt99Color.Dark })
            {
                var candidates = Pt99ColorFloor.Candidates(color);
                Check(candidates.Count > 5, "more than fixed centers");
                foreach (var point in candidates)
                    Check(Pt99ColorFloor.Contains(color,point,.4f) && Pt99Arena.Contains(point) &&
                        !Pt99ColorFloor.Contains(color == Pt99Color.Light ? Pt99Color.Dark : Pt99Color.Light,point,0), "correct floor with edge clearance");
                foreach (var point in Pt99Arena.Pools(color)) Check(Pt99ColorFloor.Contains(color,point), "old verified centers preserved");
            }
            Check(!Pt99ColorFloor.Contains(Pt99Color.None,new(-600,-300)) &&
                !Pt99ColorFloor.Contains(Pt99Color.Light,new(float.NaN,-300)), "invalid geometry rejected");
        });
        yield return ("pt99-route-search-can-use-safe-pool-interior-when-all-centers-are-unsafe", () => {
            var start = new Vector3(-600,0,-300);
            bool Safe(Vector3 _, Vector3 p) => Pt99Arena.Pools(Pt99Color.Light).All(c =>
                Vector2.DistanceSquared(c,new(p.X,p.Z)) > .45f*.45f);
            var search = new Pt99ColorRouteSearch(); bool found = false; Vector3 destination = default;
            for (int i = 0; i < 100 && !found; i++)
                found = search.TryFind(start,Pt99Color.Light,Safe,(_,_)=>true,out destination);
            Check(Pt99Arena.Pools(Pt99Color.Light).All(c=>!Safe(start,new(c.X,0,c.Y))), "old centers all fail");
            Check(found && Pt99ColorFloor.Contains(Pt99Color.Light,new(destination.X,destination.Z)), "find another actual floor position");
            Check(Pt99WalkSafety.IsSafe(start,destination,Safe), "whole route remains safe");
        });
        yield return ("pt99-route-search-yields-before-exceeding-ipc-probe-budget", () => {
            int calls = 0; var search = new Pt99ColorRouteSearch();
            Check(!search.TryFind(new(-600,0,-300),Pt99Color.Dark,(_,_)=>{calls++;return true;},(_,_)=>true,out _,6,2), "budget too small for route");
            Check(calls <= 2 && search.LastProbeCount == calls, "bounded work even if no route completes");
        });
        yield return ("pt99-route-search-never-takes-over-an-unsafe-start-or-blocked-sight", () => {
            var search = new Pt99ColorRouteSearch(); var start = new Vector3(-600,0,-300);
            Check(!search.TryFind(start,Pt99Color.Light,(_,_)=>false,(_,_)=>true,out _) && search.LastProbeCount == 1,
                "unsafe current position stays with BMR");
            Check(!search.TryFind(start,Pt99Color.Light,(_,_)=>true,(_,_)=>false,out _), "blocked LOS rejects destinations");
        });
        yield return ("pt99-route-search-cannot-cross-unsafe-band-to-safe-color", () => {
            var search = new Pt99ColorRouteSearch(); var start = new Vector3(-600,0,-300);
            bool Safe(Vector3 _,Vector3 p) { float d=Vector2.Distance(new(p.X,p.Z),new(-600,-300)); return d < .5f || d > 2; }
            bool found = false;
            for (int i = 0; i < 100; i++) found |= search.TryFind(start,Pt99Color.Dark,Safe,(_,_)=>true,out _);
            Check(!found, "safe endpoint cannot hide an unsafe segment");
        });
        yield return ("pt99-mandatory-color-recovery-starts-with-fresh-budget", () => {
            var budget = new Pt99ColorRecoveryBudget();
            for (int i=0;i<35;i++) budget.Tick(1,false,true);
            budget.RecordFailure(); budget.RecordFailure();
            Check(budget.Exhausted, "optional preparation has spent its active budget");
            budget.SetRequired(true);
            Check(!budget.Exhausted && budget.Required && budget.ActiveSeconds == 0 && budget.EligibleSeconds == 0 && budget.Failures == 0,
                "24 or no-color gets a real recovery attempt");
        });
        yield return ("pt99-mechanic-holds-never-spend-recovery-budget", () => {
            var budget = new Pt99ColorRecoveryBudget(); budget.Reset(true);
            for(int i=0;i<300;i++) budget.Tick(1,true,true);
            Check(!budget.Exhausted && budget.ActiveSeconds == 0 && budget.EligibleSeconds == 0,"hard holds excluded");
            budget.Tick(1,false,false);
            Check(budget.ActiveSeconds == 0 && budget.EligibleSeconds == 1,"BMR waiting is not active movement");
        });
        yield return ("pt99-required-wait-has-a-separate-bounded-diagnostic-budget", () => {
            var budget = new Pt99ColorRecoveryBudget(); budget.Reset(true);
            for(int i=0;i<120;i++) budget.Tick(1,false,false);
            Check(budget.Exhausted && budget.ActiveSeconds == 0,"unavailable safety cannot wait silently forever");
            budget.Reset(true); for(int i=0;i<3;i++) budget.RecordFailure();
            Check(budget.Exhausted,"three failed active attempts are bounded");
        });
        yield return ("pt99-spatial-advisories-cannot-extend-drain-aether-hold", () => {
            var now=new DateTime(2026,11,13,0,0,0,DateTimeKind.Utc); var guard=new Pt99MechanicGuard();
            guard.ObserveCast(44088,7,now); guard.ObserveCast(44079,7,now.AddSeconds(7));
            Check(!guard.IsBlocked(now.AddSeconds(9)) && guard.SpatialUntil > now.AddSeconds(20),
                "spatial zones require live checks after drain resolves, not another fixed hold");
        });
    }
}
