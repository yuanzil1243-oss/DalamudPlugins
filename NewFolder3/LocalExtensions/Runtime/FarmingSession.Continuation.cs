using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

internal sealed partial class FarmingSession
{
    internal bool PendingDutyAttachment { get; private set; }
    internal bool NeedsContinuationSource { get; private set; }

    internal void AttachCurrentDuty(int floor)
    {
        string error = string.Empty;
        if (!Plan.ManagedContinuation || !TaskContinuationPolicy.ValidateMode(Plan, floor, out error))
            throw new InvalidOperationException("無法接續目前副本。" + error);
        NextFloor = floor;
        PendingDutyAttachment = true;
        NeedsContinuationSource = true;
    }

    internal void ConfirmDutyAttachment() => PendingDutyAttachment = false;

    internal void BindContinuationSource(SaveSlotSnapshot slot)
    {
        if (!NeedsContinuationSource || OwnedSlot >= 0 || slot.Index is < 0 or > 1 || slot.Empty)
            throw new InvalidOperationException("續打來源狀態已變更。");
        OwnedSlot = slot.Index;
        Source.PinCheckpoint(slot);
        Source.ReleaseCheckpointPin();
        Source.ConfigureForEntry(Plan.ReusesSave, Plan.ReusesSave ? null : NextFloor, slot.Index, Plan.MaxFloor);
        NeedsContinuationSource = false;
    }
}
