using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Objects.Enums;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

internal sealed partial class TerminalRoomController
{
    private ulong _hohExitBefore;
    private void UpdateHoh100Shrine(DateTime now)
    {
        var ctx = _ctx!;
        var player = Service.LocalPlayer;
        if (player == null || _resultAltarComplete) return;
        var shrine = Service.GameObjects.FirstOrDefault(o => o.ObjectKind == ObjectKind.EventObj && o.BaseId == HohLocalPolicy.ShrineBaseId);
        var exit = Service.GameObjects.FirstOrDefault(o => o.ObjectKind == ObjectKind.EventObj && o.BaseId == HohLocalPolicy.ResultExitBaseId && o.IsTargetable);
        if (shrine != null && HohLocalPolicy.CanConfirmShrine(_resultAltarInteractedId, shrine.GameObjectId,
            shrine.IsTargetable, _resultAltarCompletionBefore, ctx.DutyCompletionSequence, _hohExitBefore,
            exit?.GameObjectId ?? 0, ctx.DutyFailureObserved))
        {
            _resultAltarComplete = true; ctx.TerminalRewardsRequired = false;
            if (!ctx.DutyCompletionObserved) ctx.MarkDutyCompleted();
            _navHelper?.Cancel(); ctx.Navigator.CancelAll();
            Status = ctx.StatusLine = "天宮100層：神祠狀態及完成／新出口已確認，正常退場。";
            RecordReplayEvent("hoh-shrine-confirmed", new { shrine.GameObjectId, shrine.BaseId, exitId = exit?.GameObjectId });
            return;
        }
        if ((now - _resultStarted).TotalSeconds > 120)
        { StopResultReview("天宮100層神祠／完成證據逾時；保留現場，不計通關。", now); return; }
        if (shrine == null || !shrine.IsTargetable) { Status = "天宮100層：等待神祠物件／完成證據"; return; }
        if (_resultAltarInteractedId != 0 && _resultAltarInteractedId != shrine.GameObjectId)
        { StopResultReview("天宮神祠身份變更；保留現場。", now); return; }
        if (Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInEvent] ||
            Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent]) return;
        var state = _navHelper!.Navigate(ResolvePassageWalkingPosition(shrine.Position), player.Position, 2.5f);
        Status = "天宮100層：前往神祠確認登頂";
        if (state is NavigationState.Failed or NavigationState.StuckGiveUp)
        { StopResultReview("天宮100層神祠導航失敗；保留現場。", now); return; }
        if (state != NavigationState.Arrived || now < _resultNextInteract) return;
        _resultNextInteract = now.AddSeconds(3);
        long completionBefore = ctx.DutyCompletionSequence;
        ulong exitBefore = exit?.GameObjectId ?? 0;
        GameInteraction.InteractWith(shrine, 3f, false, out var failure, out bool dispatched);
        if (dispatched && _resultAltarInteractedId == 0)
        { _resultAltarInteractedId = shrine.GameObjectId; _resultAltarCompletionBefore = completionBefore; _hohExitBefore = exitBefore; }
        RecordReplayEvent("hoh-shrine-interaction", new { shrine.GameObjectId, shrine.BaseId, dispatched, failure });
    }
}
