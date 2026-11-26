using AntiDisplacementV2;
using DeepDungeon.Fsd.Core;

namespace NewFolder3;

internal static class KnockbackProtectionTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  knockback-" + name); }
        var scene = new Scene((nint)0x123450, 0x4242, 1290, 4, 99, Environment.CurrentManagedThreadId, true);
        var motion = new Motion(scene.Actor, 0.25f, 20, 0.75f, byte.MaxValue, ulong.MaxValue);
        Decision Decide(Scene cached, Scene live, bool enabled = true, long at = 100, long now = 101, int? thread = null) =>
            KnockbackProtectionPolicy.Decide(enabled, cached, live, at, now, thread ?? scene.Thread, motion);
        var groups = new List<(uint Dungeon, uint Territory, int First)>();
        for (int i = 0; i < 5; i++) groups.Add((1, (uint)(561 + i), 1 + i * 10));
        for (int i = 0; i < 15; i++) groups.Add((1, (uint)(593 + i), 51 + i * 10));
        uint[] hoh = [770, 771, 772, 782, 773, 783, 774, 784, 775, 785];
        for (int i = 0; i < 10; i++) groups.Add((2, hoh[i], 1 + i * 10));
        for (int i = 0; i < 10; i++) groups.Add((3, (uint)(1099 + i), 1 + i * 10));
        for (int i = 0; i < 10; i++) groups.Add((4, (uint)(1281 + i), 1 + i * 10));
        foreach (var group in groups)
        {
            foreach (int floor in Enumerable.Range(group.First, 10))
            {
                var candidate = scene with { Dungeon = group.Dungeon, Floor = floor, Territory = group.Territory };
                Check($"dungeon-{group.Dungeon}-floor-{floor}", () =>
                {
                    Assert(KnockbackProtectionPolicy.Allows(candidate));
                    Assert(Decide(candidate, candidate).Suppress);
                    Assert(Decide(candidate, candidate).Forward == motion);
                    var pull = motion with { Distance = -20 };
                    Assert(KnockbackProtectionPolicy.Decide(true, candidate, candidate, 100, 101, scene.Thread, pull).Suppress);
                });
            }
            Check($"territory-boundaries-{group.Dungeon}-{group.First}", () =>
            {
                Assert(!KnockbackProtectionPolicy.Allows(scene with { Dungeon = group.Dungeon,
                    Territory = group.Territory, Floor = group.First - 1 }));
                Assert(!KnockbackProtectionPolicy.Allows(scene with { Dungeon = group.Dungeon,
                    Territory = group.Territory, Floor = group.First + 10 }));
            });
        }
        foreach (uint dungeon in new uint[] { 0, 1, 2, 3, 5 })
            Check("other-dungeon-" + dungeon, () =>
            {
                var other = scene with { Dungeon = dungeon };
                Assert(!KnockbackProtectionPolicy.Allows(other) && !Decide(other, other).Suppress);
            });
        foreach (var invalid in new[] { default(Scene), scene with { Ready = false }, scene with { Actor = 0 },
            scene with { EntityId = 0xE0000000 }, scene with { Territory = 1281 }, scene with { Territory = 1333 },
            scene with { Floor = 70 }, scene with { Floor = 100 }, scene with { Dungeon = 1 },
            scene with { Actor = 42 }, scene with { EntityId = 22 }, scene with { Thread = scene.Thread + 1 } })
            Check("live-context-change-pass-through-" + count, () =>
            {
                Assert(!Decide(scene, invalid).Suppress && Decide(scene, invalid).Forward == motion);
                Assert(!Decide(invalid, scene).Suppress && Decide(invalid, scene).Forward == motion);
            });
        Check("off-stale-clock-and-thread-pass-through", () =>
        {
            Assert(!Decide(scene, scene, false).Suppress);
            Assert(!Decide(scene, scene, now: 601).Suppress);
            Assert(!Decide(scene, scene, now: 99).Suppress);
            Assert(!Decide(scene, scene, thread: scene.Thread + 1).Suppress);
            Assert(Decide(scene, scene, now: 600).Suppress);
        });
        Check("other-actors-and-housekeeping-pass-through", () =>
        {
            foreach (var value in new[] { motion with { Actor = 42 }, motion with { Distance = 0 },
                motion with { Angle = float.NaN }, motion with { Duration = float.PositiveInfinity } })
            {
                var decision = KnockbackProtectionPolicy.Decide(true, scene, scene, 100, 101, scene.Thread, value);
                Assert(!decision.Suppress && decision.Forward == value);
            }
            var pull = motion with { Distance = -20 };
            Assert(KnockbackProtectionPolicy.Decide(true, scene, scene, 100, 101, scene.Thread, pull).Suppress);
        });
        Check("dispatcher-forwards-once-and-never-retries-original-errors", () =>
        {
            int originals = 0;
            Motion forwarded = default;
            void Original(Motion value) { originals++; forwarded = value; }
            MotionDispatcher.Dispatch(motion, m => Decide(scene, scene), Original);
            Assert(originals == 0);
            MotionDispatcher.Dispatch(motion, m => Decide(scene, scene, false), Original);
            Assert(originals == 1 && forwarded == motion);
            MotionDispatcher.Dispatch(motion, _ => throw new InvalidOperationException(), Original);
            Assert(originals == 2 && forwarded == motion);
            bool caught = false;
            try { MotionDispatcher.Dispatch(motion, m => Decide(scene, scene, false), _ => { originals++; throw new ApplicationException(); }); }
            catch (ApplicationException) { caught = true; }
            Assert(caught && originals == 3);
        });
        return count;
    }

    private static void Assert(bool value)
    { if (!value) throw new InvalidOperationException("Knockback protection restriction regression"); }
}
