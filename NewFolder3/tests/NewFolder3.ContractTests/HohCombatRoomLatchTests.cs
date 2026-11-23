using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class HohCombatRoomLatchTests
{
    internal static int Run()
    {
        var latch = new HohCombatRoomLatch();
        int[] doorwayCandidates = [12, 13, 12, 13, 12, 13, 13, 12];
        foreach (int candidate in doorwayCandidates)
        {
            int chosen = latch.Resolve(2, 31, candidate, 899, 900);
            if (chosen != 12) throw new Exception("Doorway jitter changed the selected room.");
        }
        int[] floor52DoorwayCandidates = [17, 16, 17, 16, 17, 16, 17];
        foreach (int candidate in floor52DoorwayCandidates)
        {
            int chosen = latch.Resolve(2, 52, candidate, 899, float.PositiveInfinity);
            if (chosen != 17) throw new Exception("Missing center distance must not make room selection flap.");
        }
        if (latch.Resolve(2, 52, 16, 899, 900) != 17) throw new Exception("A 1-unit lead must not reverse the doorway latch.");
        if (latch.Resolve(2, 52, 16, 800, 900) != 16) throw new Exception("A clear move into the neighboring room did not switch the latch.");
        if (latch.Resolve(2, 31, 13, 800, 900) != 13) throw new Exception("A clear move into the next room did not switch the latch.");
        if (latch.Resolve(2, 32, 12, 900, 0) != 12) throw new Exception("A new floor retained the previous room.");
        if (latch.Resolve(4, 32, 7, 900, 0) != 7) throw new Exception("A new dungeon retained the previous room.");
        Console.WriteLine("  ok  hoh-room-latch-stabilizes-doorway-and-resets-per-floor");
        return 1;
    }
}
