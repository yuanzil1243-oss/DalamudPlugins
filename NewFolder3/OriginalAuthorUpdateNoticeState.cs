namespace NewFolder3;

/// <summary>Each plugin load requires an explicit acknowledgement; nothing is persisted.</summary>
internal sealed class OriginalAuthorUpdateNoticeState
{
    internal const string RepositoryUrl = "https://github.com/transparent-duck/NewFolder3";
    internal const string FeedUrl = "https://raw.githubusercontent.com/transparent-duck/NewFolder3/refs/heads/main/NewFolder3-release.json";
    internal const string Recommendation = "原作者已经完善了死宫、天宫、妖宫的下底功能，推荐使用原作者版本。";
    internal const string PendingMessage = "请先阅读原作者更新提示，并点击“确定”后继续使用本地版。";
    private volatile bool _acknowledged;

    internal bool Acknowledged => _acknowledged;
    internal void Acknowledge() => _acknowledged = true;
}

/// <summary>The notice is an additional host gate, never a replacement for existing authorization.</summary>
internal sealed class OriginalAuthorUpdateNoticeAccessGate(
    INewFolder3AccessGate inner,
    OriginalAuthorUpdateNoticeState notice) : INewFolder3AccessGate
{
    public NewFolder3AccessDecision Current => notice.Acknowledged
        ? inner.Current
        : NewFolder3AccessDecision.Denied(OriginalAuthorUpdateNoticeState.PendingMessage);

    public string FsdStartDenialNotice => notice.Acknowledged
        ? inner.FsdStartDenialNotice
        : OriginalAuthorUpdateNoticeState.PendingMessage;

    public bool TryAuthorizeFsdStart(out string error)
    {
        if (!notice.Acknowledged)
        {
            error = OriginalAuthorUpdateNoticeState.PendingMessage;
            return false;
        }
        return inner.TryAuthorizeFsdStart(out error);
    }
}
