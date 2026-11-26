// SPDX-License-Identifier: GPL-3.0-only
namespace AntiDisplacementV2;

public enum MotionMode { Suppress, Replace, Observe }
public readonly record struct Motion(nint Actor, float Angle, float Distance, float Duration, byte NativeMode, ulong Flags);
public readonly record struct Scene(nint Actor, uint EntityId, uint Territory, uint Dungeon, int Floor, int Thread, bool Ready)
{
    public bool Eligible => Ready && Actor != 0 && EntityId is not (0 or 0xE0000000) && Thread > 0 &&
        DungeonScope.Matches(Territory, Dungeon, Floor);
}
public static class DungeonScope
{
    public static bool Matches(uint territory, uint dungeon, int floor)
    {
        (uint expected, int first) = territory switch
        {
            >= 561 and <= 565 => (1u, 1 + ((int)territory - 561) * 10),
            >= 593 and <= 607 => (1u, 51 + ((int)territory - 593) * 10),
            770 => (2u, 1), 771 => (2u, 11), 772 => (2u, 21), 782 => (2u, 31),
            773 => (2u, 41), 783 => (2u, 51), 774 => (2u, 61), 784 => (2u, 71),
            775 => (2u, 81), 785 => (2u, 91),
            >= 1099 and <= 1108 => (3u, 1 + ((int)territory - 1099) * 10),
            >= 1281 and <= 1290 => (4u, 1 + ((int)territory - 1281) * 10),
            _ => (0u, 0),
        };
        return expected != 0 && dungeon == expected && floor >= first && floor <= first + 9;
    }
}

public readonly record struct Options(MotionMode Mode, float Distance, float Duration)
{
    public bool Valid => Enum.IsDefined(Mode) && float.IsFinite(Distance) && Distance is >= 0 and <= 30 &&
        float.IsFinite(Duration) && Duration is >= 0 and <= 5;
}
public readonly record struct Decision(bool Suppress, Motion Forward, string Reason, bool Local);

/// <summary>Pure production policy. Native mode/flags remain opaque and are always preserved.</summary>
public static class MotionPolicy
{
    public static Decision Decide(bool enabled, Options options, Scene cached, Scene live,
        long updatedAt, long now, int thread, Motion motion)
    {
        Decision Pass(string why, bool local = false) => new(false, motion, why, local);
        if (!enabled) return Pass("off");
        if (!options.Valid) return Pass("invalid-settings");
        if (thread != cached.Thread) return Pass("different-thread");
        if (now < updatedAt || now - updatedAt > 500) return Pass("stale-scene");
        if (!cached.Eligible || !live.Eligible || cached != live) return Pass("scene-unavailable");
        if (motion.Actor != live.Actor) return Pass("other-character");
        if (!float.IsFinite(motion.Angle) || !float.IsFinite(motion.Distance) || !float.IsFinite(motion.Duration))
            return Pass("invalid-motion", true);
        if (motion.Distance == 0) return Pass("zero-distance", true);
        return options.Mode switch
        {
            MotionMode.Suppress => new(true, motion, "suppressed", true),
            MotionMode.Replace => new(false, motion with { Distance = options.Distance, Duration = options.Duration }, "replaced", true),
            _ => Pass("observed", true),
        };
    }
}

public static class MotionDispatcher
{
    public static void Dispatch(Motion motion, Func<Motion, Decision> decide, Action<Motion> original)
    {
        Decision result;
        try { result = decide(motion); }
        catch { result = new(false, motion, "decision-failed", false); }
        if (!result.Suppress) original(result.Forward);
    }
}
