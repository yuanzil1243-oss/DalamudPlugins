using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;
using System.Text.Json;

namespace NewFolder3;

internal static class AetherpoolExchangeTests
{
    public static int Run()
    {
        int n = 0;
        void Check(string name, bool test)
        { if (!test) throw new InvalidOperationException("grip-" + name); n++; Console.WriteLine("  ok  grip-" + name); }
        var now = new DateTime(2026,11,25,0,0,0,DateTimeKind.Utc);
        foreach(uint id in new uint[]{1,2,4})
        {
            var d = AetherpoolExchangePolicy.GetDungeon(id)!;
            Check("metadata-"+id, d.ItemId>0 && d.NpcId>0 && d.StartFloor==(id==1?51:21) && d.Reserve==(id==4?5:14));
            Check("plan-"+id, RelicFarmingPolicy.TryCreate(id,SaveUse.Create,d.StartFloor,3,false,new(false,true,true,false),out var plan,out _));
            plan = plan! with { GripExchange = new(50,4) };
            var clear = RelicFarmingPolicy.Decide(plan,new(d.StartFloor,true,false,false,false));
            var failure = RelicFarmingPolicy.Decide(plan,new(d.StartFloor,false,true,false,false));
            Check("clear-one-cycle-"+id, clear.CountCycle && clear.DeleteOwnedSave && !clear.RecoverFailure && clear.NextFloor==d.StartFloor);
            Check("failure-not-success-"+id,!failure.CountCycle && failure.DeleteOwnedSave && failure.RecoverFailure);
            Check("abort-keeps-save-"+id,!RelicFarmingPolicy.Decide(plan,new(d.StartFloor,false,false,false,true)).DeleteOwnedSave);
            Check("mismatched-floor-keeps-save-"+id,!RelicFarmingPolicy.Decide(plan,new(d.StartFloor+10,true,false,false,false)).DeleteOwnedSave);
            Check("external-plan-rejected-"+id,!RelicFarmingPolicy.Decide(plan with{ExternalSave=true},new(d.StartFloor,true,false,false,false)).DeleteOwnedSave);
            Check("prepared-rejected-"+id,!RelicFarmingPolicy.TryCreate(id,SaveUse.Prepared,d.StartFloor,1,false,new(false,true,true,false),out _,out _));
            Check("wrong-start-rejected-"+id,!RelicFarmingPolicy.TryCreate(id,SaveUse.Create,1,1,false,new(false,true,true,false),out _,out _));
            Check("pause-supported-"+id,FloorsetPausePolicy.Supports(plan));
            var checkpoint = new FloorsetPauseCheckpoint(123,31,plan,FloorsetResumeKind.RestartCycle,null,1,2);
            Check("pause-roundtrip-"+id, JsonSerializer.Deserialize<FloorsetPauseCheckpoint>(JsonSerializer.Serialize(checkpoint)) is { } loaded && loaded.IsValid() && loaded.Plan.GripExchange==plan.GripExchange);
            Check("safe-51-"+id,AetherpoolExchangePolicy.ComputePlannedExchanges(51,51,new(50,4),d.Reserve)==(id==4?4:3));
            Check("dual-threshold-"+id,AetherpoolExchangePolicy.ComputePlannedExchanges(99,49,new(50,4),d.Reserve)==0);
            Check("unknown-no-exchange-"+id,AetherpoolExchangePolicy.ComputePlannedExchanges(null,99,new(),d.Reserve)==0);
            var session = new FarmingSession(plan);
            Check("session-requires-silver-"+id,!session.TrySetTargets(new(false,true,false,false)) && session.OpenSilver);
            Check("session-allows-other-targets-"+id,session.TrySetTargets(new(true,false,true,true)) && session.BandedEnabled && !session.OpenGold);
            Check("dispatch-uses-local-flag-"+id,FarmingSessionPolicy.Decide(plan,new(d.StartFloor,true,false,false,false))==clear);
            Check("no-silver-plan-rejected-"+id,!RelicFarmingPolicy.IsValid(plan with {OpenSilver=false}));
            Check("initial-check-shared-across-attempts-"+id,!session.InitialGripExchangeChecked);
            session.InitialGripExchangeChecked=true;
            session.ConfirmOwnedSaveDeleted(); session.CompleteAttempt(clear,0);
            Check("initial-check-not-reset-after-cleanup-"+id,session.InitialGripExchangeChecked && session.OwnedSlot==-1);
        }
        Check("eo-not-enabled-without-official-support", AetherpoolExchangePolicy.GetDungeon(3)==null && !RelicFarmingPolicy.TryCreate(3,SaveUse.Create,21,1,false,new(false,true,true,false),out _,out _));
        Check("legacy-mode-values-preserved",Enum.GetValues<FarmingMode>().Length==4 && (int)FarmingMode.Hoard==0 && (int)FarmingMode.Aetherpool==1 && (int)FarmingMode.DeepProgression==3);
        Check("unknown-default-settings-not-serialized",!JsonSerializer.Serialize(new FarmingSettings()).Contains("GripExchange"));
        Check("default-relic-flag-not-serialized",!JsonSerializer.Serialize(new FarmingSettings()).Contains("RelicMaterial"));
        var relicSettings = new FarmingSettings { DungeonId=1, Mode=FarmingMode.Hoard, RelicMaterial=true };
        string config = "{\"Fsd\":{\"Farming\":" + JsonSerializer.Serialize(relicSettings) + "}}";
        var normalized = LegacyConfigurationCompatibility.Normalize(config);
        Check("relic-not-migrated-to-retired-free-mode",!normalized.Changed && normalized.Json==config);
        var loadedSettings = JsonSerializer.Deserialize<FarmingSettings>(JsonSerializer.Serialize(relicSettings))!;
        Check("relic-settings-roundtrip", loadedSettings.RelicMaterial && loadedSettings.Mode==FarmingMode.Hoard && !loadedSettings.FreeExploration);
        var relicPrefs=loadedSettings.GetPreferences(new FsdSettings()); relicPrefs.Cycles=7;
        loadedSettings.RelicMaterial=false;
        Check("relic-preferences-isolated",loadedSettings.GetPreferences(new FsdSettings()).Cycles!=7);
        var plain = new FarmingPlan(FarmingMode.Hoard,SaveUse.Create,21,1,false,false,true,true,false);
        Check("legacy-plan-shape-preserved",!JsonSerializer.Serialize(plain).Contains("GripExchange") && !JsonSerializer.Serialize(plain).Contains("RelicMaterial"));
        Check("invalid-options-no-exchange",AetherpoolExchangePolicy.ComputePlannedExchanges(99,99,new(0,100),5)==0);
        Check("negative-reserve-no-exchange",AetherpoolExchangePolicy.ComputePlannedExchanges(99,99,new(),-1)==0);
        Check("cn-forge-confirm",AetherpoolExchangePolicy.IsCraftConfirmation("消耗强化值，制作光耀聚魔柄？"));
        Check("tc-forge-confirm",AetherpoolExchangePolicy.IsCraftConfirmation("要製作天之聚魔柄嗎？"));
        Check("en-forge-confirm",AetherpoolExchangePolicy.IsCraftConfirmation("Forge an aetherpool grip?"));
        foreach(string text in new[]{"确认付款？","删除存档？","将聚魔装备带出迷宫","制作聚魔柄并删除存档？","聚魔柄"})
            Check("unknown-or-destructive-confirm-rejected-"+n,!AetherpoolExchangePolicy.IsCraftConfirmation(text));
        Check("known-cancel",AetherpoolExchangePolicy.IsCancelOption("取消"));
        Check("no-last-menu-option-fallback",!AetherpoolExchangePolicy.IsCancelOption("将聚魔装备带出迷宫"));
        var store = new AetherpoolStateStore();
        store.Observe(123,4,60,55,AetherpoolSource.InDutyMemory,now);
        Check("memory-state",store.TryGet(123,4,now,out var state) && state.Weapon==60 && state.Armor==55);
        Check("other-character-cannot-read",!store.TryGet(456,4,now,out _));
        Check("stale-state-rejected",!store.TryGet(123,4,now.AddMinutes(11),out _));
        var partialStore = new AetherpoolStateStore();
        partialStore.Observe(123,4,60,60,AetherpoolSource.InDutyMemory,now);
        partialStore.Observe(123,4,70,null,AetherpoolSource.LogMessage,now.AddMinutes(11));
        Check("partial-refresh-does-not-revive-stale-armor",partialStore.TryGet(123,4,now.AddMinutes(11),out var partial) && partial.Weapon==70 && partial.Armor==null);
        store.Observe(123,4,20,null,AetherpoolSource.LogMessage,now.AddSeconds(1));
        Check("old-log-cannot-lower-memory",store.TryGet(123,4,now.AddSeconds(1),out state) && state.Weapon==60);
        Check("confirmed-deduction",store.RecordConfirmedCraft(123,4,now.AddSeconds(2)) && store.TryGet(123,4,now.AddSeconds(2),out state) && state.Weapon==50 && state.Armor==45);
        store.Observe(123,4,99,99,AetherpoolSource.LogMessage,now.AddSeconds(3));
        Check("late-log-cannot-undo-craft",store.TryGet(123,4,now.AddSeconds(3),out state) && state.Weapon==50);
        store.SetCharacter(456);
        Check("identity-switch-clears-cache",!store.TryGet(456,4,now,out _) && !store.TryGet(123,4,now,out _));
        store.Observe(456,4,30,30,AetherpoolSource.InDutyMemory,now);
        store.SetCharacter(0);
        Check("logout-clears-cache",!store.TryGet(456,4,now,out _));
        var receipt = new GripCraftReceipt();
        Check("unrelated-inventory-ignored-without-pending",receipt.Observe(999,now,out bool complete,out _) && !complete);
        receipt.Begin(10,now);
        Check("confirm-once",receipt.ConfirmOnce() && !receipt.ConfirmOnce());
        Check("waits-for-inventory",receipt.Observe(10,now.AddSeconds(1),out complete,out _) && !complete && receipt.Pending);
        Check("inventory-plus-one-confirms",receipt.Observe(11,now.AddSeconds(2),out complete,out _) && complete && !receipt.Pending);
        receipt.Begin(11,now.AddSeconds(3));
        Check("inventory-without-confirm-rejected",!receipt.Observe(12,now.AddSeconds(4),out _,out _));
        receipt = new(); receipt.Begin(10,now); receipt.ConfirmOnce();
        Check("unexpected-inventory-delta-rejected",!receipt.Observe(12,now.AddSeconds(1),out _,out _));
        receipt = new(); receipt.Begin(10,now); receipt.ConfirmOnce();
        Check("inventory-decrease-rejected",!receipt.Observe(9,now.AddSeconds(1),out _,out _));
        receipt = new(); receipt.Begin(10,now); receipt.ConfirmOnce();
        Check("receipt-timeout-does-not-retry",!receipt.Observe(10,now.AddSeconds(13),out _,out _) && receipt.Pending && receipt.ConfirmationSent);
        var completedRun = new FarmingAttemptResult(21, true, false, false, false);
        Check("pending-pause-skips-new-exchange",!AetherpoolExchangePolicy.ShouldRunPostExitExchange(true,false,true,false,completedRun));
        Check("no-pause-allows-new-exchange",AetherpoolExchangePolicy.ShouldRunPostExitExchange(true,false,false,false,completedRun));
        Check("pause-does-not-abandon-active-receipt",AetherpoolExchangePolicy.ShouldRunPostExitExchange(true,false,true,true,completedRun));
        Check("cancel-pause-restores-optional-exchange",AetherpoolExchangePolicy.ShouldRunPostExitExchange(true,false,false,true,completedRun));
        Check("completed-exchange-not-repeated",!AetherpoolExchangePolicy.ShouldRunPostExitExchange(true,true,false,false,completedRun));
        Check("failed-run-cannot-start-exchange",!AetherpoolExchangePolicy.ShouldRunPostExitExchange(true,false,false,false,new(21,false,true,false,false)));
        Check("aborted-run-cannot-start-exchange",!AetherpoolExchangePolicy.ShouldRunPostExitExchange(true,false,false,false,new(21,true,false,false,true)));
        Check("disabled-option-cannot-start-exchange",!AetherpoolExchangePolicy.ShouldRunPostExitExchange(false,false,false,false,completedRun));
        foreach (var c in new (string Name, ulong Expected, ulong Current, bool Ready, bool Transitioning, GripCharacterStatus Status)[]
        {
            ("same-character",123,123,true,false,GripCharacterStatus.Ready),
            ("unloaded-player",123,0,false,true,GripCharacterStatus.Waiting),
            ("missing-character-id",123,0,true,false,GripCharacterStatus.Waiting),
            ("missing-player-with-known-id",123,123,false,false,GripCharacterStatus.Waiting),
            ("transition-with-known-player",123,123,true,true,GripCharacterStatus.Waiting),
            ("transition-with-other-id",123,456,true,true,GripCharacterStatus.Waiting),
            ("different-ready-character",123,456,true,false,GripCharacterStatus.Changed),
            ("unbound-start-character",0,123,true,false,GripCharacterStatus.Changed),
            ("unbound-and-unavailable",0,0,false,false,GripCharacterStatus.Waiting)
        })
            Check("character-"+c.Name,AetherpoolExchangePolicy.EvaluateCharacter(c.Expected,c.Current,c.Ready,c.Transitioning)==c.Status);
        Check("outward-zone-load-recovers-without-error",new[]
        {
            AetherpoolExchangePolicy.EvaluateCharacter(123,123,true,false),
            AetherpoolExchangePolicy.EvaluateCharacter(123,0,false,true),
            AetherpoolExchangePolicy.EvaluateCharacter(123,0,false,false),
            AetherpoolExchangePolicy.EvaluateCharacter(123,123,true,false)
        }.SequenceEqual(new[]{GripCharacterStatus.Ready,GripCharacterStatus.Waiting,GripCharacterStatus.Waiting,GripCharacterStatus.Ready}));
        Check("real-character-change-still-rejected-after-loading",new[]
        {
            AetherpoolExchangePolicy.EvaluateCharacter(123,456,true,true),
            AetherpoolExchangePolicy.EvaluateCharacter(123,456,true,false)
        }.SequenceEqual(new[]{GripCharacterStatus.Waiting,GripCharacterStatus.Changed}));
        var hohRelic = new FarmingPlan(FarmingMode.Hoard,SaveUse.Create,21,3,true,false,true,true,false)
            { DungeonId=2, RelicMaterial=true, GripExchange=new(60,1) };
        var bossStock = new HohRelicMagiciteSnapshot(2,30,true,true,true,false,3);
        Check("hoh-relic-magicite-enabled",HohRelicMagicitePolicy.ShouldSpendAtBoss(hohRelic,bossStock));
        foreach (var other in new[]
        {
            hohRelic with { DungeonId=1,StartFloor=51 },
            hohRelic with { DungeonId=4 },
            hohRelic with { DungeonId=3 },
            hohRelic with { RelicMaterial=false },
            hohRelic with { Mode=FarmingMode.DeepProgression },
            hohRelic with { OrdinaryDescent=true },
            hohRelic with { FreeExploration=true },
            hohRelic with { ExternalSave=true },
            hohRelic with { CurrentDutyOnly=true },
            hohRelic with { SaveUse=SaveUse.Prepared },
            hohRelic with { StartFloor=31 },
            hohRelic with { OpenSilver=false }
        })
            Check("magicite-other-plan-isolated-"+n,!HohRelicMagicitePolicy.AppliesTo(other) &&
                !HohRelicMagicitePolicy.ShouldSpendAtBoss(other,bossStock));
        Check("magicite-no-plan-disabled",!HohRelicMagicitePolicy.ShouldSpendAtBoss(null,bossStock));
        foreach (int floor in new[]{0,21,29,30,31,40,100})
            Check("magicite-boss-floor-boundary-"+floor,
                HohRelicMagicitePolicy.ShouldSpendAtBoss(hohRelic,bossStock with {Floor=floor})==(floor==30));
        foreach (var blocked in new[]
        {
            bossStock with { DungeonId=1 }, bossStock with { DungeonId=4 },
            bossStock with { BossFloor=false }, bossStock with { InCombat=false },
            bossStock with { LiveHostile=false }, bossStock with { PassageOpen=true },
            bossStock with { Stock=0 }
        })
            Check("magicite-inactive-boss-holds-"+n,!HohRelicMagicitePolicy.ShouldSpendAtBoss(hohRelic,blocked));
        foreach(int floor in new[]{21,25,29})
        {
            Check("ordinary-floor-keeps-magicite-"+floor,!HohMagicitePolicy.ShouldUse(floor,false,false));
            Check("ordinary-overflow-still-uses-magicite-"+floor,HohMagicitePolicy.ShouldUse(floor,true,false));
        }
        var interval = new HohRelicMagiciteCooldown();
        Check("magicite-fresh-request-ready",interval.CanRequest(now));
        interval.ObserveRequestOrConfirmation(now);
        Check("magicite-request-starts-shared-interval",!interval.CanRequest(now.AddSeconds(3)) &&
            !interval.CanRequest(now.AddMilliseconds(10499)) && interval.CanRequest(now.AddMilliseconds(10500)));
        interval.ObserveRequestOrConfirmation(now.AddMilliseconds(300));
        Check("magicite-confirmation-keeps-cooldown",!interval.CanRequest(now.AddMilliseconds(10500)) &&
            interval.CanRequest(now.AddMilliseconds(10800)));
        interval.ObserveRequestOrConfirmation(now.AddMilliseconds(100));
        Check("magicite-late-observation-cannot-shorten-interval",interval.NextRequestAtUtc==now.AddMilliseconds(10800));
        var itemChannel = new FloorItemUseSession();
        interval = new();
        int stock=3, requests=0;
        for (int i=0;i<3;i++)
        {
            DateTime at=i==0 ? now : interval.NextRequestAtUtc;
            Check("magicite-next-boss-item-ready-"+i,interval.CanRequest(at) && itemChannel.Pending==null &&
                HohRelicMagicitePolicy.ShouldSpendAtBoss(hohRelic,bossStock with {Stock=stock}));
            var pending=itemChannel.Begin(new(FloorItemUseKind.Stone,1),FloorItemUsePurpose.Automatic,stock,
                at,"HoH relic boss test",false,0,0,0);
            requests++; interval.ObserveRequestOrConfirmation(at);
            Check("magicite-dispatch-is-not-consumption-"+i,
                itemChannel.Observe(stock,false,at.AddMilliseconds(500))==FloorItemUseConfirmationDecisionKind.PendingConfirmation &&
                !interval.CanRequest(at.AddSeconds(3)));
            stock--;
            Check("magicite-inventory-decrement-confirms-"+i,
                itemChannel.Observe(stock,false,at.AddMilliseconds(600))==FloorItemUseConfirmationDecisionKind.Confirmed);
            itemChannel.Confirm(pending); interval.ObserveRequestOrConfirmation(at.AddMilliseconds(600));
        }
        Check("magicite-all-three-spent-without-once-per-floor-limit",requests==3 && stock==0 && itemChannel.Pending==null &&
            !HohRelicMagicitePolicy.ShouldSpendAtBoss(hohRelic,bossStock with {Stock=stock}));
        return n;
    }
}
