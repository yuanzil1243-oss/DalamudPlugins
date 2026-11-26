// SPDX-License-Identifier: GPL-3.0-only
using AntiDisplacementV2;

namespace DeepDungeon.Fsd.Core;

/// <summary>Host restriction; the reusable native capability retains its original scene checks.</summary>
public static class KnockbackProtectionPolicy
{
    public static bool Allows(Scene scene) => scene.Eligible;

    public static Decision Decide(bool enabled, Scene cached, Scene live, long updatedAt,
        long now, int thread, Motion motion) =>
        MotionPolicy.Decide(enabled && Allows(cached) && Allows(live),
            new(MotionMode.Suppress, 0, 0.1f), cached, live, updatedAt, now, thread, motion);
}
