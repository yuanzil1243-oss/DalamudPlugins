using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

internal sealed partial class FarmingSession
{
    internal void BindFailedSaveCheckpoint(SaveSlotSnapshot source)
    {
        if (source.Empty || !source.Enterable || source.Index != OwnedSlot)
            throw new InvalidOperationException("失敗後續打來源與本任務存檔不符。");
        Source.PinCheckpoint(source);
        Source.ReleaseCheckpointPin();
        Source.ConfigureForEntry(Plan.ReusesSave, source.StartFloor, source.Index, Plan.MaxFloor);
    }
}
