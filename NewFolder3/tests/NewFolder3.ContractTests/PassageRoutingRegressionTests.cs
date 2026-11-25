using System.Numerics;
using DeepDungeon.Fsd.Core;

namespace NewFolder3;

// Exercise the actual upstream .23 policy; do not duplicate its matching algorithm.
internal static class PassageRoutingRegressionTests
{
    private const uint HohBeacon = 2009507;
    private static readonly PassageSearchPoint[] Rooms = [new(1, Vector3.Zero), new(2, new(10, 0, 0))];

    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test)
        { test(); count++; Console.WriteLine("  ok  passage-23-" + name); }

        Check("room-remains-stable-across-beacon-load-unload", () =>
        {
            PassageSearchPoint[] rooms = [new(1, Vector3.Zero), new(2, new(20, 0, 0))];
            var before = Pick(rooms, []);
            var loaded = Pick(rooms, [Beacon(101, new(8, 0, 0)), Beacon(102, new(21, 0, 0))]);
            var unloaded = Pick(rooms, []);
            Require(before.Room == 1 && loaded.Room == 1 && unloaded.Room == 1);
            Require(before.IsSearch && !loaded.IsSearch && unloaded.IsSearch);
            Require(loaded.Position == new Vector3(8, 0, 0));
        });
        Check("two-beacons-near-one-room-still-pair-one-to-one", () =>
        {
            PassageBeacon[] actors = [Beacon(101, new(1, 0, 0)), Beacon(102, new(2, 0, 0))];
            Require(Pick(Rooms, actors, room: 1).ActorId == 101);
            Require(Pick(Rooms, actors, room: 2).ActorId == 102);
        });
        Check("object-and-map-enumeration-order-do-not-change-pairing", () =>
        {
            PassageBeacon[] actors = [Beacon(101, new(1, 0, 0)), Beacon(102, new(2, 0, 0))];
            var first = Pick(Rooms, actors, room: 2);
            var reordered = Pick(Rooms.Reverse().ToArray(), actors.Reverse().ToArray(), room: 2);
            Require(first == reordered);
        });
        Check("room-matching-uses-horizontal-distance", () =>
        {
            PassageBeacon[] actors = [Beacon(101, new(1, 1000, 0)), Beacon(102, new(9, -1000, 0))];
            Require(Pick(Rooms, actors, room: 1).ActorId == 101);
            Require(Pick(Rooms, actors, room: 2).ActorId == 102);
        });
        Check("partially-loaded-beacon-pairs-with-closest-marked-room", () =>
        {
            PassageBeacon[] actors = [Beacon(101, new(9, 0, 0))];
            Require(Pick(Rooms, actors, room: 1).IsSearch);
            Require(Pick(Rooms, actors, room: 2).ActorId == 101);
        });
        Check("extra-live-beacons-do-not-replace-the-nearest-room-match", () =>
        {
            var goal = Pick([Rooms[0]], [Beacon(101, new(1, 0, 0)), Beacon(102, new(20, 0, 0))]);
            Require(goal.Room == 1 && goal.ActorId == 101);
        });
        Check("unmarked-exit-retains-exact-live-traversal-coordinates", () =>
        {
            Vector3 position = new(7, 3, 9);
            var goal = Pick([], [Beacon(101, position)]);
            Require(goal.Room == -1 && goal.ActorId == 101 && goal.Position == position);
        });
        Check("requested-room-does-not-fall-through-to-a-different-room", () =>
            Require(Select(Rooms, [Beacon(101, new(1, 0, 0))], room: 7) == null));
        Check("owned-transition-survives-unknown-transient-beacon-state", () =>
        {
            var goal = Pick(Rooms, [Beacon(101, Vector3.Zero, 0), Beacon(102, new(10, 0, 0))], committed: 101);
            Require(goal.ActorId == 101 && goal.Room == 1);
        });
        Check("removed-owned-beacon-releases-to-another-valid-exit", () =>
        {
            var goal = Pick(Rooms, [Beacon(101, Vector3.Zero, 0x400), Beacon(102, new(10, 0, 0))], committed: 101);
            Require(goal.ActorId == 102 && goal.Room == 2);
        });
        Check("rejected-exit-does-not-outrank-another-usable-room", () =>
        {
            var goal = Pick(Rooms, [Beacon(101, Vector3.Zero), Beacon(102, new(10, 0, 0))], eligible: g => g.ActorId != 101);
            Require(goal.ActorId == 102 && goal.Room == 2);
        });
        Check("nonfinite-live-coordinates-are-not-selected", () =>
        {
            PassageBeacon[] actors = [Beacon(101, new(float.NaN, 0, 0)),
                Beacon(102, new(0, 0, float.PositiveInfinity)), Beacon(103, new(5, 0, 0))];
            Require(Pick([], actors).ActorId == 103);
            Require(Select([], actors[..2]) == null);
        });
        return count;
    }

    private static PassageBeacon Beacon(ulong id, Vector3 position, ushort state = 4) =>
        new(id, HohBeacon, state, position);

    private static PassageGoal? Select(PassageSearchPoint[] rooms, PassageBeacon[] actors,
        int? room = null, ulong committed = 0, Func<PassageGoal, bool>? eligible = null) =>
        PassageGoalPolicy.Select(actors, rooms, p => Vector3.Distance(new(-5, 0, 0), p),
            eligible ?? (_ => true), committed, room);

    private static PassageGoal Pick(PassageSearchPoint[] rooms, PassageBeacon[] actors,
        int? room = null, ulong committed = 0, Func<PassageGoal, bool>? eligible = null) =>
        Select(rooms, actors, room, committed, eligible) ?? throw new Exception("Expected an exit goal.");

    private static void Require(bool value)
    { if (!value) throw new Exception("Upstream .23 passage-routing regression."); }
}
