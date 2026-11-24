using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace NewFolder3;

internal static class HohNavigationTests
{
    private static readonly DateTime Now = new(2026, 11, 19, 9, 20, 0, DateTimeKind.Utc);
    private static readonly Vector3 Start = new(273.212f, 0, 354.174f);
    private static readonly Vector3 WrongIsland = new(-299.999f, .018f, 356.002f);
    private static readonly HohPassageCandidate A = new(1, new(197.2214f, 0, 251.4295f), false);
    private static readonly HohPassageCandidate B = new(2, new(402.2955f, 0, 251.4143f), false);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    private static Vector3?[] Centers(Vector3 p) { var a = new Vector3?[25]; a[16] = p; return a; }
    internal static int Run()
    {
        (string, Action)[] tests =
        [
            ("hoh-map-floor5-does-not-accept-normal-negative-X-cache", () => {
                var s = new HohMapSession(); s.Enter(5, 0);
                Check(!s.Set(Centers(WrongIsland), Start) && !s.Available(Start), "recorded >500m cache mismatch"); }),
            ("hoh-map-runtime-flat-zero-Y-centers-are-valid", () => {
                var s = new HohMapSession(); s.Enter(5, 0);
                Check(s.Set(Centers(Start), Start) && s.TryGet(16, Start, out var p) && p == Start, "flat floor must not be rejected as legacy missing Y"); }),
            ("hoh-map-same-layout-new-floor-invalidates-cache", () => {
                var s = new HohMapSession(); s.Enter(4, 0); s.Set(Centers(WrongIsland), WrongIsland);
                s.Enter(5, 0); Check(!s.Available(WrongIsland), "layout0 alone is insufficient"); }),
            ("hoh-map-mixed-islands-cannot-pass-with-one-nearby-center", () => {
                var s = new HohMapSession(); var a = Centers(Start); a[17] = WrongIsland;
                Check(!s.Set(a, Start), "local validation covers every generated point"); }),
            ("hoh-map-layout-change-invalidates-cache", () => {
                var s = new HohMapSession(); s.Enter(5, 0); s.Set(Centers(Start), Start); s.Enter(5, 1);
                Check(!s.Available(Start), "mirrored layout isolated"); }),
            ("hoh-map-unchanged-scope-retains-centers", () => {
                var s = new HohMapSession(); s.Enter(5, 0); s.Set(Centers(Start), Start); s.Enter(5, 0);
                Check(s.Available(Start), "no per-frame regeneration"); }),
            ("hoh-map-invalid-vectors-fail-closed", () => {
                var s = new HohMapSession(); var a = Centers(Start); a[17] = new(float.NaN, 0, 0);
                Check(!s.Set(a, Start) && !s.Set(Centers(Start), new(float.NaN, 0, 0)), "invalid coordinates"); }),
            ("hoh-map-stores-a-copy-and-bounds-room-indices", () => {
                var s = new HohMapSession(); var a = Centers(Start); s.Set(a, Start); a[16] = WrongIsland;
                Check(s.TryGet(16, Start, out var p) && p == Start && !s.TryGet(25, Start, out _), "no external mutation or overflow"); }),
            ("hoh-map-detects-only-complete-native-large-room-footprint", () => {
                int[] rooms = [5, 6, 7, 8, 10, 11, 12, 13, 15, 16, 17, 18];
                Check(HohMapSession.IsLargeRoomMap(rooms.Reverse()) && !HohMapSession.IsLargeRoomMap(rooms.Skip(1)) &&
                    !HohMapSession.IsLargeRoomMap(Enumerable.Range(0, 25)), "not inferred from floor number or every12 rooms"); }),
            ("hoh-map-reenter-same-floor-can-clear-session", () => {
                var s = new HohMapSession(); s.Enter(5, 0); s.Set(Centers(Start), Start); s.Clear(); s.Enter(5, 0);
                Check(!s.Available(Start), "separate run must not reuse old floor5"); }),
            ("hoh-passage-native-room-ranks-before-nearest-model", () => {
                var s = new HohPassageCandidates();
                Check(s.TrySelect([A, B with { InPassageRoom = true }], A.Position, Now, out var c, out _, out _) && c.Id == B.Id,
                    "native map is stronger than proximity"); }),
            ("hoh-passage-candidate-is-stable-while-walking", () => {
                var s = new HohPassageCandidates(); s.TrySelect([A, B], A.Position, Now, out _, out _, out _);
                s.TrySelect([A, B], B.Position, Now.AddSeconds(1), out var c, out bool changed, out _);
                Check(c.Id == A.Id && !changed, "do not oscillate with distance"); }),
            ("hoh-passage-floor5-unresponsive-left-beacon-switches-right", () => {
                var s = new HohPassageCandidates(); HohPassageCandidate c = default; bool expired = false;
                for (int i = 0; i <= 9; i++) s.TrySelect([A, B], A.Position, Now.AddSeconds(i), out c, out _, out expired);
                Check(expired && c.Id == B.Id && s.RejectedCount == 1, "recorded repeated arrival must not loop forever"); }),
            ("hoh-passage-recentering-does-not-refill-arrival-budget", () => {
                var s = new HohPassageCandidates(); HohPassageCandidate c = default;
                for (int i = 0; i <= 30; i++) s.TrySelect([A, B], A.Position + Vector3.UnitX * (i % 2 == 0 ? .8f : 1.4f),
                    Now.AddSeconds(i), out c, out _, out _);
                Check(c.Id == B.Id && s.RejectedCount == 1, "small movements cannot repeat8sec hold indefinitely"); }),
            ("hoh-passage-combat-suspends-activation-wait", () => {
                var s = new HohPassageCandidates();
                for (int i = 0; i <= 2; i++) s.TrySelect([A, B], A.Position, Now.AddSeconds(i), out _, out _, out _);
                s.Suspend(); s.TrySelect([A, B], A.Position, Now.AddMinutes(10), out var c, out _, out bool expired);
                Check(c.Id == A.Id && !expired, "combat duration is not an activation failure"); }),
            ("hoh-passage-reloaded-fake-object-remains-excluded-by-position", () => {
                var s = new HohPassageCandidates(); s.TrySelect([A], A.Position, Now, out _, out _, out _); s.RejectCurrent();
                Check(!s.TrySelect([A with { Id = 999 }], A.Position, Now.AddSeconds(1), out _, out _, out _), "new object ID cannot revive failed beacon"); }),
            ("hoh-passage-invalid-model-never-selected", () => {
                var s = new HohPassageCandidates();
                Check(!s.TrySelect([A with { Position = new(float.NaN, 0, 0) }], Start, Now, out _, out _, out _), "finite coordinates required"); }),
            ("hoh-passage-manual-reset-is-explicit", () => {
                var s = new HohPassageCandidates(); s.TrySelect([A], A.Position, Now, out _, out _, out _); s.RejectCurrent(); s.Clear();
                Check(s.TrySelect([A], A.Position, Now, out _, out _, out _) && s.RejectedCount == 0, "manual continue opens one new reviewable attempt"); }),
            ("hoh-passage-candidate-attempts-are-bounded-per-floor", () => {
                var s = new HohPassageCandidates();
                var candidates = Enumerable.Range(0, 20).Select(i => new HohPassageCandidate((ulong)i, new(i * 10, 0, 0), false)).ToArray();
                for (int i = 0; i < 16; i++) { Check(s.TrySelect(candidates, Vector3.Zero, Now, out _, out _, out _), "candidate"); s.RejectCurrent(); }
                Check(!s.TrySelect(candidates, Vector3.Zero, Now, out _, out _, out _) && s.RejectedCount == 16, "no unbounded fake-model cycling"); }),
            ("hoh-navigation-recorded-partial-endpoint-never-moves-to-edge", () => {
                using var h = new Harness(); h.Query = (f, t, _) => Task.FromResult(new List<Vector3> { f, new(193.5f, 0, 355.5f) });
                for (int i = 0; i < 100; i++)
                { h.Tick(i * 2, target: WrongIsland); h.Tick(i * 2 + .1, target: WrongIsland); h.Navigation.Cancel(); }
                Check(h.Moves == 0 && h.Queries == 3 && h.Tick(201, target: WrongIsland) == NavDriveResult.Failed, "consume and reject partial results; outer cancellation cannot refill budget"); }),
            ("hoh-navigation-verified-complete-path-can-move", () => {
                using var h = new Harness(); h.Tick(); h.Tick(.1);
                Check(h.Moves == 1 && h.Queries == 1, "full route accepted"); }),
            ("hoh-navigation-offset-retry-can-escape-straight-path-failure", () => {
                using var h = new Harness(); var goals = new List<Vector3>();
                h.Query = (f, t, _) => { goals.Add(t); return goals.Count == 1 ? Task.FromException<List<Vector3>>(new Exception("FindStraightPath")) : Task.FromResult(new List<Vector3> { f, t }); };
                h.Tick(); h.Tick(.1); h.Tick(.2); h.Tick(.3);
                Check(h.Moves == 1 && goals.Count == 2 && goals[0] != goals[1] && Vector3.Distance(goals[1], h.Goal) <= .5f, "small grounded retry, not blind movement"); }),
            ("hoh-navigation-verified-room-retreat-restores-original-route", () => {
                using var h = new Harness(); var retreat = Start + new Vector3(5, 0, 0);
                h.Query = (f, t, _) => f == Start && Vector3.Distance(t, h.Goal) < 1
                    ? Task.FromException<List<Vector3>>(new Exception("FindStraightPath"))
                    : Task.FromResult(new List<Vector3> { f, t });
                for (int i = 0; i < 7; i++) h.Tick(i);
                h.Tick(7, target: retreat); h.Tick(7.1, target: retreat);
                h.Tick(8, player: retreat, target: retreat);
                h.Tick(9, player: retreat); h.Tick(9.1, player: retreat);
                Check(h.Moves == 2 && h.Queries == 5, "retreat and resumed approach both need complete validated paths"); }),
            ("hoh-navigation-timeout-cancels-query-and-ignores-late-result", () => {
                using var h = new Harness(); var late = new TaskCompletionSource<List<Vector3>>(); CancellationToken token = default;
                h.Query = (_, _, c) => { token = c; return late.Task; }; h.Tick(); h.Tick(8);
                late.SetResult([Start, h.Goal]); Check(token.IsCancellationRequested && h.Moves == 0, "timed out path cannot execute"); }),
            ("hoh-navigation-objective-cancel-ignores-late-result", () => {
                using var h = new Harness(); var late = new TaskCompletionSource<List<Vector3>>(); CancellationToken token = default;
                h.Query = (_, _, c) => { token = c; return late.Task; }; h.Tick(); h.Navigation.Cancel(); late.SetResult([Start, h.Goal]);
                Check(token.IsCancellationRequested && h.Moves == 0, "cancel owns only its query"); }),
            ("hoh-navigation-old-simplemove-query-cannot-overwrite-path", () => {
                using var h = new Harness { SimplePending = true }; h.Tick();
                Check(h.Queries == 0 && h.Tick(10) == NavDriveResult.Failed, "wait bounded for previous owner"); }),
            ("hoh-navigation-stale-player-result-is-rejected", () => {
                using var h = new Harness(); h.Tick(); h.Tick(.1, player: Start + new Vector3(3, 0, 0));
                Check(h.Moves == 0, "manual movement invalidates old route"); }),
            ("hoh-navigation-ground-projection-cannot-jump-islands", () => {
                using var h = new Harness(); h.Ground = _ => WrongIsland;
                for (int i = 0; i < 10; i++) h.Tick(i);
                Check(h.Queries == 0 && h.Moves == 0 && h.Tick(11) == NavDriveResult.Failed, "local ground required"); }),
            ("hoh-navigation-no-progress-requeries-with-a-fixed-budget", () => {
                using var h = new Harness(); for (int i = 0; i < 50; i++) h.Tick(i);
                Check(h.Queries == 3 && h.Moves == 3 && h.Tick(51) == NavDriveResult.Failed, "even valid geometry cannot freeze indefinitely"); }),
            ("hoh-navigation-real-retreat-allows-retry-but-total-is-bounded", () => {
                using var h = new Harness(); h.Query = (_, _, _) => Task.FromException<List<Vector3>>(new Exception("no route"));
                for (int leg = 0; leg < 6; leg++) for (int i = 0; i < 8; i++) h.Tick(leg * 10 + i, player: Start + new Vector3(leg * 3, 0, 0));
                Check(h.Queries == 9 && h.Moves == 0, "movement changes approach but cannot create endless attempts"); }),
            ("hoh-navigation-cancel-and-target-switch-do-not-refill-failed-route", () => {
                using var h = new Harness(); h.Query = (_, _, _) => Task.FromException<List<Vector3>>(new Exception("no route"));
                for (int i = 0; i < 8; i++) h.Tick(i);
                h.Navigation.Cancel(); h.Tick(9, target: h.Goal + new Vector3(8, 0, 0));
                Check(h.Tick(10) == NavDriveResult.Failed && h.Queries == 4, "per-destination evidence survives cancel"); }),
            ("hoh-navigation-manual-continue-explicitly-resets-budget", () => {
                using var h = new Harness(); h.Query = (_, _, _) => Task.FromException<List<Vector3>>(new Exception("no route"));
                for (int i = 0; i < 8; i++) h.Tick(i); h.Navigation.ResetBudget(); h.Tick(9);
                Check(h.Queries == 4, "only explicit reset refills"); }),
            ("hoh-navigation-arrival-stops-owned-movement", () => {
                using var h = new Harness(); h.Tick(); h.Tick(.1);
                Check(h.Tick(.2, player: h.Goal) == NavDriveResult.Arrived && h.Stops == 1, "release owned path"); }),
            ("hoh-navigation-missing-mesh-fails-bounded", () => {
                using var h = new Harness { Ready = false }; h.Tick();
                Check(h.Tick(10) == NavDriveResult.Failed && h.Queries == 0, "no infinite readiness wait"); }),
            ("hoh-navigation-height-and-nan-endpoints-are-rejected", () => {
                using var h = new Harness(); h.Query = (f, t, _) => Task.FromResult(new List<Vector3> { f, t + new Vector3(0, 5, 0) });
                h.Tick(); h.Tick(.1); Check(h.Moves == 0, "wrong height");
                h.Query = (f, t, _) => Task.FromResult(new List<Vector3> { f, new(float.NaN, 0, 0) });
                h.Tick(.2); h.Tick(.3); Check(h.Moves == 0, "nonfinite endpoint"); }),
            ("hoh59-floor36-successful-path-cancellation-does-not-exhaust-query-budget", () => {
                using var h = new Harness();
                for(int i=0;i<40;i++) {
                    var goal=h.Goal+new Vector3(i%2*8,0,0);h.Tick(i*.3,target:goal);h.Tick(i*.3+.1,target:goal);h.Navigation.Cancel();
                }
                Check(h.Queries==40 && h.Moves==40 && h.Tick(13)!=NavDriveResult.Failed,"accepted complete paths are not evidence of unreachable destinations"); }),
            ("hoh59-cancelled-pending-query-is-neutral-not-a-failure", () => {
                using var h=new Harness(); h.Query=(_,_,_)=>new TaskCompletionSource<List<Vector3>>().Task;
                for(int i=0;i<30;i++){h.Tick(i*.1);h.Navigation.Cancel();}
                Check(h.Queries==30 && h.Moves==0 && h.Tick(4)!=NavDriveResult.Failed,"ownership cancellation never refills or consumes real failure evidence"); }),
            ("hoh59-map-rebuild-grants-one-real-bounded-recheck", () => {
                using var h=new Harness();h.Query=(_,_,_)=>Task.FromException<List<Vector3>>(new Exception("bad route"));
                for(int i=0;i<8;i++)h.Tick(i*.1);
                Check(h.Tick(1)==NavDriveResult.Failed && h.Queries==3,"initial approach truly failed");
                h.Navigation.RecheckAfterMapRefresh();for(int i=0;i<8;i++)h.Tick(2+i*.1);
                Check(h.Tick(3)==NavDriveResult.Failed && h.Queries==6,"new geometry gets actual queries, not an immediate cached failure");
                h.Navigation.RecheckAfterMapRefresh();Check(h.Tick(4)==NavDriveResult.Failed && h.Queries==6,"repeating regeneration cannot create an infinite retry"); }),
            ("hoh59-stale-query-from-real-player-movement-does-not-poison-destination", () => {
                using var h=new Harness();
                for(int i=0;i<20;i++){h.Tick(i*.3);h.Tick(i*.3+.1,player:Start+new Vector3(3,0,0));h.Navigation.Cancel();}
                Check(h.Queries==20 && h.Moves==0 && h.Tick(7)!=NavDriveResult.Failed,"stale routes are discarded safely without classifying the destination as unreachable"); }),
            ("hoh-navigation-other-dungeons-keep-default-driver", LegacyDriver),
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }

    private static unsafe void LegacyDriver()
    {
        foreach (byte dungeon in new byte[] { 1, 3, 4 })
        {
            var nav = new LegacyNavigator(); var helper = new NavigationHelper(nav, () => false, () => Now, _ => { }, _ => { }, () => false);
            var driver = new NavigationDriver(helper);
            FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon dd = default;
            dd.DeepDungeonId = dungeon; dd.Floor = 12;
            Check(driver.Drive(Start + new Vector3(10, 0, 0), Start, .5f, &dd, 0, null) == NavDriveResult.Moving && nav.Starts == 1,
                "PT and other dungeons must dispatch original INavigator; no HoH geometry dependency");
        }
    }
    private sealed class LegacyNavigator : INavigator
    {
        public int Starts;
        public bool PathfindAndMoveTo(Vector3 dest, bool alignCamera) { Starts++; return true; }
        public bool RepathTo(Vector3 dest, bool alignCamera) => PathfindAndMoveTo(dest, alignCamera);
        public void CancelAll() { }
    }
    private sealed class Harness : IDisposable
    {
        public readonly Vector3 Goal = Start + new Vector3(20, 0, 0);
        public DateTime Time = Now;
        public bool Ready = true, SimplePending;
        public int Moves, Stops, Queries;
        public Func<Vector3, Vector3?> Ground = p => p;
        public Func<Vector3, Vector3, CancellationToken, Task<List<Vector3>>> Query = (f, t, _) => Task.FromResult(new List<Vector3> { f, t });
        public readonly HohValidatedNavigation Navigation;
        public Harness() => Navigation = new(() => Ready, p => Ground(p), (f, t, c) => { Queries++; return Query(f, t, c); },
            _ => Moves++, () => Stops++, _ => { }, () => Time, () => SimplePending);
        public NavDriveResult Tick(double seconds = 0, Vector3? player = null, Vector3? target = null)
        { Time = Now.AddSeconds(seconds); return Navigation.Tick(target ?? Goal, player ?? Start, .5f); }
        public void Dispose() => Navigation.Dispose();
    }
}
