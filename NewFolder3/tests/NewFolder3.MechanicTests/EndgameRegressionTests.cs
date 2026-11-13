using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

internal static class EndgameRegressionTests
{
    public static IEnumerable<(string, Action)> Cases()
    {
        yield return ("replay81-and91-queued-chest-identity-survives-scan-removal", StableChestIdentity);
        yield return ("ordinary-target-around-corner-switches-to-visible-engaged", VisibleEngaged);
        yield return ("unreachable-target-allows-bounded-pull-and-stays-until-aggro", BoundedPull);
        yield return ("passage-or-consolidation-blocks-new-pull", NoPullWhenBlocked);
        yield return ("priority-fight-cannot-be-replaced-by-visible-ordinary-or-new", PriorityObligation);
        yield return ("healthy-target-does-not-thrash-and-reset-discards-old-floor", TargetProgress);
        yield return ("pt98-fate-reservation-includes-capped-silver-chest", ReservedFate);
        yield return ("pt91-surplus-buffs-retain-one-and-dont-overwrite-effects", SurplusBuffs);
        yield return ("pt99-items-follow-confirmed-strength-steel-haste-fate-serenity", BossSequence);
        yield return ("pt99-missing-forbidden-or-deferred-items-dont-block", MissingBossItems);
        yield return ("pt99-fate-waits-for-two-live-bosses-without-bypassing-buffs", BossReadiness);
        yield return ("new-item-requests-share-one-confirmation-channel", ItemConfirmation);
    }
    private static readonly DateTime Now = new(2026, 11, 11, 11, 0, 0, DateTimeKind.Utc);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static void StableChestIdentity()
    {
        foreach (int room in new[] { 3, 7 })
        {
            var supply = new SupplyChestWorkState();
            using var session = new FloorObjectiveSession(81);
            var initialKey = new RoomObjectiveKey(room, RoomObjectiveCategory.Chests, FloorObjectiveKind.OpenPlannedChest);
            var initial = session.GetOrCreateObjective(initialKey, initialKey.Kind, false);
            session.ObjectiveLedger.ApplyOutcome(initial.Identity, ObjectiveOutcomeKind.Succeeded);
            supply.Update([room]);
            var queued = supply.Bind(new RoomPlanEntry(room, false, true));
            for (int scan = 0; scan < 100; scan++) supply.Update([room]);
            Check(supply.BatchFor(room) == queued.ChestBatch && queued.ChestBatch > 0, "same work must keep one batch");
            var workKey = initialKey with { Batch = queued.ChestBatch };
            var visit = session.GetOrCreateObjective(workKey, workKey.Kind, false);
            Check(visit.Outcome == ObjectiveOutcomeKind.Pending && visit.Identity != initial.Identity, "drop is new work");
            session.ObjectiveLedger.ApplyOutcome(visit.Identity, ObjectiveOutcomeKind.Succeeded);
            var remaining = session.RemoveResolvedWork(queued with { ShouldProbeHoard = true });
            Check(!remaining.ShouldSearchChests && remaining.ShouldProbeHoard, "stale optional work removed without dropping required hoard");
            supply.Update([]); // scan changes between plan generation and objective start, as in the replay
            var delayed = session.GetOrCreateObjective(initialKey with { Batch = queued.ChestBatch }, workKey.Kind, false);
            Check(delayed.Identity == visit.Identity && delayed.Identity != initial.Identity, "queued batch cannot collapse back to batch zero");
            supply.Update([room]);
            var later = supply.Bind(new RoomPlanEntry(room, false, true));
            Check(later.ChestBatch != queued.ChestBatch, "a later drop gets fresh work");
            Check(session.RemoveResolvedWork(later).ShouldSearchChests, "fresh batch must survive completed earlier batches");
            Check(session.GetRoomProgress(room).ChestsSearched, "original progress remains intact");
        }
    }
    private static OutputTargetCandidate Mob(ulong id, bool hit, bool engaged = true, int priority = 0, uint hp = 100) =>
        new(id, hp, hit ? 5 : 30, priority, engaged, hit);
    private static void VisibleEngaged()
    {
        var state = new MobTargetSwitchState();
        Check(state.Select([Mob(1, true)], true, Now) == 1, "first");
        Check(state.Select([Mob(1, false), Mob(2, true)], true, Now.AddSeconds(.2)) == 2,
            "switch before position recovery gets trapped on the other side of the wall");
    }
    private static void BoundedPull()
    {
        var state = new MobTargetSwitchState();
        OutputTargetCandidate[] pool = [Mob(1, false), Mob(2, true, false)];
        Check(state.Select(pool, true, Now) == 1, "wait before extra pull");
        Check(state.Select(pool, true, Now.AddSeconds(2)) == 2, "select visible new mob");
        Check(state.Select(pool, true, Now.AddSeconds(2.1)) == 2, "hold for external rotation");
        Check(state.Select([Mob(1, false), Mob(2, true, hp: 90)], true, Now.AddSeconds(2.2)) == 2, "engaged transition");
    }
    private static void NoPullWhenBlocked()
    {
        var state = new MobTargetSwitchState();
        OutputTargetCandidate[] pool = [Mob(1, false), Mob(2, true, false)];
        state.Select(pool, false, Now);
        Check(state.Select(pool, false, Now.AddSeconds(10)) == 1, "gate remains authoritative even after timeout");
    }
    private static void PriorityObligation()
    {
        var state = new MobTargetSwitchState();
        OutputTargetCandidate[] pool = [Mob(1, false, priority: 2), Mob(2, true, priority: 1), Mob(3, true, false)];
        state.Select(pool, true, Now);
        Check(state.Select(pool, true, Now.AddSeconds(10)) == 1, "mimic obligation");
    }
    private static void TargetProgress()
    {
        var state = new MobTargetSwitchState();
        state.Select([Mob(1, true), Mob(2, true, hp: 200)], true, Now);
        Check(state.Select([Mob(1, true, hp: 300), Mob(2, true, hp: 190)], true, Now.AddSeconds(4)) == 2, "hold progressing target");
        state.Reset();
        Check(state.Select([], true, Now.AddSeconds(5)) == 0, "no stale target on new floor");
    }
    private static void ReservedFate()
    {
        foreach (var mode in new[] { FarmingMode.DeepProgression, FarmingMode.FreeExploration })
        {
            bool reserved = EndgameItemPolicy.ReserveFate(4, 98, mode);
            Check(reserved && !EndgameItemPolicy.ReserveFate(4, 97, mode) && !EndgameItemPolicy.ReserveFate(4, 99, mode), "98 exception only");
            Check(SilverChestOvercapPolicy.ReliefStone(3, 0, 0, false, false, reserved) == 0, "skip full fate chest rather than spend reservation");
            Check(SilverChestOvercapPolicy.ReliefStone(1, 1, 1, false, false, reserved) == 2, "wisdom relief");
            Check(SilverChestOvercapPolicy.ReliefStone(0, 0, 3, true, true, reserved) == 3, "all serenity exception preserved");
        }
        Check(!EndgameItemPolicy.ReserveFate(4, 98, FarmingMode.Hoard), "other modes unchanged");
    }
    private static void SurplusBuffs()
    {
        Check(EndgameItemPolicy.UseHighFloorBuff(4, 91, FarmingMode.DeepProgression, 2, false, true), "spend surplus");
        Check(!EndgameItemPolicy.UseHighFloorBuff(4, 98, FarmingMode.DeepProgression, 1, false, true), "reserve last");
        Check(!EndgameItemPolicy.UseHighFloorBuff(4, 98, FarmingMode.DeepProgression, 3, true, true), "active effect");
        Check(!EndgameItemPolicy.UseHighFloorBuff(4, 98, FarmingMode.DeepProgression, 3, false, false), "no combat work");
        Check(!EndgameItemPolicy.UseHighFloorBuff(4, 90, FarmingMode.DeepProgression, 3, false, true), "scope");
        Check(ProgressionItemPolicy.AllowsCombat(FloorInitPlanner.HastePomanderSlotIndex, false, FloorItemUsePurpose.HighFloorBuff), "combat dispatch allowed");
        Check(!ProgressionItemPolicy.AllowsCombat(4, false, FloorItemUsePurpose.HighFloorBuff), "other items not broadened");
    }
    private static void BossSequence()
    {
        var actual = new List<Pt99ItemStep>();
        bool strength = true, steel = true, haste = true, fateConfirmed = false, serenity = true;
        for (int i = 0; i < 6; i++)
        {
            var step = EndgameItemPolicy.NextBossStep(strength, steel, haste, true, fateConfirmed, true, serenity);
            actual.Add(step);
            switch (step)
            {
                case Pt99ItemStep.Strength: strength = false; break;
                case Pt99ItemStep.Steel: steel = false; break;
                case Pt99ItemStep.Haste: haste = false; break;
                case Pt99ItemStep.Fate: fateConfirmed = true; break;
                case Pt99ItemStep.Serenity: serenity = false; break;
            }
        }
        Check(actual.SequenceEqual(new[] { Pt99ItemStep.Strength, Pt99ItemStep.Steel, Pt99ItemStep.Haste,
            Pt99ItemStep.Fate, Pt99ItemStep.Serenity, Pt99ItemStep.None }), "single ordered sequence");
    }
    private static void MissingBossItems()
    {
        Check(EndgameItemPolicy.NextBossStep(false, false, false, false, false, false, true) == Pt99ItemStep.Serenity, "missing/deferred fate");
        Check(EndgameItemPolicy.NextBossStep(false, false, false, true, true, true, false) == Pt99ItemStep.None, "remaining fate stock cannot summon repeatedly");
    }
    private static void BossReadiness()
    {
        Check(EndgameItemPolicy.NextBossStep(false, false, false, true, false, false, true) == Pt99ItemStep.WaitForFateTargets, "don't waste fate on unavailable boss");
        Check(EndgameItemPolicy.NextBossStep(true, false, false, true, false, false, true) == Pt99ItemStep.Strength, "buff before waiting");
    }
    private static void ItemConfirmation()
    {
        var channel = new FloorItemUseSession();
        var pending = channel.Begin(new(FloorItemUseKind.Stone, 1), FloorItemUsePurpose.BossFate, 2, Now,
            "boss", false, 0, 0, 0, 0);
        Check(channel.Pending?.Purpose == FloorItemUsePurpose.BossFate, "pending blocks later request at adapter gate");
        Check(channel.Observe(2, false, Now.AddSeconds(.1)) == FloorItemUseConfirmationDecisionKind.PendingConfirmation, "dispatch is not confirmation");
        Check(channel.Observe(1, false, Now.AddSeconds(.2)) == FloorItemUseConfirmationDecisionKind.Confirmed, "stock decrease");
        channel.Confirm(pending);
        Check(channel.Pending == null, "release single channel after confirmation");
    }
}
