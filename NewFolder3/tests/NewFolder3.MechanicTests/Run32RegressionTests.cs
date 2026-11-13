using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

internal static class Run32RegressionTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string why) { if (!value) throw new Exception(why); }
    public static IEnumerable<(string, Action)> Cases()
    {
        yield return ("pt99-light-prepares-dark-at-18-without-stopping-before-24", () => {
            var d = Pt99TargetPolicy.Decide(true,false,800,1000,981,1000,false,true);
            Check(d.Target == Pt99TargetKind.Grief && d.RequestedColor == Pt99Color.Dark && !d.SuppressOutput,"18.1 prepares without stopping");
            Check(Pt99TargetPolicy.Decide(true,false,800,1000,980,1000,false,true).RequestedColor == Pt99Color.Dark,"exact 18 prepares");
        });
        yield return ("pt99-dark-requests-light-only-when-eater-is-lower", () => {
            var d = Pt99TargetPolicy.Decide(false,true,981,1000,800,1000,false,true);
            Check(d.Target == Pt99TargetKind.Eater && d.RequestedColor == Pt99Color.Light && !d.SuppressOutput,"reverse preparation direction");
            Check(Pt99TargetPolicy.Decide(false,true,980,1000,800,1000,false,true).RequestedColor == Pt99Color.Light,"exact 18 prepares");
        });
        yield return ("pt99-correct-color-does-not-oscillate-at-large-absolute-gap", () => {
            Check(Pt99TargetPolicy.Decide(false,true,500,1000,990,1000,false,true).Target == Pt99TargetKind.Eater,"already attacking higher eater");
            Check(Pt99TargetPolicy.Decide(true,false,990,1000,500,1000,false,true).Target == Pt99TargetKind.Grief,"already attacking higher grief");
        });
        yield return ("pt99-hp-percentages-account-for-different-max-hp", () => {
            var d = Pt99TargetPolicy.Decide(true,false,1000,2000,700,1000,false,true);
            Check(Math.Abs(d.HpDifference-20)<0.001 && d.RequestedColor == Pt99Color.Dark,"percentage points, not raw HP");
        });
        yield return ("pt99-color-missing-in-combat-stops-and-requests-higher-side", () => {
            var d = Pt99TargetPolicy.Decide(false,false,500,1000,700,1000,false,true);
            Check(d.Target == Pt99TargetKind.WaitForColor && d.RequestedColor == Pt99Color.Dark,"must obtain a buff");
            Check(Pt99TargetPolicy.Decide(false,false,700,1000,500,1000,false,true).RequestedColor == Pt99Color.Light,"light");
            Check(Pt99TargetPolicy.Decide(false,false,700,1000,500,1000,false).SuppressOutput,"pre-pull also requires actual color");
        });
        yield return ("pt99-buff-confirmation-not-arrival-controls-resuming-target", () => {
            var before = Pt99TargetPolicy.Decide(true,false,400,1000,900,1000,false,true);
            var after = Pt99TargetPolicy.Decide(false,true,400,1000,900,1000,false,true);
            Check(before.SuppressOutput && !after.SuppressOutput && after.Target == Pt99TargetKind.Eater,"only actual dark changes eligibility");
        });
        yield return ("pt99-single-finished-boss-requests-other-color-and-both-finished-stop", () => {
            Check(Pt99TargetPolicy.Decide(true,false,1,1000,100,1000,false,true).RequestedColor == Pt99Color.Dark,"cannot hit finished grief");
            Check(Pt99TargetPolicy.Decide(false,true,100,1000,1,1000,false,true).RequestedColor == Pt99Color.Light,"cannot hit finished eater");
            Check(Pt99TargetPolicy.Decide(false,true,1,1000,1,1000,false,true).Target == Pt99TargetKind.Finished,"finish");
        });
        yield return ("pt99-invalid-and-double-color-states-remain-suppressed", () => {
            Check(Pt99TargetPolicy.Decide(true,true,100,1000,200,1000,false,true).SuppressOutput,"double color");
            Check(Pt99TargetPolicy.Decide(true,false,100,0,200,1000,false,true).Target == Pt99TargetKind.WaitForBossState,"unavailable boss");
        });
        yield return ("pt99-minion-priority-is-preserved", () => {
            var d = Pt99TargetPolicy.Decide(true,false,300,1000,900,1000,true,true);
            Check(d.Target == Pt99TargetKind.Minion && !d.SuppressOutput && d.RequestedColor == Pt99Color.None,"adds still have priority");
        });
        yield return ("pt99-drain-guard-keeps-longer-helper-and-cast-end-gap", () => {
            var guard = new Pt99MechanicGuard();
            guard.ObserveCast(44088,7,Now); guard.ObserveCast(44093,12,Now);
            Check(guard.IsBlocked(Now.AddSeconds(7.3)) && guard.IsBlocked(Now.AddSeconds(12.9)),"do not seize between pair");
            Check(!guard.IsBlocked(Now.AddSeconds(13.6)),"full pair and settlement completed");
        });
        yield return ("pt99-late-and-consecutive-drain-extends-mechanic-ownership", () => {
            var guard = new Pt99MechanicGuard(); guard.ObserveCast(44090,6,Now);
            guard.ObserveCast(44091,7,Now.AddSeconds(5.8));
            Check(guard.IsBlocked(Now.AddSeconds(13.3)),"late helper extends window");
            guard.ObserveCast(44092,11,Now.AddSeconds(13));
            Check(guard.IsBlocked(Now.AddSeconds(24.5)),"consecutive sequence");
            guard.Reset(); Check(!guard.IsBlocked(Now),"floor exit reset");
        });
        yield return ("pt99-spatial-mechanics-use-live-safety-instead-of-a-blanket-hold", () => {
            var guard = new Pt99MechanicGuard(); guard.ObserveCast(44813,0.8,Now);
            Check(!guard.IsBlocked(Now),"auto attack is not a movement mechanic");
            guard.ObserveCast(44079,7,Now); Check(!guard.IsBlocked(Now.AddSeconds(20)) && guard.SpatialUntil > Now.AddSeconds(20),"spatial advisory cannot block all safe color routes");
            guard.ObserveCast(44088,double.NaN,Now); Check(guard.BlockedUntil < Now.AddSeconds(30),"invalid duration cannot corrupt deadline");
        });
        yield return ("pt99-pool-samples-are-bounded-and-color-specific", () => {
            Check(Pt99Arena.Pools(Pt99Color.Light).Count == 5 && Pt99Arena.Pools(Pt99Color.Dark).Count == 5,"five pools each");
            foreach (var p in Pt99Arena.Pools(Pt99Color.Light).Concat(Pt99Arena.Pools(Pt99Color.Dark))) Check(Pt99Arena.Contains(p),"inside arena");
            Check(!Pt99Arena.Contains(new(float.NaN,0)) && !Pt99Arena.Contains(Vector2.Zero),"unknown arenas rejected");
        });
        yield return ("pt99-safe-landing-cannot-hide-an-unsafe-walking-segment", () => {
            var start=new Vector3(-610,0,-300); var end=new Vector3(-600,0,-300);
            bool Safe(Vector3 _,Vector3 p)=>Math.Abs(p.X+605)>0.6f;
            Check(Safe(start,end) && !Pt99WalkSafety.IsSafe(start,end,Safe),"must detect obstacle between safe endpoints");
        });
        yield return ("pt99-walk-safety-defers-to-bmr-if-current-position-is-unsafe", () => {
            var start=new Vector3(-610,0,-300); var end=new Vector3(-600,0,-300);
            Check(!Pt99WalkSafety.IsSafe(start,end,(_,p)=>p.X > -601),"BMR color/escape guidance must retain movement");
            int calls=0;
            Check(Pt99WalkSafety.IsSafe(start,end,(_,_)=>{calls++;return true;}) && calls>=15,"probe full segment");
            Check(!Pt99WalkSafety.IsSafe(start,new(0,0,0),(_,_)=>true),"never leave known arena");
        });
        yield return ("ae-provider-persistence-does-not-renumber-existing-options", () => {
            Check((int)FsdRotationProvider.Custom == 4 && (int)FsdRotationProvider.InsertNameHere3 == 5 && (int)FsdRotationProvider.AeAssist == 6,"persistent values");
            var settings = new FsdRotationSettings { Provider = FsdRotationProvider.AeAssist };
            Check(settings.TryValidate(out _),"no custom commands required");
            Check(System.Text.Json.JsonSerializer.Deserialize<FsdRotationSettings>(System.Text.Json.JsonSerializer.Serialize(settings))?.Provider == FsdRotationProvider.AeAssist,"persist AE");
        });
        yield return ("ae-enable-holds-output-until-main-and-pull-are-on", () => {
            bool running=false,pull=false,stopped=false; var calls=new List<string>();
            var ae = new AeRotationControl(()=>running,()=>pull,b=>{running=b;calls.Add("run:"+b);},b=>{pull=b;calls.Add("pull:"+b);},b=>{stopped=b;calls.Add("stop:"+b);return true;});
            Check(ae.SetEnabled(true) && running && pull && !stopped && ae.ReadState()==FsdRotationState.On,"all switches");
            Check(calls.SequenceEqual(new[]{"stop:True","run:True","pull:True","stop:False"}),"safe ordering");
            Check(ae.SetEnabled(true) && running && pull && !stopped,"repeat must not toggle");
        });
        yield return ("ae-disable-pauses-before-closing-pull-and-main", () => {
            bool running=true,pull=true,stopped=false; var calls=new List<string>();
            var ae = new AeRotationControl(()=>running,()=>pull,b=>{running=b;calls.Add("run:"+b);},b=>{pull=b;calls.Add("pull:"+b);},b=>{stopped=b;calls.Add("stop:"+b);return true;});
            Check(ae.SetEnabled(false) && !running && !pull && stopped,"all disabled");
            Check(calls.SequenceEqual(new[]{"stop:True","pull:False","run:False"}),"pause first");
        });
        yield return ("ae-partial-enable-failure-closes-all-possible-controls", () => {
            bool running=false,pull=false,stopped=false;
            var ae = new AeRotationControl(()=>running,()=>pull,b=>running=b,b=>{if(b)throw new Exception("missing pull");pull=b;},b=>{stopped=b;return true;});
            Check(!ae.SetEnabled(true) && !running && !pull && stopped,"fail closed");
        });
        yield return ("ae-unaccepted-stop-command-cannot-enable-output", () => {
            bool running=false,pull=false;
            var ae = new AeRotationControl(()=>running,()=>pull,b=>running=b,b=>pull=b,b=>false);
            Check(!ae.SetEnabled(true) && !running && !pull && ae.LastStopRequest==null,"no success from missing pause command");
        });
        yield return ("ae-running-alone-is-not-proof-of-output", () => {
            var ae = new AeRotationControl(()=>true,()=>true,_=>{},_=>{},_=>true);
            Check(ae.ReadState()==FsdRotationState.Unknown,"stop flag not read back");
        });
        yield return ("ae-task-stop-and-duty-exit-close-owned-output", () => {
            var calls=new List<bool>(); var settings=new FsdRotationSettings{Provider=FsdRotationProvider.AeAssist};
            var state=new DeepDungeonStateSnapshot(true,true,true,4,99,DeepDungeonFloorKind.Boss,false,1);
            var controller=new FsdRotationController(_=>FsdRotationState.Unknown,(_,enable,_)=>{calls.Add(enable);return true;});
            controller.Update(true,FarmingMode.FinalBoss,state,false,settings); controller.Reset();
            Check(calls.SequenceEqual(new[]{true,false}),"stop");
            controller.Update(true,FarmingMode.FinalBoss,state,false,settings);
            controller.Update(true,FarmingMode.FinalBoss,state with{IsInDuty=false},false,settings);
            Check(calls.SequenceEqual(new[]{true,false,true,false}),"exit");
        });
        yield return ("ae-boss-suppression-restores-only-at-policy-boundary", () => {
            var calls=new List<bool>(); var settings=new FsdRotationSettings{Provider=FsdRotationProvider.AeAssist};
            var state=new DeepDungeonStateSnapshot(true,true,true,4,99,DeepDungeonFloorKind.Boss,false,1);
            var controller=new FsdRotationController(_=>FsdRotationState.Unknown,(_,enable,_)=>{calls.Add(enable);return true;});
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings);
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings,true);
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings,true);
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings,false);
            Check(calls.SequenceEqual(new[]{true,false,true}),"no per-frame toggling");
        });
        yield return ("ae-provider-switch-cannot-enable-a-second-provider-when-shutdown-fails", () => {
            bool allowShutdown=false; var calls=new List<(FsdRotationProvider,bool)>();
            var state=new DeepDungeonStateSnapshot(true,true,true,4,99,DeepDungeonFloorKind.Boss,false,1);
            var settings=new FsdRotationSettings{Provider=FsdRotationProvider.AeAssist};
            var controller=new FsdRotationController(_=>FsdRotationState.Unknown,(provider,enabled,_)=> {
                calls.Add((provider,enabled)); return provider!=FsdRotationProvider.AeAssist || enabled || allowShutdown;
            });
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings);
            settings.Provider=FsdRotationProvider.WrathCombo;
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings);
            Check(controller.FailedProvider==FsdRotationProvider.AeAssist && !controller.DesiredEnabled,"retain shutdown failure");
            Check(calls.All(c=>c.Item1==FsdRotationProvider.AeAssist),"never start second output");
            allowShutdown=true;
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings);
            Check(calls[^2]==(FsdRotationProvider.AeAssist,false) && calls[^1]==(FsdRotationProvider.WrathCombo,true),"verified release before next provider");
        });
        yield return ("ae-unresolved-shutdown-survives-reset-and-blocks-a-new-provider", () => {
            var state=new DeepDungeonStateSnapshot(true,true,true,4,99,DeepDungeonFloorKind.Boss,false,1);
            var settings=new FsdRotationSettings{Provider=FsdRotationProvider.AeAssist}; int otherStarts=0;
            var controller=new FsdRotationController(_=>FsdRotationState.Unknown,(provider,enabled,_)=> {
                if(provider!=FsdRotationProvider.AeAssist && enabled) otherStarts++;
                return provider!=FsdRotationProvider.AeAssist || enabled;
            });
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings); controller.Reset();
            Check(controller.FailedProvider==FsdRotationProvider.AeAssist,"do not erase failed stop");
            settings.Provider=FsdRotationProvider.WrathCombo;
            controller.Update(true,FarmingMode.FinalBoss,state,true,settings);
            Check(otherStarts==0 && controller.FailedProvider==FsdRotationProvider.AeAssist,"task restart does not forget owned AE");
        });
        yield return ("final-boss-mode-is-standalone-and-cannot-acquire-a-save", () => {
            Check((int)FarmingMode.FreeExploration==4 && (int)FarmingMode.FinalBoss==5,"mode persistence");
            Check(FarmingPlan.TryCreate(FarmingMode.FinalBoss,SaveUse.None,99,1,false,false,false,false,false,out var p,out _),"valid practice");
            Check(p is {IsProgression:false,ReusesSave:false,StartFloor:99,SaveUse:SaveUse.None},"standalone");
            foreach(var save in new[]{SaveUse.Create,SaveUse.Prepared}) Check(!FarmingPlan.TryCreate(FarmingMode.FinalBoss,save,99,1,false,false,false,false,false,out _,out _),"no save entry");
            Check(!FarmingPlan.TryCreate(FarmingMode.DeepProgression,SaveUse.None,1,1,false,false,true,true,false,out _,out _),"no-save cannot leak into progression");
        });
        yield return ("final-boss-targets-are-all-disabled-and-preferences-independent", () => {
            var settings=new FarmingSettings{Mode=FarmingMode.FinalBoss};
            var p=settings.GetPreferences(new FsdSettings{NecromancerFsdLoopCount=3,NecromancerFsdLoopInfinite=true});
            Check(p.Cycles==1 && !p.Infinite && p.BandedEnabled==false && !p.OpenGold && !p.OpenSilver && !p.OpenBronze,"safe test defaults");
            Check(FarmingTargetPolicy.LocksHoard(FarmingMode.FinalBoss) && FarmingTargetPolicy.LocksGold(FarmingMode.FinalBoss) && FarmingTargetPolicy.LocksSilverBronze(FarmingMode.FinalBoss),"no chests");
        });
        yield return ("final-boss-cycle-needs-completion-and-never-deletes-saves", () => {
            FarmingPlan.TryCreate(FarmingMode.FinalBoss,SaveUse.None,99,1,false,false,false,false,false,out var p,out _);
            foreach (var result in new[]{new FarmingAttemptResult(99,true,false,false,false),new(99,false,true,false,false),new(99,false,false,false,true),new(91,true,false,false,false)}) {
                var d=FarmingSessionPolicy.Decide(p!,result);
                Check(!d.DeleteOwnedSave && !d.RecoverFailure,"no cleanup or retry after failure");
                Check(d.CountCycle==(result.EntryFloor==99 && result.DutyCompleted && !result.DutyFailed && !result.Aborted),"success only");
            }
        });
        yield return ("final-boss-entry-selects-fixed-party-only-and-rejects-ambiguous-options", () => {
            Check(FinalBossPracticePolicy.TrySelectFixedParty(["返回","“随机组队”进入","“固定队伍”进入"],out int i)&&i==2,"fixed party regardless of order");
            Check(!FinalBossPracticePolicy.TrySelectFixedParty(["随机组队进入","返回"],out _),"no random fallback");
            Check(!FinalBossPracticePolicy.TrySelectFixedParty(["固定队伍进入","固定队伍进入"],out _),"ambiguous");
            Check(FinalBossPracticePolicy.MatchesChallenge("挑战“卓异的悲寂歼灭战”","卓异的悲寂歼灭战"),"localized title");
            Check(!FinalBossPracticePolicy.MatchesChallenge("挑战“卓异的悲寂深想战”","卓异的悲寂歼灭战"),"never extreme");
            Check(FinalBossPracticePolicy.IsPractice(1333)&&!FinalBossPracticePolicy.IsPractice(1311)&&!FinalBossPracticePolicy.IsPractice(1290),"exact practice territory");
        });
        yield return ("final-boss-fixed-party-confirmation-matches-reported-game-dialog", () => {
            Check(FinalBossPracticePolicy.CanConfirmFixedPartyEntry(true,true,"确定以\"固定队伍\"进入吗？"),"reported straight-quote dialog");
            Check(FinalBossPracticePolicy.CanConfirmFixedPartyEntry(true,true,"确定以“固定队伍”进入吗？"),"smart quote equivalent");
            Check(FinalBossPracticePolicy.CanConfirmFixedPartyEntry(true,true," 确定以 \"固定队伍\" 进入吗?\n"),"whitespace and fullwidth punctuation");
        });
        yield return ("final-boss-fixed-party-confirmation-requires-both-owned-selection-stages", () => {
            foreach(var stages in new[]{(false,false),(true,false),(false,true)})
                Check(!FinalBossPracticePolicy.CanConfirmFixedPartyEntry(stages.Item1,stages.Item2,FinalBossPracticePolicy.FixedPartyEntryPrompt),"stale dialog cannot authorize a fresh attempt");
        });
        yield return ("final-boss-fixed-party-confirmation-rejects-random-delete-and-other-prompts", () => {
            foreach(var prompt in new[]{"确定以\"随机组队\"进入吗？","确定要删除存档吗？","确定要离开副本吗？","固定队伍","","其他内容确定以\"固定队伍\"进入吗？","确定以\"固定队伍\"进入吗？并删除存档"})
                Check(!FinalBossPracticePolicy.CanConfirmFixedPartyEntry(true,true,prompt),"must exactly match the known entry prompt");
        });
    }
}
