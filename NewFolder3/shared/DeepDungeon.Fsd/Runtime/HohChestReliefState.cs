namespace DeepDungeon.Fsd.Runtime;

/// <summary>One native capped-loot opportunity may consume at most one item before reopening its own chest.</summary>
public sealed class HohChestReliefState
{
    public bool Consumed { get; private set; }
    public bool AwaitingLoot { get; private set; }
    public int? StockAfterUse { get; private set; }
    public void ConfirmConsumption(int count)
    { if (Consumed) return; Consumed = AwaitingLoot = true; StockAfterUse = count; }
    public bool ObserveLoot(int count, bool nativeChestComplete)
    {
        if (!AwaitingLoot || !(nativeChestComplete || count > StockAfterUse)) return false;
        AwaitingLoot = false; return true;
    }
    public bool CanRelieve(bool correlated, int stock, bool pending) =>
        correlated && stock == 3 && !Consumed && !pending;
}
