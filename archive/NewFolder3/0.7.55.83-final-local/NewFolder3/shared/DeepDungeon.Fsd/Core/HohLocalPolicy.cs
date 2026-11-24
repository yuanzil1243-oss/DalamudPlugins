namespace DeepDungeon.Fsd.Core;

/// <summary>Local HoH scope. PT checkpoint and item rules must never leak into HoH.</summary>
public static class HohLocalPolicy
{
    public const uint DungeonId = 2;
    public const uint ShrineBaseId = 2009505;
    public const uint ResultExitBaseId = 2005809;
    public static bool IsNewSaveStart(int floor) => floor is 1 or 21;
    public static int RestartFloor(int floor) => floor >= 21 ? 21 : 1;
    public static CheckpointMenuResult SelectStartOption(IReadOnlyList<string> options, int floor, out int index)
    {
        index = -1;
        if (!IsNewSaveStart(floor)) return CheckpointMenuResult.NotFloorset;
        bool numbered = false;
        int matches = 0;
        for (int i = 0; i < options.Count; i++)
        {
            var numbers = System.Text.RegularExpressions.Regex.Matches(options[i].Normalize(System.Text.NormalizationForm.FormKC), "[0-9]+");
            if (numbers.Count == 0) continue;
            if (numbers.Count != 1 || !int.TryParse(numbers[0].Value, out int candidate) || !IsNewSaveStart(candidate))
                return CheckpointMenuResult.NotFloorset;
            numbered = true;
            if (candidate == floor) { index = i; matches++; }
        }
        if (!numbered) return CheckpointMenuResult.NotFloorset;
        if (matches == 1) return CheckpointMenuResult.Selected;
        index = -1;
        return matches == 0 ? CheckpointMenuResult.Missing : CheckpointMenuResult.Ambiguous;
    }
    public static bool CanConfirmShrine(ulong interacted, ulong observed, bool targetable,
        long completionBefore, long completionNow, ulong exitBefore, ulong exitNow, bool failed) =>
        !failed && interacted != 0 && interacted == observed && !targetable &&
        (completionNow > completionBefore || exitNow != 0 && exitNow != exitBefore);
}

/// <summary>Initial conservative resource budget; thresholds are policy, not game mechanics.</summary>
public static class HohItemPolicy
{
    // DeepDungeon row 2 PomanderSlot: slot 11 = item 17 (Frailty), slot 15 = item 19 (Petrification).
    // These are HoH slots, not PT's slot 11 Haste / slot 15 guidance.
    public const uint Witching = 9, Frailty = 11, Concealment = 12, Petrification = 15;
    public static bool IsHardFloor(int floor) => floor is >= 84 and <= 86 or >= 94 and <= 96;
    public static bool IsEmergency(uint hp, uint maxHp, int attackers) =>
        hp > 0 && maxHp > 0 && attackers > 0 && (100d * hp / maxHp < 35 || attackers >= 2 && 100d * hp / maxHp < 70);
    public static bool AllowPomander(int floor, uint slot, int count, bool boss, bool emergency, bool overflow,
        bool centaurEngaged = false, int magiciteStock = 0)
    {
        if (floor is < 1 or >= 100 || count <= 0) return false;
        return slot switch
        {
            0 or 1 => floor >= 71 || overflow,
            2 => boss || floor >= 91 || count > 1 && (floor >= 61 || overflow),
            3 => boss || floor == 99 || (floor >= 91 ? centaurEngaged : count > 2 && (floor >= 71 || emergency || overflow)),
            4 => true, // Supply-shortage / actual chest-overcap gate is applied by the executor.
            5 => true, // Flight may accelerate any eligible next normal floor; executor retains next-floor/status gates.
            6 => false, // Alteration needs mimic/mandragora-aware selection before enabling.
            7 => true,
            8 => floor < 91 && overflow,
            Witching => false, // This local HoH edition fights with packet Y=-7; Witching does not protect it.
            10 => floor >= 91 || count > 2, // Release the final stock at99 for any harmful removable effect.
            Petrification => !boss && (floor >= 94 || overflow || floor is >= 81 and <= 89 && magiciteStock + count > 4),
            Concealment => false,
            13 => false, // Initial local edition focuses on floor progression, not optional hoards.
            14 => floor >= 71,
            Frailty => boss || floor >= 91 || count == 3 && (overflow || floor >= 81),
            _ => false
        };
    }

