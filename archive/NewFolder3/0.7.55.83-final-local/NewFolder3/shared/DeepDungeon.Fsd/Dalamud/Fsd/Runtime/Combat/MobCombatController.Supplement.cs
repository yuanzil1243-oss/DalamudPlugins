using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.Actions;
using OmenTools.Interop.Game.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal sealed partial class MobCombatController
{
    private DateTime _nextSupplementAt;
    private DateTime _nextSupplementDiagnosticAt;
    private ulong _pendingSupplement;
    private DateTime _supplementDeadline;

    internal unsafe bool IsLocalSupplementCandidate(RunContext context, IBattleChara candidate)
    {
        if (!MobCombatPolicy.UsesControlledHighFloor(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor) ||
            context.PassageOpenForCombat || Engaged.Count == 0 || candidate.IsDead || !candidate.IsTargetable ||
            context.IsEngagedCombatTarget(candidate) || context.IsCombatTargetSuppressed(candidate.GameObjectId) ||
            Service.LocalPlayer is not { } player) return false;
        if (!context.IsHohCentaurPullSafe(candidate)) return false;
        var dd = (FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon*)context.CurrentDeepDungeon;
        if (dd == null || dd->Floor != context.Duty.Floor) return false;
        int room = context.ResolveHohCombatRoom(dd);
        if (room < 0) return false;
        bool hoh = HohHighFloorPolicy.ControlsCombat(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor);
        bool earlier = HohHighFloorPolicy.EarlierGroup(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor);
        if (!Hoh70FloorPolicy.AllowsVoluntaryTarget(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor,
                candidate.BaseId, candidate.NameId, false, room, room)) return false;
        if (hoh && (!earlier && HohCombatRoom != room || context.ControlledCombatEngagedCount != Engaged.Count ||
            context.HohPetrification.Clearing || context.HohPetrification.StopAttackingAfterClear)) return false;
        var reachable = RoomGraph.BuildReachableRoomOrder(dd, room);
        bool sameRoom = RoomGraph.GetRoomIndexForPosition(dd, candidate.Position, reachable, -1) == room &&
            (earlier || Engaged.All(e => RoomGraph.GetRoomIndexForPosition(dd, e.Position, reachable, -1) == room));
        float distance = Vector2.Distance(new(player.Position.X, player.Position.Z), new(candidate.Position.X, candidate.Position.Z));
        bool priorityFight = PriorityFight || hoh && Engaged.Any(e => HohStoneskinPriority.IsCaster(e.BaseId, e.NameId));
        int limit = HohHighFloorPolicy.SupplementLimit(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor,
            context.Configuration.Combat.GetHighFloorEnemyLimit(context.Duty.DungeonId));
        // HoH candidates need not already be shootable. The shared output/pursuit pipeline
        // validates a full same-room ground path to the first safe firing position.
        if (!HohHighFloorPolicy.CanSupplement(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor, Engaged.Count, limit,
                sameRoom, distance, hoh ? 60f : context.CombatEngageRange, DangerousEngaged, priorityFight,
                MobPullPolicy.RequiresSolo(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor, candidate.NameId))) return false;
        if (hoh) return true;
        float y = Math.Max(player.Position.Y, candidate.Position.Y) + 1;
        return RaycastHelper.HasLineOfSight(new(player.Position.X, y, player.Position.Z), new(candidate.Position.X, y, candidate.Position.Z)) &&
            (!hoh || CombatPositionNavigation.Sight(player.Position, candidate.Position, out _) == CombatSight.Clear &&
                CombatPositionNavigation.Sight(player.Position - new Vector3(0, 7, 0), candidate.Position, out _) == CombatSight.Clear);
    }

    internal void TrySupplementLocalGroup(RunContext context)
    {
        // HoH supplements exclusively through selection + safe navigation + rotation.
        // The independent optional auto-attract setting must never enter this direct-cast path.
        if (HohHighFloorPolicy.ControlsCombat(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor)) return;
        var config = context.Configuration;
        if (_pendingSupplement != 0)
        {
            if (Engaged.Any(e => e.GameObjectId == _pendingSupplement) || DateTime.UtcNow >= _supplementDeadline)
                _pendingSupplement = 0;
            else return; // One in-flight pull occupies a slot before the hate-list update.
        }
        if (!HohHighFloorPolicy.AllowsSupplementExecutor(context.FarmingPlan?.Mode, context.Duty.DungeonId,
                context.Duty.Floor, config.NecromancerBandedAutoAttract) || config.NecromancerBandedAttractSkillId == 0 ||
            DateTime.UtcNow < _nextSupplementAt || context.NormalCombatOutputSuppressed || context.Duty.IsTransitioning ||
            context.DutyFailureObserved || context.AttemptAborted || context.StatusIsError ||
            Service.LocalPlayer is not { IsCasting: false, IsDead: false } player ||
            Service.Condition[ConditionFlag.Casting] || Service.Condition[ConditionFlag.OccupiedInEvent] ||
            Service.Condition[ConditionFlag.OccupiedInQuestEvent]) return;
        var candidate = Unengaged.Where(e => IsLocalSupplementCandidate(context, e))
            .OrderBy(e => Vector3.DistanceSquared(player.Position, e.Position)).FirstOrDefault();
        if (candidate == null) return; // Continue current output immediately; never wait for a quota.
        var result = FsdActionExecutor.CastWithDiagnostics(config.NecromancerBandedAttractSkillId, candidate.GameObjectId);
        bool activeGroup = HohHighFloorPolicy.ActiveGroup(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor);
        // A failed animation-lock check is not a pull. Retry promptly instead of phase-locking to a two-second poll.
        _nextSupplementAt = DateTime.UtcNow.AddSeconds(activeGroup ? result.Accepted ? 1 : .15 : 2);
        if (result.Accepted) { _pendingSupplement = candidate.GameObjectId; _supplementDeadline = DateTime.UtcNow.AddSeconds(6); }
        if (!result.Accepted && DateTime.UtcNow < _nextSupplementDiagnosticAt) return;
        _nextSupplementDiagnosticAt = DateTime.UtcNow.AddSeconds(2);
        context.RecordDiagnostic?.Invoke("high-floor-local-supplement", new { floor = context.Duty.Floor,
            candidate.GameObjectId, name = candidate.Name.TextValue, engaged = Engaged.Count,
            configuredAction = config.NecromancerBandedAttractSkillId, job = player.ClassJob.RowId,
            limit = HohHighFloorPolicy.SupplementLimit(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor,
                config.Combat.GetHighFloorEnemyLimit(context.Duty.DungeonId)), result,
            rule = activeGroup ? "active-group-full-action-range-six-ceiling" : "same-room-in-range-no-movement-no-quota" });
    }

}
