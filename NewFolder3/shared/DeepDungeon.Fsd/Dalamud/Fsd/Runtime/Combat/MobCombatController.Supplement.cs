using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Event;
using OmenTools.Interop.Game.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal sealed partial class MobCombatController
{
    private DateTime _nextSupplementAt;
    private ulong _pendingSupplement;
    private DateTime _supplementDeadline;

    internal unsafe bool IsLocalSupplementCandidate(RunContext context, IBattleChara candidate)
    {
        if (!MobCombatPolicy.HighFloorEnabled(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor) ||
            context.PassageOpenForCombat || Engaged.Count == 0 || candidate.IsDead || !candidate.IsTargetable ||
            context.IsEngagedCombatTarget(candidate) || context.IsCombatTargetSuppressed(candidate.GameObjectId) ||
            Service.LocalPlayer is not { } player) return false;
        var framework = EventFramework.Instance();
        var dd = framework == null ? null : framework->GetInstanceContentDeepDungeon();
        if (dd == null || dd->Floor != context.Duty.Floor) return false;
        int room = RoomGraph.GetLocalPlayerRoomIndex(dd);
        if (room < 0) return false;
        var reachable = RoomGraph.BuildReachableRoomOrder(dd, room);
        bool sameRoom = RoomGraph.GetRoomIndexForPosition(dd, candidate.Position, reachable, -1) == room &&
            Engaged.All(e => RoomGraph.GetRoomIndexForPosition(dd, e.Position, reachable, -1) == room);
        float distance = Vector2.Distance(new(player.Position.X, player.Position.Z), new(candidate.Position.X, candidate.Position.Z));
        if (!PassageCombatPolicy.CanSupplement(Engaged.Count, context.Configuration.Combat.HighFloorEnemyLimit,
                sameRoom, distance, context.CombatAssist.GetCachedEngageRange(context.Configuration), DangerousEngaged, PriorityFight,
                MobPullPolicy.RequiresSolo(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor, candidate.NameId))) return false;
        float y = Math.Max(player.Position.Y, candidate.Position.Y) + 1;
        return RaycastHelper.HasLineOfSight(new(player.Position.X, y, player.Position.Z), new(candidate.Position.X, y, candidate.Position.Z));
    }

    internal void TrySupplementLocalGroup(RunContext context)
    {
        var config = context.Configuration;
        if (_pendingSupplement != 0)
        {
            if (Engaged.Any(e => e.GameObjectId == _pendingSupplement) || DateTime.UtcNow >= _supplementDeadline)
                _pendingSupplement = 0;
            else return; // One in-flight pull occupies a slot before the hate-list update.
        }
        if (!config.NecromancerBandedAutoAttract || config.NecromancerBandedAttractSkillId == 0 ||
            DateTime.UtcNow < _nextSupplementAt || context.NormalCombatOutputSuppressed || context.Duty.IsTransitioning ||
            context.DutyFailureObserved || context.AttemptAborted || context.StatusIsError ||
            Service.LocalPlayer is not { IsCasting: false, IsDead: false } player ||
            Service.Condition[ConditionFlag.Casting] || Service.Condition[ConditionFlag.OccupiedInEvent] ||
            Service.Condition[ConditionFlag.OccupiedInQuestEvent]) return;
        var candidate = Unengaged.Where(e => IsLocalSupplementCandidate(context, e))
            .OrderBy(e => Vector3.DistanceSquared(player.Position, e.Position)).FirstOrDefault();
        if (candidate == null) return; // Continue current output immediately; never wait for a quota.
        _nextSupplementAt = DateTime.UtcNow.AddSeconds(2);
        // Cast by explicit ID, keeping the current output/chase target and all movement intact.
        var result = DeepDungeon.Fsd.Dalamud.Actions.FsdActionExecutor.CastWithDiagnostics(
            config.NecromancerBandedAttractSkillId, candidate.GameObjectId);
        if (result.Accepted) { _pendingSupplement = candidate.GameObjectId; _supplementDeadline = DateTime.UtcNow.AddSeconds(6); }
        context.RecordDiagnostic?.Invoke("high-floor-local-supplement", new { floor = context.Duty.Floor,
            candidate.GameObjectId, name = candidate.Name.TextValue, engaged = Engaged.Count,
            limit = config.Combat.HighFloorEnemyLimit, result, rule = "same-room-in-range-no-movement-no-quota" });
    }
}