    public static bool SupplyBuffsLow(int strength, int steel) => strength <= 1 || steel <= 1;
    public static uint? CombatBuffToMaintain(int floor, bool centaurEngaged, bool clearingNeeded,
        int steel, bool steelActive, int strength, bool strengthActive) => floor is < 91 or > 99 ? null :
        (centaurEngaged || floor == 99 && clearingNeeded) && steel > 0 && !steelActive ? FloorInitPlanner.SteelPomanderSlotIndex :
        clearingNeeded && strength > 0 && !strengthActive ? FloorInitPlanner.StrengthPomanderSlotIndex : null;
    public static bool AllowPetrification(int floor, bool boss, bool passageOpen, bool magiciteConfirmed,
        bool active, bool overflow, int count, int magiciteStock = 0) => !boss && !passageOpen && !magiciteConfirmed && (!active || overflow) &&
        count > 0 && (floor is >= 94 and <= 99 || floor is >= 1 and <= 99 && overflow && count == 3 ||
            floor is >= 81 and <= 89 && ClearStockAllowsUse(floor, magiciteStock, count, false));

    // The combined reserve is four, with magicite preferred: normally carry three magicites plus at least one stone curse.
    // Confirmed replacement chests and emergencies are separate exception channels.
    public static bool ClearStockAllowsUse(int floor, int magicite, int petrification, bool usingMagicite) =>
        floor is >= 91 and <= 99 || floor is >= 1 and <= 89 && floor % 10 != 0 &&
            !usingMagicite && petrification > 0 && magicite + petrification > 4;
    public static bool AllowBossFrailty(uint hp, uint maxHp, int confirmedUses) =>
        hp > 0 && maxHp > 0 && (double)hp / maxHp >= .30 && confirmedUses < 2;
    public static bool AllowBossStrengthRefresh(uint hp, uint maxHp) =>
        hp > 0 && maxHp > 0 && (double)hp / maxHp >= .30;

    public static bool ShouldUseMagicite(int floor, int stock, bool passageOpen,
        bool emergency, bool hasEnemies, bool usedThisFloor) =>
        floor is >= 1 and <= 99 && floor % 10 != 0 && stock > 0 && !passageOpen &&
        hasEnemies && !usedThisFloor &&
        // Before 91 retain magicite, spending only surplus petrification above the combined four-item reserve.
        // User-confirmed Jinba window: spend clear items on94–97 instead of saving them for98/99.
        // Leftovers on98/99 are also usable; never hoard stock beyond the final combat floor.
        // Chest-overcap relief has its own path; emergencies may spend the reserve.
        (emergency || floor >= 94);
}

/// <summary>One stable floor owns this budget. A dispatch is not proof of consumption.</summary>
public sealed class HohFloorItemBudget
{
    private readonly HashSet<uint> _floorDispatches = new();
    private readonly Dictionary<uint, DateTime> _nextPomander = new();
    public bool MagiciteConfirmed { get; private set; }
    public int BossFrailtyConfirmed { get; private set; }
    public bool AllowsPomander(uint slot, DateTime now) =>
        !(slot is 0 or 1 && _floorDispatches.Contains(slot)) &&
        (!_nextPomander.TryGetValue(slot, out var next) || now >= next);
    public void RecordPomanderConfirmed(uint slot, DateTime now, bool boss = false)
    {
        if (slot is 0 or 1) _floorDispatches.Add(slot);
        // Policy delays avoid consuming multiple items while native active flags settle.
        if (slot == HohItemPolicy.Witching) _nextPomander[slot] = now.AddSeconds(60);
        if (slot == HohItemPolicy.Frailty) _nextPomander[slot] = now.AddMinutes(3);
        if (slot == FloorInitPlanner.SerenityPomanderSlotIndex) _nextPomander[slot] = now.AddSeconds(5);
        if (slot == HohItemPolicy.Frailty && boss) BossFrailtyConfirmed++;
    }
    public void RecordMagiciteConfirmed() => MagiciteConfirmed = true;
}

/// <summary>Explicit local HoH recovery only; never adopts another dungeon or an implicit save.</summary>
public static class HohCurrentDutyResumePolicy
{
    public static bool Allows(FarmingPlan plan, int? slot, uint dungeonId, int floor,
        bool transitioning, bool stateReadFailed, bool playerAlive, int? timerRemaining) =>
        plan.DungeonId == HohLocalPolicy.DungeonId && dungeonId == HohLocalPolicy.DungeonId &&
        plan.Mode == FarmingMode.FreeExploration && plan.SaveUse == SaveUse.Create &&
        slot is >= 0 and <= 1 && floor is >= 1 and <= 99 &&
        !transitioning && !stateReadFailed && playerAlive && timerRemaining is > 0 &&
        plan.StopAfterFloor is { } stop && stop >= (floor - 1) / 10 * 10 + 10;

    public static bool UsesBoundedReposition(FarmingMode? mode, uint dungeonId, int floor) =>
        mode == FarmingMode.FreeExploration && dungeonId == HohLocalPolicy.DungeonId &&
        floor is >= 1 and <= 99 && floor % 10 != 0;
}
