using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Dalamud.Actions;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.OmenService;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal sealed partial class CombatAssistPolicy
{
    private readonly HohStoneskinInterrupt _hohInterrupt = new();
    private ulong _hohOwnedMarker;
    private DateTime _nextHohMarker;

    private unsafe void UpdateHohStoneskinControl(FsdSettings configuration, RunContext? context)
    {
        var player = Service.LocalPlayer;
        var target = Service.TargetManager.Target as IBattleNpc;
        if (!configuration.NecromancerBandedAutoSelect || context == null || context.NormalCombatOutputSuppressed ||
            !HohStoneskinPriority.Enabled(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor) ||
            player is not { IsDead: false } || !Service.Condition[ConditionFlag.InCombat] ||
            Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51] ||
            target is not { IsDead: false, IsTargetable: true, CurrentHp: > 0 } ||
            HohStoneskinTargetId == 0 || target.GameObjectId != HohStoneskinTargetId ||
            !HohStoneskinPriority.IsCaster(target.BaseId, target.NameId))
        { ResetHohStoneskinControl(); return; }

        var now = DateTime.UtcNow;
        if (now >= _nextHohMarker)
        {
            _nextHohMarker = now.AddSeconds(1);
            try
            {
                var marks = MarkingController.Instance();
                if (marks != null && (ulong)marks->Markers[0] != target.GameObjectId)
                {
                    ChatManager.Instance().SendMessage("/mk attack1 <t>");
                    _hohOwnedMarker = target.GameObjectId;
                    context.RecordDiagnostic?.Invoke("hoh-stoneskin-marker-requested", new {
                        targetId = target.GameObjectId, target.BaseId, target.NameId, marker = "attack1" });
                }
            }
            catch (Exception error) { Service.Log.Warning($"[HOH.Stoneskin] Marker: {error.Message}"); }
        }
        uint action = HohStoneskinInterrupt.ActionFor(player.ClassJob.RowId, player.Level);
        _hohInterrupt.ObserveCast(target.GameObjectId, target.IsCasting, target.CurrentCastTime);
        if (action == 0 || !target.IsCasting || target.CastActionId != HohStoneskinPriority.StoneskinActionId ||
            target.Address == 0 || player.IsCasting) return;
        var cast = ((Character*)target.Address)->GetCastInfo();
        if (cast == null || !_hohInterrupt.CanAttempt(target.GameObjectId, cast->SourceSequence,
                target.IsCastInterruptible, target.TotalCastTime - target.CurrentCastTime, now)) return;
        var manager = ActionManager.Instance();
        if (manager == null || manager->AnimationLock > .05f ||
            !manager->IsActionOffCooldown(ActionType.Action, action) ||
            manager->GetActionStatus(ActionType.Action, action, target.GameObjectId) != Service.ActionStatus_Ready) return;
        // Role actions use Range=-1 in Excel. The native range resolves their real effective range.
        float range = ActionManager.GetActionRange(action) + player.HitboxRadius + target.HitboxRadius;
        if (!float.IsFinite(range) || range <= 0 ||
            System.Numerics.Vector3.DistanceSquared(player.Position, target.Position) > range * range) return;
        var attempt = FsdActionExecutor.CastWithDiagnostics(action, target.GameObjectId);
        _hohInterrupt.Record(target.GameObjectId, cast->SourceSequence, attempt.Accepted, now);
        context.RecordDiagnostic?.Invoke("hoh-stoneskin-interrupt-request", new {
            targetId = target.GameObjectId, target.BaseId, target.NameId, action, castSequence = cast->SourceSequence,
            remaining = target.TotalCastTime - target.CurrentCastTime, attempt,
            result = attempt.Accepted ? "request-accepted-resolution-unconfirmed" : "request-rejected" });
    }

    private unsafe void ResetHohStoneskinControl()
    {
        if (_hohOwnedMarker != 0)
        {
            try
            {
                var marks = MarkingController.Instance();
                if (marks != null && (ulong)marks->Markers[0] == _hohOwnedMarker)
                    ChatManager.Instance().SendMessage("/mk off <attack1>");
            }
            catch { }
        }
        _hohOwnedMarker = 0; _nextHohMarker = default; _hohInterrupt.Reset();
    }
}
