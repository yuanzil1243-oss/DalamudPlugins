// SPDX-License-Identifier: AGPL-3.0-or-later
using AntiDisplacementV2;

namespace ForcedMovementLab;

/// <summary>Host-only boundary: the native deep-dungeon identity must agree with the territory and floor.</summary>
internal static class ForcedMovementScope
{
    internal static bool Allows(Scene scene, long now) => scene.Fresh(now) && scene.Thread > 0 &&
        DungeonScope.Matches(scene.Territory, scene.Dungeon, scene.Floor);

    internal static bool Matches(Scene cached, Scene live, long now, int thread) =>
        Allows(cached, now) && Allows(live, now) && thread == cached.Thread && thread == live.Thread &&
        cached.ActorId == live.ActorId && cached.ActorAddress == live.ActorAddress &&
        cached.ConditionsAddress == live.ConditionsAddress && cached.Territory == live.Territory &&
        cached.Dungeon == live.Dungeon && cached.Floor == live.Floor;
}
