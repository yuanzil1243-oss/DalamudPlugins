namespace DeepDungeon.Fsd.Core;

public enum PreparedEntryMenuAction { Request, Wait, Stop }

/// <summary>Bounded retries for opening the entry UI, never for selecting a save.</summary>
public sealed class PreparedEntryRetryState
{
    public const int MaximumMenuRequests = 3;
    public static readonly TimeSpan MenuResponseGrace = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan NpcResponseGrace = TimeSpan.FromMilliseconds(2500);

    public int MenuRequests { get; private set; }
    public bool CommenceDispatched { get; private set; }
    private DateTime _nextMenuRequestUtc = DateTime.MinValue;
    private DateTime _nextNpcInteractionUtc = DateTime.MinValue;

    public bool CanInteractNpc(DateTime nowUtc) =>
        !CommenceDispatched && nowUtc >= _nextNpcInteractionUtc;

    public void RecordNpcInteraction(DateTime nowUtc) =>
        _nextNpcInteractionUtc = nowUtc + NpcResponseGrace;

    public PreparedEntryMenuAction DecideMenu(DateTime nowUtc)
    {
        if (CommenceDispatched || nowUtc < _nextMenuRequestUtc)
            return PreparedEntryMenuAction.Wait;
        return MenuRequests < MaximumMenuRequests
            ? PreparedEntryMenuAction.Request : PreparedEntryMenuAction.Stop;
    }

    public void RecordMenuRequest(DateTime nowUtc)
    {
        MenuRequests++;
        _nextMenuRequestUtc = nowUtc + MenuResponseGrace;
    }

    public void RecordCommence() => CommenceDispatched = true;

    public void Reset()
    {
        MenuRequests = 0;
        CommenceDispatched = false;
        _nextMenuRequestUtc = _nextNpcInteractionUtc = DateTime.MinValue;
    }
}
