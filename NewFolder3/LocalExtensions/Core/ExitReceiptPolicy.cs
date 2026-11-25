namespace DeepDungeon.Fsd.Core;

/// <summary>Advance an exit receipt only when live UI offers a recognized next step.</summary>
public static class ExitReceiptPolicy
{
    public static bool ShouldClear(in EntryDispatchReceipt pending, string scope, ExitAction nextAction) =>
        pending.Scope != scope ||
        pending.Action == nameof(ExitAction.ConfirmDutyExit) && nextAction == ExitAction.LeaveResult ||
        pending.Action == nameof(ExitAction.LeaveResult) &&
            nextAction is ExitAction.ConfirmDutyExit or ExitAction.ConfirmRestExit;
}
