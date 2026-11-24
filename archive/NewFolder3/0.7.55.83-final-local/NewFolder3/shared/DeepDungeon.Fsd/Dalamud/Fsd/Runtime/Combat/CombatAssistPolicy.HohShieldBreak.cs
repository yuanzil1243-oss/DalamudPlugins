using System.Numerics;
using System.Text.Json;
using DeepDungeon.Fsd.Dalamud.Actions;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Runtime;
using global::Dalamud.Game.ClientState.Conditions;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal sealed partial class CombatAssistPolicy
{
    private ICallGateSubscriber<bool>? _shieldAeRunning, _shieldAePull;
    private ICallGateSubscriber<Vector3,Vector3,bool>? _shieldPointSafe;
    private DateTime _nextShieldTick, _nextShieldSafetyRead;
    private HashSet<uint>? _shieldOtherProtection;
    private bool _aeClassifiesStoneskin;
    private string? _shieldLastReason;
    private ulong _shieldLastTarget;

    private unsafe void UpdateHohShieldBreak(FsdSettings configuration, RunContext? context)
    {
        if (context==null) return;
        var now=DateTime.UtcNow;
        context.HohShieldBreak.BeginFloor(context.FarmingPlan?.Mode,context.Duty.DungeonId,context.Duty.Floor,
            configuration.Rotation.Provider);
        if (now<_nextShieldTick) return;
        _nextShieldTick=now.AddMilliseconds(100);
        var player=Service.LocalPlayer;
        var target=Service.TargetManager.Target as IBattleChara;
        var state=context.HohShieldBreak;
        try
        {
            bool eligible=HohShieldBreakState.Enabled(context.FarmingPlan?.Mode,context.Duty.DungeonId,
                context.Duty.Floor,configuration.Rotation.Provider) && context.Duty.IsInDuty &&
                !context.Duty.IsTransitioning && !context.StatusIsError && !context.NormalCombatOutputSuppressed &&
                context.ShieldBreakOutputPermitted &&
                !context.DutyFailureObserved && !context.AttemptAborted &&
                player is { IsDead:false,CurrentHp:>0 } && target is { IsDead:false,IsTargetable:true,CurrentHp:>0 } &&
                Service.Condition[ConditionFlag.InCombat] && context.IsEngagedCombatTarget(target) &&
                context.IsHohPetrificationTargetAllowed(target) &&
                !Service.Condition[ConditionFlag.BetweenAreas] && !Service.Condition[ConditionFlag.BetweenAreas51] &&
                !Service.Condition[ConditionFlag.OccupiedInEvent] && !Service.Condition[ConditionFlag.OccupiedInQuestEvent];
            bool shield=target?.StatusList.Any(s=>s.StatusId==HohShieldBreakState.Shield)==true;
            bool ae=false,protectedTarget=true,position=false,ready=false;
            uint action=0;
            if (eligible && shield)
            {
                _shieldAeRunning??=Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsRunning");
                _shieldAePull??=Service.PluginInterface.GetIpcSubscriber<bool>("AEAssist.CombatRoutine.IsPullEnabled");
                ae=_shieldAeRunning.HasFunction && _shieldAePull.HasFunction &&
                    _shieldAeRunning.InvokeFunc() && _shieldAePull.InvokeFunc();
                ReadShieldSafetyData(now);
                protectedTarget=_shieldOtherProtection==null || !_aeClassifiesStoneskin ||
                    target!.StatusList.Any(s=>_shieldOtherProtection.Contains(s.StatusId));
                var manager=ActionManager.Instance();
                uint baseAction=HohShieldBreakActions.ForJob(player!.ClassJob.RowId);
                action=manager!=null && baseAction!=0?manager->GetAdjustedActionId(baseAction):0;
                var row=action!=0?Service.DataManager.GetExcelSheet<global::Lumina.Excel.Sheets.Action>()?.GetRow(action):null;
                bool validAction=row is {} data && data.IsPlayerAction && data.CanTargetHostile &&
                    !data.TargetArea && data.ClassJobLevel<=player.Level;
                float range=validAction?ActionManager.GetActionRange(action):0;
                if (float.IsFinite(range) && range>0 && !protectedTarget)
                {
                    _shieldPointSafe??=Service.PluginInterface.GetIpcSubscriber<Vector3,Vector3,bool>("BossMod.Hints.IsDashSafe");
                    position=CombatPositionRecoveryController.HasClearOutputPosition(player.Position,target!.Position,
                        range,player.HitboxRadius,target.HitboxRadius) && _shieldPointSafe.HasFunction &&
                        _shieldPointSafe.InvokeFunc(player.Position,player.Position);
                }
                ready=validAction && manager!=null && manager->AnimationLock<=.05f &&
                    manager->IsActionOffCooldown(ActionType.Action,action) &&
                    manager->GetActionStatus(ActionType.Action,action,target!.GameObjectId)==Service.ActionStatus_Ready;
            }
            var frame=new HohShieldBreakFrame(target?.GameObjectId??0,eligible,
                eligible && context.IsEngagedCombatTarget(target!),shield,ae,context.DeathRecorder.NativeOutputAvailable,
                protectedTarget,position,ready,player?.IsCasting==true || Service.Condition[ConditionFlag.Casting] ||
                    context.HohRecoveryMovementActive);
            var decision=state.Update(frame,now);
            if (decision.Reason!=_shieldLastReason || frame.Target!=_shieldLastTarget)
            {
                _shieldLastReason=decision.Reason; _shieldLastTarget=frame.Target;
                if (shield || decision.Reason=="shield-gone-return-to-ae")
                    context.RecordDiagnostic?.Invoke("hoh-shield-break-state",new { floor=context.Duty.Floor,
                        targetId=frame.Target,decision.Reason,decision.Attempts,decision.HoldPosition,
                        aeEnabled=ae,shield,otherProtection=protectedTarget,outputPosition=position,action,
                        source="ae-enabled-stoneskin151-no-native-skill-progress" });
            }
            if (!decision.Cast || action==0 || Service.TargetManager.Target?.GameObjectId!=frame.Target) return;
            var cast=FsdActionExecutor.CastWithDiagnostics(action,frame.Target);
            state.RecordAttempt(frame.Target,action,cast.Accepted,now);
            context.RecordDiagnostic?.Invoke("hoh-shield-break-request",new { floor=context.Duty.Floor,
                targetId=frame.Target,action,attempt=decision.Attempts+1,maximumAttempts=HohShieldBreakState.MaximumAttempts,
                maximumWindowSeconds=HohShieldBreakState.Window.TotalSeconds,cast,
                purpose="break-existing-engaged-target-shield",activePull=false,
                result=cast.Accepted?"request-accepted-impact-and-shield-break-unconfirmed":"request-rejected" });
        }
        catch (Exception error)
        {
            state.Update(default,now);
            if (_shieldLastReason!="observer-unavailable")
            { _shieldLastReason="observer-unavailable";context.RecordDiagnostic?.Invoke("hoh-shield-break-unavailable",new{error=error.Message}); }
        }
    }

    private void ReadShieldSafetyData(DateTime now)
    {
        if (now<_nextShieldSafetyRead) return;
        _nextShieldSafetyRead=now.AddSeconds(30);
        _shieldOtherProtection=null; _aeClassifiesStoneskin=false;
        try
        {
            var parent=Path.GetDirectoryName(Service.PluginInterface.GetPluginConfigDirectory());
            if (parent==null) return;
            var dir=Path.Combine(parent,"AEAssistV3");
            uint[] Read(string name)=>JsonSerializer.Deserialize<uint[]>(File.ReadAllText(Path.Combine(dir,name)))
                ?? throw new InvalidDataException("Empty AE safety data: "+name);
            var invincible=Read("InvincibleStatus.json");
            var physical=Read("PhysicalReflectStatus.json");
            var magic=Read("MagicReflectStatus.json");
            _aeClassifiesStoneskin=invincible.Contains(HohShieldBreakState.Shield);
            _shieldOtherProtection=invincible.Where(id=>id!=HohShieldBreakState.Shield)
                .Concat(physical).Concat(magic).ToHashSet();
        }
        catch { /* Missing or changed AE safety data cannot authorize a direct attack. */ }
    }
}
