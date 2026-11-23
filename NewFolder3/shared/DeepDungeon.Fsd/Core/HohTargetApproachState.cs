using System.Numerics;

namespace DeepDungeon.Fsd.Core;

public enum HohTargetApproachStage { None, NearSide, OppositeSide, Finished }

/// <summary>Target pursuit may cross connected rooms; a local firing-position repair may not.</summary>
public static class HohTargetApproachPolicy
{
    public const float MaximumPathLength = 160;
    public static bool CanHoldOutput(bool inRange, bool nativeSightClear, bool offsetSightClear) =>
        inRange && nativeSightClear && offsetSightClear;
    public static bool NeedsApproach(bool canOutput, bool engaged, bool passageOpen, bool targetAllowed) =>
        !canOutput && (engaged || !passageOpen && targetAllowed);
    public static bool AllowsRoom(int hops, bool restrictToCombatRoom, int candidateRoom, int combatRoom) =>
        hops >= 0 && candidateRoom >= 0 && (!restrictToCombatRoom || candidateRoom == combatRoom);
    public static bool AllowsUndirectedFallback(bool engaged, bool petrified) => engaged || petrified;
}

/// <summary>One target, two directed ground legs before the existing local recovery fallback.</summary>
public sealed class HohTargetApproachState
{
    private Vector2 _nearDirection;
    public HohTargetApproachStage Stage { get; private set; }
    public bool Active => Stage is HohTargetApproachStage.NearSide or HohTargetApproachStage.OppositeSide;
    public const int PathQueryLimit = 8;
    public static readonly TimeSpan OutputWindow = TimeSpan.FromSeconds(6);

    public void Begin(Vector3 player, Vector3 target)
    {
        Reset();
        if (!CombatPositionPolicy.IsFinite(player) || !CombatPositionPolicy.IsFinite(target)) return;
        var delta = new Vector2(player.X - target.X, player.Z - target.Z);
        _nearDirection = delta.LengthSquared() > .01f ? Vector2.Normalize(delta) : Vector2.UnitX;
        Stage = HohTargetApproachStage.NearSide;
    }

    public bool Advance()
    {
        if (!Active) return false;
        Stage = Stage == HohTargetApproachStage.NearSide ? HohTargetApproachStage.OppositeSide : HohTargetApproachStage.Finished;
        return true;
    }

    public IEnumerable<Vector3> Seeds(Vector3 player, Vector3 target)
    {
        if (!Active || !CombatPositionPolicy.IsFinite(player) || !CombatPositionPolicy.IsFinite(target)) yield break;
        var side = Stage == HohTargetApproachStage.NearSide ? _nearDirection : -_nearDirection;
        var tangent = new Vector2(-side.Y, side.X);
        // The side remains relative to the initial player/target direction after the first leg arrives.
        foreach (float y in Math.Abs(player.Y - target.Y) > .01f ? new[] { target.Y, player.Y } : new[] { target.Y })
            foreach (float radius in new[] { 4f, 6f })
                foreach (float angle in new[] { 0f, MathF.PI / 4, -MathF.PI / 4 })
                {
                    var offset = radius * (side * MathF.Cos(angle) + tangent * MathF.Sin(angle));
                    yield return new(target.X + offset.X, y, target.Z + offset.Y);
                }
    }

    public void Reset() { Stage = HohTargetApproachStage.None; _nearDirection = default; }
}
