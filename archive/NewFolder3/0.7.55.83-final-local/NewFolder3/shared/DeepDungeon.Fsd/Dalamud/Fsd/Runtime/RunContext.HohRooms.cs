using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

public sealed partial class RunContext
{
    private readonly Func<nint> _currentDeepDungeon;
    internal nint CurrentDeepDungeon => _currentDeepDungeon();
    private readonly HohCombatRoomLatch _hohCombatRoomLatch = new();
    private DateTime _nextHohRoomDiagnostic;

    private static unsafe nint ReadCurrentDeepDungeon()
    {
        var framework = FFXIVClientStructs.FFXIV.Client.Game.Event.EventFramework.Instance();
        return framework == null ? 0 : (nint)framework->GetInstanceContentDeepDungeon();
    }

    // All ordinary HoH pulls use the same world-center map. Do not compare their
    // mapped room against the party's delayed virtual room at an entrance.
    internal unsafe int ResolveHohCombatRoom(InstanceContentDeepDungeon* dd,
        IReadOnlyList<int>? reachable = null)
    {
        int native = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (dd == null || !Hoh70FloorPolicy.Enabled(FarmingPlan?.Mode, dd->DeepDungeonId, dd->Floor) ||
            Service.LocalPlayer is not { } player || !CombatPositionPolicy.IsFinite(player.Position)) return native;
        reachable ??= RoomGraph.BuildReachableRoomOrder(dd, native);
        int candidate = RoomGraph.GetRoomIndexForPosition(dd, player.Position, reachable, native);
        int mapped = candidate;
        if (candidate >= 0 && MapPos.TryGetRoomCenter(dd, candidate, out var candidateCenter))
        {
            float candidateDistance = Vector2.DistanceSquared(new(player.Position.X, player.Position.Z),
                new(candidateCenter.X, candidateCenter.Z));
            float currentDistance = float.PositiveInfinity;
            if (_hohCombatRoomLatch.Room >= 0 && reachable.Contains(_hohCombatRoomLatch.Room) &&
                MapPos.TryGetRoomCenter(dd, _hohCombatRoomLatch.Room, out var currentCenter))
                currentDistance = Vector2.DistanceSquared(new(player.Position.X, player.Position.Z),
                    new(currentCenter.X, currentCenter.Z));
            mapped = _hohCombatRoomLatch.Resolve(dd->DeepDungeonId, dd->Floor, candidate, candidateDistance, currentDistance);
        }
        if (mapped != native && DateTime.UtcNow >= _nextHohRoomDiagnostic)
        {
            _nextHohRoomDiagnostic = DateTime.UtcNow.AddSeconds(5);
            RecordDiagnostic?.Invoke("hoh-combat-room-world-mapping", new { floor = dd->Floor,
                nativeRoom = native, combatRoom = mapped,
                player = new { player.Position.X, player.Position.Y, player.Position.Z } });
        }
        return mapped;
    }
}
