using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Combat;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;

namespace DeepDungeon.Fsd.Dalamud.Runtime;

public sealed partial class RunContext
{
    internal readonly HohCombatTargetState HohCombatTargets = new();
    internal readonly HohApproachProgressState HohApproachProgress = new();
    private DateTime _nextHohSelectionDiagnostic;
    private DateTime _nextHohCentaurPullDiagnostic;
    internal bool HohCentaurRetreatActive;
    internal bool HohCentaurFightActive => Hoh70FloorPolicy.Enabled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor) &&
        (Service.GameObjects?.OfType<IBattleChara>() ?? Enumerable.Empty<IBattleChara>()).Any(e => IsHohCombatant(e) && e.IsTargetable && !e.IsDead && e.CurrentHp > 0 &&
            Hoh70FloorPolicy.IsCentaur(e.BaseId, e.NameId) && IsEngagedCombatTarget(e) && !IsHohPetrificationClearTarget(e));
    internal bool HohTargetingEnabled => HohStoneskinPriority.Enabled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor);
    // Rotation positioning must not depend on a different job's configured optional opener.
    internal float CombatEngageRange => HohTargetingEnabled && Service.LocalPlayer is { } player && player.ClassJob.RowId != 0
        ? player.ClassJob.RowId is 5 or 6 or 7 or 23 or 24 or 25 or 26 or 27 or 28 or 31 or 33 or 35 or 38 or 40 or 42 ? 25f : 3f
        : CombatAssist.GetCachedEngageRange(Configuration);
    internal void SyncHohCombatScope()
    {
        HohCombatTargets.BeginFloor(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor);
        HohApproachProgress.BeginFloor(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor);
        HohShieldBreak.BeginFloor(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor, Configuration.Rotation.Provider);
    }

    internal unsafe int? HohPassageProgress
    {
        get
        {
            if (Duty.DungeonId != 2 || Duty.IsTransitioning || Duty.StateReadFailed) return null;
            var dd=(FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon*)CurrentDeepDungeon;
            return dd!=null && dd->DeepDungeonId==Duty.DungeonId && dd->Floor==Duty.Floor
                ? dd->PassageProgress : null;
        }
    }

    private static bool IsHohCombatant(IBattleChara e) =>
        e.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
        (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)e.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant;

    internal void ObserveHohCombatTargets()
    {
        SyncHohCombatScope();
        if (!HohTargetingEnabled || Service.LocalPlayer is not { } player) return;
        var enemies = Service.GameObjects.OfType<IBattleChara>().Where(IsHohCombatant).ToArray();
        HohCombatTargets.Observe(enemies.Select(e => new HohCombatCandidate(e.GameObjectId,
            !e.IsDead && e.CurrentHp > 0, e.IsTargetable, IsNativeEngagedCombatTarget(e), true, false, 0,
            e.CurrentHp, Vector3.DistanceSquared(player.Position, e.Position))),
            Service.Condition[ConditionFlag.InCombat], DateTime.UtcNow);
    }

    internal IBattleChara? ResolveHohCombatTarget(ulong preferred, bool allowNewTargets)
    {
        ObserveHohCombatTargets();
        if (!HohTargetingEnabled || Service.LocalPlayer is not { } player) return null;
        var enemies = Service.GameObjects.OfType<IBattleChara>().Where(IsHohCombatant).ToArray();
        if (HohPetrification.Clearing)
            return SelectHohPetrifiedTarget(enemies.Where(e => e.IsTargetable && !e.IsDead && e.CurrentHp > 0 && IsHohPetrified(e)), player.Position);
        var candidates = enemies.Select(e => new HohCombatCandidate(e.GameObjectId,
            !e.IsDead && e.CurrentHp > 0, e.IsTargetable, IsEngagedCombatTarget(e),
            !IsCombatTargetSuppressed(e.GameObjectId) && IsHohPetrificationTargetAllowed(e) &&
                (!WisdomConfirmed || IsEngagedCombatTarget(e)),
            IsNewCombatTargetAllowed(e),
            Hoh70FloorPolicy.Priority(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor, e.BaseId, e.NameId,
                HohStoneskinPriority.IsCaster(e.BaseId, e.NameId)
                    ? e.IsCasting && e.CastActionId == HohStoneskinPriority.StoneskinActionId ? 2 : 1 : 0),
            e.CurrentHp, Vector3.DistanceSquared(player.Position, e.Position))).ToArray();
        ulong chosen = HohCombatTargets.Select(candidates, preferred, allowNewTargets && !PassageOpenForCombat,
            MobCombat.OutputTarget?.GameObjectId ?? 0);
        return enemies.FirstOrDefault(e => e.GameObjectId == chosen && chosen != 0);
    }

    internal bool EnsureHohCombatTarget(IBattleChara desired)
    {
        if (!HohTargetingEnabled) return true;
        if (!IsHohVoluntaryTargetAllowed(desired) ||
            !IsEngagedCombatTarget(desired) && IsCombatTargetSuppressed(desired.GameObjectId))
        {
            if (Service.TargetManager.Target?.GameObjectId == desired.GameObjectId) Service.TargetManager.Target = null;
            if (Service.TargetManager.SoftTarget?.GameObjectId == desired.GameObjectId) Service.TargetManager.SoftTarget = null;
            return false;
        }
        SyncHohCombatScope();
        var now = DateTime.UtcNow;
        ulong before = Service.TargetManager.Target?.GameObjectId ?? 0;
        string? error = null;
        try
        {
            if (Service.TargetManager.SoftTarget is { } soft && soft.GameObjectId != desired.GameObjectId)
                Service.TargetManager.SoftTarget = null;
            if (before != desired.GameObjectId) Service.TargetManager.Target = desired;
        }
        catch (Exception e) { error = e.Message; }
        ulong hard = Service.TargetManager.Target?.GameObjectId ?? 0;
        ulong softId = Service.TargetManager.SoftTarget?.GameObjectId ?? 0;
        bool ready = HohCombatTargets.ObserveSelection(now, desired.GameObjectId, hard, softId);
        if (ready) PrepareCombat(desired.GameObjectId);
        if (now >= _nextHohSelectionDiagnostic && (!ready || before != hard || error != null))
        {
            _nextHohSelectionDiagnostic = now.AddSeconds(1);
            RecordDiagnostic?.Invoke("hoh-target-selection", new { desiredId = desired.GameObjectId,
                previousId = before, hardTargetId = hard, softTargetId = softId, ready, error,
                timedOut = HohCombatTargets.SelectionTimedOut });
        }
        if (HohCombatTargets.SelectionTimedOut)
            StopExplorationRecoverably("指定目标连续6秒未能实际选中；暂停探索，保留旧接战目标");
        return ready;
    }

    internal void ClearHohPetrifiedOutputTarget()
    {
        if (Service.TargetManager.Target != null) Service.TargetManager.Target = null;
        if (Service.TargetManager.SoftTarget != null) Service.TargetManager.SoftTarget = null;
    }

    internal bool PrepareHohPetrifiedOutput(IBattleChara? target, Vector3 player)
    {
        // Selection/output is independent of arrival: keep attacking while navigation closes in.
        if (target == null)
        { ClearHohPetrifiedOutputTarget(); return false; }
        if (HohTargetingEnabled) return EnsureHohCombatTarget(target);
        Service.TargetManager.Target = target; PrepareCombat(target.GameObjectId); return true;
    }

    private bool IsNativeEngagedCombatTarget(IBattleChara target) =>
        target.TargetObjectId == Service.LocalPlayer?.GameObjectId || Floor.EnemyChaseHelper.IsAggroedToPlayer(target.GameObjectId);

    internal HohCentaurObstacle[] HohLiveCentaurObstacles() => Service.GameObjects.OfType<IBattleChara>()
        .Where(e => IsHohCombatant(e) && e.IsTargetable && !e.IsDead && e.CurrentHp > 0 &&
            Hoh70FloorPolicy.IsCentaur(e.BaseId, e.NameId) && !IsHohPetrificationClearTarget(e))
        .Select(e => new HohCentaurObstacle(e.GameObjectId, e.Position, e.HitboxRadius)).ToArray();

    internal bool IsHohCentaurPullSafe(IBattleChara target)
    {
        if (!HohHighFloorPolicy.Enabled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor) || IsEngagedCombatTarget(target)) return true;
        if (Service.LocalPlayer is not { } player) return false;
        bool safe = HohCentaurPullPolicy.AllowsPull(player.Position, player.HitboxRadius, target.Position,
            target.HitboxRadius, HohLiveCentaurObstacles(), out var blocker, out var reason);
        if (!safe && DateTime.UtcNow >= _nextHohCentaurPullDiagnostic)
        {
            _nextHohCentaurPullDiagnostic = DateTime.UtcNow.AddSeconds(2);
            RecordDiagnostic?.Invoke("hoh-centaur-pull-blocked", new { floor = Duty.Floor,
                targetId = target.GameObjectId, centaurId = blocker, reason,
                player = new { player.Position.X, player.Position.Y, player.Position.Z },
                target = new { target.Position.X, target.Position.Y, target.Position.Z }, transitAllowed = true });
        }
        return safe;
    }

    internal void ClearUnsafeHohPullTargets()
    {
        if (!Hoh70FloorPolicy.Enabled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor)) return;
        if (Service.TargetManager.Target is IBattleChara hard && IsHohCombatant(hard) && !IsHohVoluntaryTargetAllowed(hard))
            Service.TargetManager.Target = null;
        if (Service.TargetManager.SoftTarget is IBattleChara soft && IsHohCombatant(soft) && !IsHohVoluntaryTargetAllowed(soft))
            Service.TargetManager.SoftTarget = null;
    }

    private unsafe bool IsHohVoluntaryTargetAllowed(IBattleChara target)
    {
        if (!Hoh70FloorPolicy.Enabled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor) || IsEngagedCombatTarget(target)) return true;
        int room = -1, targetRoom = -1;
        if (Hoh70FloorPolicy.Enabled(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor))
        {
            var dd = (FFXIVClientStructs.FFXIV.Client.Game.InstanceContent.InstanceContentDeepDungeon*)_currentDeepDungeon();
            if (dd != null)
            {
                room = ResolveHohCombatRoom(dd);
                var reachable = GameState.RoomGraph.BuildReachableRoomOrder(dd, room);
                targetRoom = GameState.RoomGraph.GetRoomIndexForPosition(dd, target.Position, reachable, -1);
            }
        }
        SyncHohCombatScope();
        if (HohApproachProgress.RequiresPatrol(room) ||
            !HohApproachPathPolicy.AllowsTargetRoom(true, false, room, targetRoom)) return false;
        // Confirmed petrification clearing owns targeting before ordinary centaur exclusions.
        // Actual target status is required; requesting Stone Curse alone cannot permit a pull.
        if (IsHohPetrificationClearTarget(target)) return true;
        return IsHohCentaurPullSafe(target) &&
            Hoh70FloorPolicy.AllowsVoluntaryTarget(FarmingPlan?.Mode, Duty.DungeonId, Duty.Floor,
            target.BaseId, target.NameId, false, room, targetRoom);
    }
}
