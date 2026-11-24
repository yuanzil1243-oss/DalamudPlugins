using System.Numerics;
using System.Reflection;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime;

namespace NewFolder3;

internal static class Hoh62ContinuityTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static readonly DateTime Now = new(2026, 11, 22, 4, 0, 0, DateTimeKind.Utc);
    private static HohContinuityFrame Frame(byte floor = 89) => new(true, true, true, true, 2, floor, 819, false, false);
    private static HohRunContinuity Started(byte floor = 89)
    { var s = new HohRunContinuity(); s.Observe(Frame(floor), Now); return s; }
    private static void Disconnect(HohRunContinuity s) => s.Observe(Frame() with { LoggedIn = false, PlayerAvailable = false, Stable = false, DungeonId = 0, Floor = 0, InDuty = false }, Now.AddSeconds(1));
    private static void Reconnect(HohRunContinuity s, HohContinuityFrame f)
    { s.Observe(f, Now.AddSeconds(4)); s.Observe(f, Now.AddSeconds(5)); }
    internal static int Run()
    {
        (string, Action)[] tests = [
            ("hoh62-logout-with-zeroed-duty-freezes-original-attempt", () => {
                var s = Started(); Disconnect(s);
                Check(s.HasIdentity && s.HoldsHost && s.HoldsControls && s.Phase == HohContinuityPhase.Disconnected, "do not run leave/delete/entry on missing-duty frame"); }),
            ("hoh62-same-duty-reconnect-resynchronizes-once", () => {
                var s = Started(); Disconnect(s); s.Observe(Frame(), Now.AddSeconds(4));
                Check(s.HoldsHost, "stable grace"); s.Observe(Frame(), Now.AddSeconds(5));
                Check(!s.HoldsControls && s.ResumeSequence == 1, "resume boundary");
                s.Observe(Frame(), Now.AddSeconds(6)); Check(s.ResumeSequence == 1, "not every frame"); }),
            ("hoh62-floor-transition-does-not-resynchronize", () => {
                var s = Started(88); s.Observe(Frame(89) with { Stable = false }, Now.AddSeconds(1)); s.Observe(Frame(89), Now.AddSeconds(2));
                Check(s.ResumeSequence == 0 && !s.HoldsControls, "normal zoning unchanged"); }),
            ("hoh62-reconnect-different-territory-rejected", () => {
                var s = Started(); Disconnect(s); Reconnect(s, Frame() with { Territory = 999 });
                Check(s.Phase == HohContinuityPhase.Mismatch && s.HoldsControls, "do not attack another duty"); }),
            ("hoh62-reconnect-different-dungeon-rejected", () => {
                var s = Started(); Disconnect(s); Reconnect(s, Frame() with { DungeonId = 4 }); Check(s.Phase == HohContinuityPhase.Mismatch, "PT untouched"); }),
            ("hoh62-reconnect-different-floorset-rejected", () => {
                var s = Started(); Disconnect(s); Reconnect(s, Frame(91)); Check(s.Phase == HohContinuityPhase.Mismatch, "original attempt only"); }),
            ("hoh62-reconnect-outside-requires-failure-cleanup", () => {
                var s = Started(); Disconnect(s); Reconnect(s, Frame() with { InDuty = false, DungeonId = 0, Floor = 0 });
                Check(s.Phase == HohContinuityPhase.Exited && !s.HoldsHost && s.HoldsControls, "host can clean up, no combat"); }),
            ("hoh62-unstable-reconnect-does-not-consume-stable-grace", () => {
                var s = Started(); Disconnect(s); s.Observe(Frame() with { Stable = false }, Now.AddSeconds(20));
                s.Observe(Frame(), Now.AddSeconds(21)); Check(s.HoldsHost, "must settle online"); }),
            ("hoh62-death-awaits-revival-before-failing", () => {
                var s = Started(90); s.Observe(Frame(90) with { Dead = true }, Now.AddSeconds(1));
                s.Observe(Frame(90) with { Dead = true }, Now.AddSeconds(12));
                Check(s.Phase == HohContinuityPhase.WaitingRevival && s.HoldsHost, "12 second grace"); }),
            ("hoh62-unrecovered-death-releases-host-for-exit-not-combat", () => {
                var s = Started(90); s.ObserveDeath(Now); s.ObserveDeath(Now.AddSeconds(12));
                Check(s.Phase == HohContinuityPhase.ConfirmedDeath && !s.HoldsHost && s.HoldsControls, "normal failure path can run"); }),
            ("hoh62-boss-revival-blocks-controls-until-rebuff", () => {
                var s = Started(90); s.ObserveDeath(Now); s.Observe(Frame(90), Now.AddSeconds(3));
                Check(!s.HoldsHost && s.BossRebuffPending && s.HoldsControls && s.ResumeSequence == 1, "item controller can run before AI");
                s.CompleteBossRebuff(); Check(!s.HoldsControls && s.ResumeSequence == 2, "enable boundary after actual buff check");
                s.CompleteBossRebuff(); Check(s.ResumeSequence == 2, "idempotent"); }),
            ("hoh62-mob-floor-revival-resumes-without-boss-gate", () => {
                var s = Started(); s.ObserveDeath(Now); s.Observe(Frame(), Now.AddSeconds(3)); Check(!s.HoldsControls && !s.BossRebuffPending, "ordinary floor"); }),
            ("hoh62-boss-reconnect-also-checks-lost-buffs", () => {
                var s = Started(90); Disconnect(s); Reconnect(s, Frame(90)); Check(s.BossRebuffPending, "death during disconnected interval may be unobserved"); }),
            ("hoh62-offline-time-does-not-confirm-death", () => {
                var s = Started(90); s.ObserveDeath(Now); Disconnect(s);
                s.Observe(Frame(90) with { Dead = true }, Now.AddMinutes(1)); s.Observe(Frame(90) with { Dead = true }, Now.AddMinutes(1).AddSeconds(1));
                Check(s.Phase == HohContinuityPhase.WaitingRevival, "observe online death grace"); }),
            ("hoh62-existing-terminal-result-never-cleared", () => {
                var s = Started(); Disconnect(s); s.Observe(Frame() with { Terminal = true }, Now.AddSeconds(4));
                Check(s.Phase == HohContinuityPhase.Exited && !s.HoldsHost && s.ResumeSequence == 0, "terminal evidence releases cleanup, never resumes combat"); }),
            ("hoh62-policy-scope-only-hoh-free-exploration", () => {
                foreach (var mode in Enum.GetValues<FarmingMode>()) foreach (uint id in new uint[] {1,2,4})
                    Check(HohRunContinuity.Applies(mode,id) == (mode == FarmingMode.FreeExploration && id == 2), "PT and other modes excluded"); }),
            ("hoh62-rebuff-steel-before-strength", () => Check(HohRevivalBuffPolicy.Next(HohRebuffItemState.Ready, HohRebuffItemState.Ready) == HohRebuffStep.Steel, "defence first")),
            ("hoh62-rebuff-pending-does-not-enable-output", () => {
                Check(HohRevivalBuffPolicy.Next(HohRebuffItemState.Pending, HohRebuffItemState.Ready) == HohRebuffStep.Wait, "no duplicate use");
                Check(HohRevivalBuffPolicy.Next(HohRebuffItemState.Present, HohRebuffItemState.Pending) == HohRebuffStep.Wait, "strength must resolve"); }),
            ("hoh62-rebuff-unavailable-item-does-not-deadlock", () => {
                foreach (var state in new[] {HohRebuffItemState.NoStock,HohRebuffItemState.Forbidden,HohRebuffItemState.Present})
                    Check(HohRevivalBuffPolicy.Next(state,HohRebuffItemState.Ready) == HohRebuffStep.Strength &&
                        HohRevivalBuffPolicy.Next(state,HohRebuffItemState.Present) == HohRebuffStep.Complete, "explicit unavailable disposition"); }),
            ("hoh62-actual-world-arrival-allows-virtual-room-mismatch", () => Check(HohRoomArrivalPolicy.CanBeginSearch(2,true,5,5,new(1,0,0),Vector3.Zero), "start original planned search")),
            ("hoh62-staging-height-nan-and-changed-plan-not-arrival", () => {
                Check(!HohRoomArrivalPolicy.CanBeginSearch(2,false,5,5,Vector3.Zero,Vector3.Zero), "staging");
                Check(!HohRoomArrivalPolicy.CanBeginSearch(2,true,5,6,Vector3.Zero,Vector3.Zero), "plan changed");
                Check(!HohRoomArrivalPolicy.CanBeginSearch(2,true,5,5,new(0,2,0),Vector3.Zero), "other height");
                Check(!HohRoomArrivalPolicy.CanBeginSearch(2,true,5,5,new(float.NaN,0,0),Vector3.Zero), "nonfinite"); }),
            ("hoh62-pt-arrival-policy-unchanged", () => Check(!HohRoomArrivalPolicy.CanBeginSearch(4,true,5,5,Vector3.Zero,Vector3.Zero), "PT retains room index rule")),
            ("hoh62-rebuff-temporary-action-lock-and-retry-block-are-not-unavailable", () => {
                Check(HohRevivalBuffPolicy.ObserveItem(false,false,1,true,true,true,true) == HohRebuffItemState.Pending,"retry block");
                Check(HohRevivalBuffPolicy.ObserveItem(false,false,1,true,false,false,true) == HohRebuffItemState.Pending,"action lock");
                Check(HohRevivalBuffPolicy.ObserveItem(false,true,0,true,false,true,true) == HohRebuffItemState.Pending,"await native buff after consumption"); }),
            ("hoh62-rebuff-uses-boss-reserves-and-verifies-native-buff", () => {
                Check(HohRevivalBuffPolicy.ObserveItem(false,false,1,true,false,true,true) == HohRebuffItemState.Ready,"one remaining is usable on boss");
                Check(HohRevivalBuffPolicy.ObserveItem(true,true,0,true,false,false,false) == HohRebuffItemState.Present,"actual status sufficient"); }),
            ("hoh62-room-search-remains-active-within-validated-world-area", () => {
                Check(HohRoomArrivalPolicy.CanContinueSearch(2,5,5,true,2),"waypoint can leave arrival circle without returning to center");
                Check(!HohRoomArrivalPolicy.CanContinueSearch(2,5,5,false,0),"leaving search area requires reentry");
                Check(!HohRoomArrivalPolicy.CanContinueSearch(2,5,5,true,5),"height");
                Check(!HohRoomArrivalPolicy.CanContinueSearch(2,null,5,true,0),"arrival evidence required");
                Check(!HohRoomArrivalPolicy.CanContinueSearch(4,5,5,true,0),"PT unchanged"); }),
            ("hoh62-room-watchdog-detects-no-movement-without-client-error", () => {
                var s = new HohRoomProgressWatch();
                for(int i=0;i<20;i++) Check(!s.Stalled(5,Vector3.Zero,Now.AddSeconds(i)),"not premature");
                Check(s.Stalled(5,Vector3.Zero,Now.AddSeconds(20)),"bounded navigation failure"); }),
            ("hoh62-room-watchdog-does-not-count-combat-pause-or-real-progress", () => {
                var s = new HohRoomProgressWatch(); s.Stalled(5,Vector3.Zero,Now);
                Check(!s.Stalled(5,Vector3.Zero,Now.AddMinutes(1)),"pause resets continuous time");
                for(int i=1;i<40;i++) Check(!s.Stalled(5,new(i*.5f,0,0),Now.AddMinutes(1).AddSeconds(i)),"real movement"); }),
            ("hoh62-room-watchdog-resets-for-another-task", () => {
                var s = new HohRoomProgressWatch(); for(int i=0;i<20;i++) s.Stalled(5,Vector3.Zero,Now.AddSeconds(i));
                Check(!s.Stalled(6,Vector3.Zero,Now.AddSeconds(20)),"new room"); s.Reset();
                Check(!s.Stalled(6,Vector3.Zero,Now.AddSeconds(21)),"manual continue reset"); }),
            ("hoh62-ae-gets-real-enable-command-after-revival-hold", () => {
                var commands = new List<bool>(); var ae = new FsdRotationController(_ => FsdRotationState.Unknown,(_,v,_) => { commands.Add(v); return true; });
                var settings = new FsdRotationSettings { Provider = FsdRotationProvider.AeAssist };
                var state = new DeepDungeonStateSnapshot(true,true,true,2,90,DeepDungeonFloorKind.Boss,false,1);
                ae.Update(true,FarmingMode.FreeExploration,state,true,settings);
                ae.Update(false,FarmingMode.FreeExploration,state,false,settings);
                ae.Update(true,FarmingMode.FreeExploration,state,false,settings);
                Check(commands.SequenceEqual(new[] {true,false,true}),"AI cache must not hide disabled AE after reconnect"); }),
            ("hoh62-companion-caches-reapply-after-continuity-hold", () => {
                var commands = new List<string>(); var ai = new FsdBossMechanicsController(c => { commands.Add(c); return true; });
                var settings = new FsdBossMechanicsSettings { Provider = FsdBossMechanicsProvider.Bmr };
                var state = new DeepDungeonStateSnapshot(true,true,true,2,90,DeepDungeonFloorKind.Boss,false,1);
                ai.Update(true,state,settings,FarmingMode.FreeExploration); ai.Update(false,state,settings,FarmingMode.FreeExploration);
                ai.Update(true,state,settings,FarmingMode.FreeExploration); Check(commands.Count == 3 && ai.IsEnabled, "must resend actual command"); }),
            ("hoh62-rebuff-suppresses-native-combat-channel", () => {
                using var c = new RunContext(new FsdSettings(),new DutyState(),new(DetailedMapRuntimePolicy.PalacePalOnly,null,null));
                c.HohContinuity.Observe(Frame(90),Now); c.HohContinuity.ObserveDeath(Now); c.HohContinuity.Observe(Frame(90),Now.AddSeconds(2));
                Check(c.NormalCombatOutputSuppressed,"not just companion UI state"); c.HohContinuity.CompleteBossRebuff(); Check(!c.NormalCombatOutputSuppressed,"release"); })
        ];
        foreach (var (name, test) in tests) { test(); Console.WriteLine("  ok  " + name); }
        return tests.Length;
    }
}
