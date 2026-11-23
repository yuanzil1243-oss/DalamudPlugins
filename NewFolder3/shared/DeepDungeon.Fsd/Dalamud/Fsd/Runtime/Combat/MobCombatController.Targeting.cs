using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Types;
using OmenTools.Interop.Game.Helpers;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal sealed partial class MobCombatController
{
    private readonly MobTargetSwitchState _outputSelection = new();
    private readonly Func<Vector3, Vector3, bool> _outputSight = RaycastHelper.HasLineOfSight;
    private readonly HohRotationPullState _rotationPull = new();
    private int? _lastPassageProgress;
    private int _lastPassageLimit=-1;
    internal bool RotationPulling => _rotationPull.Pending != 0;
    public IBattleChara? OutputTarget { get; private set; }
    public bool OutputTargetHittable { get; private set; }
    private void UpdateOutputTarget(RunContext context)
    {
        if (context.NormalCombatOutputSuppressed ||
            !Service.Condition[ConditionFlag.InCombat] && !(context.HohTargetingEnabled && context.HohCombatTargets.HasEngagement) ||
            Service.LocalPlayer is not { } player)
        { OutputTarget = null; OutputTargetHittable = false; _outputSelection.Reset(); _rotationPull.Reset(); return; }
        float range = context.CombatEngageRange;
        var pool = Engaged.Concat(Unengaged.Where(context.IsNewCombatTargetAllowed))
            .Where(e => !context.IsCombatTargetSuppressed(e.GameObjectId)).ToArray();
        var engagedIds = Engaged.Select(e => e.GameObjectId).ToHashSet();
        var candidates = pool.Select(e =>
        {
            float distance = Math.Max(0, Vector2.Distance(new(player.Position.X, player.Position.Z), new(e.Position.X, e.Position.Z)) - e.HitboxRadius - player.HitboxRadius);
            bool visible = false;
            if (distance <= range && range > 0)
            {
                // HoH output and approach use the same two supported height probes.
                float y = Math.Max(player.Position.Y, e.Position.Y) + 1;
                visible = context.HohTargetingEnabled
                    ? _outputSight(player.Position, e.Position) && _outputSight(player.Position - new Vector3(0, 7, 0), e.Position)
                    : _outputSight(new(player.Position.X, y, player.Position.Z), new(e.Position.X, y, e.Position.Z));
            }
            return new OutputTargetCandidate(e.GameObjectId, e.CurrentHp, distance,
                TargetPriority(e), engagedIds.Contains(e.GameObjectId), visible);
        }).ToArray();
        bool allowNew = !context.PassageOpenForCombat && !Consolidating && !PriorityFight &&
            (!MobCombatPolicy.UsesControlledHighFloor(_mode, _dungeon, _floor) || context.ControlledCombatEngagedCount == 0) &&
            !Service.Condition[ConditionFlag.Casting] && !Service.Condition[ConditionFlag.OccupiedInEvent] &&
            !Service.Condition[ConditionFlag.OccupiedInQuestEvent] && !player.IsCasting &&
            !context.Duty.IsTransitioning && !context.DutyFailureObserved && !context.AttemptAborted;
        ulong previous = OutputTarget?.GameObjectId ?? 0;
        var now = DateTime.UtcNow;
        bool activeGather = HohHighFloorPolicy.ControlsCombat(_mode, _dungeon, _floor);
        int? passageProgress=activeGather ? context.HohPassageProgress : null;
        int passageLimit=activeGather ? HohPassagePullPolicy.MaximumEngaged(passageProgress) : 6;
        if (activeGather && (_lastPassageProgress!=passageProgress || _lastPassageLimit!=passageLimit))
        {
            _lastPassageProgress=passageProgress; _lastPassageLimit=passageLimit;
            context.RecordDiagnostic?.Invoke("hoh-passage-pull-budget",new { floor=_floor,
                progress=passageProgress, openProgress=HohPassagePullPolicy.OpenProgress,
                maximumEngaged=passageLimit, engaged=Engaged.Count,
                reason=passageProgress==null?"progress-unavailable-no-extra-pull":passageLimit<=1?
                    "near-passage-finish-held-combat":passageLimit<6?"passage-approaching-reduce-pulls":"normal-gather" });
        }
        bool safeGather = activeGather && !context.PassageOpenForCombat && !PriorityFight && !DangerousEngaged &&
            !context.HohCentaurFightActive && !context.WisdomConfirmed && !context.HohPetrification.Clearing &&
            !context.HohPetrification.StopAttackingAfterClear && Engaged.Count > 0 &&
            context.ControlledCombatEngagedCount == Engaged.Count && Engaged.Count < passageLimit &&
            !context.Duty.IsTransitioning && !context.DutyFailureObserved && !context.AttemptAborted && !context.StatusIsError;
        bool canStart = !Consolidating && !player.IsCasting && !Service.Condition[ConditionFlag.Casting] &&
            !Service.Condition[ConditionFlag.OccupiedInEvent] && !Service.Condition[ConditionFlag.OccupiedInQuestEvent];
        ulong pendingBefore = _rotationPull.Pending;
        string reasonBefore = _rotationPull.Reason;
        ulong hardTarget = Service.TargetManager.Target?.GameObjectId ?? 0;
        ulong id = _rotationPull.Select(candidates, safeGather, canStart, hardTarget, now);
        if (activeGather && (pendingBefore != _rotationPull.Pending || reasonBefore != _rotationPull.Reason))
            context.RecordDiagnostic?.Invoke("hoh-rotation-pull-state", new { floor = _floor,
                previous = pendingBefore, pending = _rotationPull.Pending, reason = _rotationPull.Reason,
                selected = hardTarget, engaged = Engaged.Count, passageProgress, passageLimit, directActionDispatched = false });
        if (_rotationPull.DeferredTarget != 0)
            context.RecordDiagnostic?.Invoke("hoh-rotation-pull-deferred", new { floor = _floor,
                targetId = _rotationPull.DeferredTarget, reason = _rotationPull.Reason, directActionDispatched = false });
        if (id == 0) id = _outputSelection.Select(candidates, allowNew, now);
        OutputTarget = pool.FirstOrDefault(e => e.GameObjectId == id);
        OutputTargetHittable = candidates.Any(c => c.Id == id && c.CanHit);
        if (id != previous)
            context.RecordDiagnostic?.Invoke("mob-output-target-changed", new
            {
                floor = _floor, previous, targetId = id, name = OutputTarget?.Name.TextValue,
                reason = RotationPulling ? _rotationPull.Reason : _outputSelection.Reason, hittable = OutputTargetHittable, allowNew,
                rotationPull = RotationPulling, navigationRequired = RotationPulling && !OutputTargetHittable,
                engaged = Engaged.Count, passageOpen = context.Duty.PassageOpen
            });
    }
}
