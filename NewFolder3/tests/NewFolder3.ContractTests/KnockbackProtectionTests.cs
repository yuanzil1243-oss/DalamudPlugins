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
        for (int floor = 1; floor <= 100; floor++)
        {
            var candidate = scene with { Floor = floor, Territory = (uint)(1281 + (floor - 1) / 10) };
            bool expected = floor is >= 71 and <= 99;
            Check("pt-floor-" + floor, () =>
            {
                Assert(KnockbackProtectionPolicy.Allows(candidate) == expected);
                Assert(Decide(candidate, candidate).Suppress == expected);
                Assert(Decide(candidate, candidate).Forward == motion);
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
