using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class MobCombatPolicyTests
{
    public static int Run()
    {
        TestPriorityAndFloorScope();
        TestStickyTargetWithinPriority();
        TestConsolidationLatch();
        TestDeathEvidenceAttribution();
        Console.WriteLine("  ok  mob-priority-consolidation-and-death-evidence");
        return 1;
    }

    private static void TestPriorityAndFloorScope()
    {
        const uint mimic = 14264, wolf = 14194, flower = 14222;
        Assert(MobCombatPolicy.Priority(4, 71, mimic) > MobCombatPolicy.Priority(4, 71, wolf),
            "Mimic must outrank the rock-eating wolf.");
        Assert(MobCombatPolicy.Priority(4, 93, mimic) > MobCombatPolicy.Priority(4, 93, flower),
            "Mimic must outrank the summoning flower.");
        Assert(MobCombatPolicy.Priority(4, 71, wolf) == MobCombatPolicy.Priority(4, 93, flower),
            "Wolf and flower share one priority tier.");
        Assert(MobCombatPolicy.Priority(4, 80, wolf) == 0 && MobCombatPolicy.Priority(4, 92, flower) == 0,
            "Priority must stay within recorded floors.");
    }

    private static void TestStickyTargetWithinPriority()
    {
        CombatEnemy ordinary = new(1, 100, 10, 1, true);
        CombatEnemy wolf = new(2, 14194, 500, 5, true);
        CombatEnemy mimic = new(3, 14264, 900, 8, true);
        var target = MobCombatPolicy.Select(4, 71, [ordinary, wolf, mimic], stickyId: 2);
        Assert(target?.Id == 3, "Higher-priority mimic must override sticky wolf target.");
        var sameTier = MobCombatPolicy.Select(4, 71, [wolf, ordinary], stickyId: 2);
        Assert(sameTier?.Id == 2, "Sticky target must hold within the same priority tier.");
    }

    private static void TestConsolidationLatch()
    {
        var state = new CombatConsolidationState();
        Assert(!state.Update(true, 2, 3), "Count below threshold must not trigger consolidation.");
        Assert(state.Update(true, 3, 3), "Threshold count must trigger consolidation.");
        Assert(state.Update(true, 1, 3), "Consolidation stays active until all engaged enemies are gone.");
        Assert(!state.Update(true, 0, 3), "No engaged enemies releases consolidation.");
    }

    private static void TestDeathEvidenceAttribution()
    {
        DateTime now = DateTime.UtcNow;
        var direct = new DamageEvidence(now.AddMilliseconds(-100), 1, 14194, 14194,
            "交错路食岩狼", 9001, 1, "腐烂惡臭", 200_000, 1, 176_311);
        Assert(DeathEvidencePolicy.IsLethalCandidate(direct), "Direct damage covering pre-hit HP is a lethal candidate.");
        Assert(!DeathEvidencePolicy.IsLethalCandidate(direct with { Damage = 50_000 }),
            "Sublethal damage must not be reported as the confirmed killer.");
        Assert(DeathEvidencePolicy.RecentDamage([direct], now)?.ActionId == 9001,
            "Recent damage should remain available as evidence.");
        Assert(DeathEvidencePolicy.RecentDamage([direct], now.AddSeconds(4)) == null,
            "Stale damage must not be attributed to a later death.");
        Assert(DeathEvidencePolicy.DecodeDamage(2, 0x40, 7) == 131_079,
            "Damage decoder must preserve the high-damage flag.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
