using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class FloorExplorationController
{
    private readonly PassageStandstillState _passageStandstill = new();
    private bool _passageMovementOwned;
    private bool _passageHoldLogged;

    internal unsafe bool TryUpdatePassageStandstill(InstanceContentDeepDungeon* dd)
    {
        var player = Service.LocalPlayer;
        var evidence = ObjectEvidence.Current;
        bool eligible = !(PassageFirst && PendingExplorationWork) && !IsDisposed && dd != null && player is { IsDead: false } &&
            CurrentObjectiveDecision.PrimaryObjective == FloorObjectiveKind.EnterPassage &&
            Objectives.HasObjectiveDecision &&
            CurrentObjectiveDecision.Channels.Transition == CommandChannelPermission.PrimaryObjective &&
            CurrentObjectiveDecision.Channels.Movement != CommandChannelPermission.Blocked &&
            _ctx.Duty.PassageOpen && !Service.Condition[ConditionFlag.InCombat] &&
            !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51];
        if (!eligible || evidence?.Available != true ||
            !PassageLocator.TryResolvePassageDestination(dd, evidence, out var actor, out bool exact, out _) || !exact)
        { ReleasePassageStandstill(); return false; }
        var destination = ResolvePassageWalkingPosition(actor);
        var delta = player!.Position - destination;
        // Acquire shortly before reaching the circle so BMR cannot pull against VNav.
        if (delta.X * delta.X + delta.Z * delta.Z > 2.5f * 2.5f || Math.Abs(delta.Y) > 3)
        { ReleasePassageStandstill(); return false; }
        foreach (var obj in Service.GameObjects)
            if (obj is IBattleChara { IsCasting: true, IsDead: false } caster &&
                caster.GameObjectId != player.GameObjectId &&
                Vector3.DistanceSquared(caster.Position, player.Position) < 40 * 40)
            { ReleasePassageStandstill(); return false; }
        if (!_passageMovementOwned && _ctx.Configuration.BossMechanics.UsesBmrBossHandling())
        {
            if (_ctx.SetBossMovementOverride?.Invoke(true) != true) return false;
            _passageMovementOwned = true;
        }
        if (!_passageStandstill.Update(true, player.Position, destination, DateTime.UtcNow))
        {
            if (_passageHoldLogged)
            {
                _passageHoldLogged = false;
                RecordReplayEvent("passage-standstill-recenter", new { player.Position, destination });
            }
            return false; // Normal passage navigation can recenter; BMR remains suspended nearby.
        }
        RunTelemetry?.ObservePassageCommit(DateTime.UtcNow);
        _status = "At passage, standing still for transition";
        if (!_passageHoldLogged)
        {
            _navDriver.Cancel();
            _passageHoldLogged = true;
            RecordReplayEvent("passage-standstill", new { player.Position, destination, generation = Generation,
                bmrSuspended = _passageMovementOwned });
        }
        return true;
    }

    internal void ReleasePassageStandstill()
    {
        _passageStandstill.Reset();
        if (_passageMovementOwned)
        {
            _passageMovementOwned = false;
            _ctx.SetBossMovementOverride?.Invoke(false);
        }
        if (_passageHoldLogged)
        {
            _passageHoldLogged = false;
            RecordReplayEvent("passage-standstill-released", new { generation = Generation });
        }
    }
}
