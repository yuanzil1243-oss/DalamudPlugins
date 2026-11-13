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
    public IBattleChara? OutputTarget { get; private set; }
    public bool OutputTargetHittable { get; private set; }
    private void UpdateOutputTarget(RunContext context)
    {
        if (context.NormalCombatOutputSuppressed || !Service.Condition[ConditionFlag.InCombat] || Service.LocalPlayer is not { } player)
        { OutputTarget = null; OutputTargetHittable = false; _outputSelection.Reset(); return; }
        float range = context.CombatAssist.GetCachedEngageRange(context.Configuration);
        var pool = Engaged.Concat(Unengaged.Where(context.IsNewCombatTargetAllowed))
            .Where(e => !context.IsCombatTargetSuppressed(e.GameObjectId)).ToArray();
        var engagedIds = Engaged.Select(e => e.GameObjectId).ToHashSet();
        var candidates = pool.Select(e =>
        {
            float distance = Math.Max(0, Vector2.Distance(new(player.Position.X, player.Position.Z), new(e.Position.X, e.Position.Z)) - e.HitboxRadius - player.HitboxRadius);
            bool visible = false;
            if (distance <= range && range > 0)
            {
                // Use the enemy's floor height when burrow has moved the player below it.
                float y = Math.Max(player.Position.Y, e.Position.Y) + 1;
                visible = RaycastHelper.HasLineOfSight(new(player.Position.X, y, player.Position.Z), new(e.Position.X, y, e.Position.Z));
            }
            return new OutputTargetCandidate(e.GameObjectId, e.CurrentHp, distance,
                MobCombatPolicy.Priority(_dungeon, _floor, e.NameId), engagedIds.Contains(e.GameObjectId), visible);
        }).ToArray();
        bool allowNew = !context.PassageOpenForCombat && !Consolidating && !PriorityFight &&
            (!MobCombatPolicy.HighFloorEnabled(_mode, _dungeon, _floor) || Engaged.Count == 0) &&
            !Service.Condition[ConditionFlag.Casting] && !Service.Condition[ConditionFlag.OccupiedInEvent] &&
            !Service.Condition[ConditionFlag.OccupiedInQuestEvent] && !player.IsCasting &&
            !context.Duty.IsTransitioning && !context.DutyFailureObserved && !context.AttemptAborted;
        ulong previous = OutputTarget?.GameObjectId ?? 0;
        ulong id = _outputSelection.Select(candidates, allowNew, DateTime.UtcNow);
        OutputTarget = pool.FirstOrDefault(e => e.GameObjectId == id);
        OutputTargetHittable = candidates.Any(c => c.Id == id && c.CanHit);
        if (id != previous)
            context.RecordDiagnostic?.Invoke("mob-output-target-changed", new
            {
                floor = _floor, previous, targetId = id, name = OutputTarget?.Name.TextValue,
                reason = _outputSelection.Reason, hittable = OutputTargetHittable, allowNew,
                engaged = Engaged.Count, passageOpen = context.Duty.PassageOpen
            });
    }
}
