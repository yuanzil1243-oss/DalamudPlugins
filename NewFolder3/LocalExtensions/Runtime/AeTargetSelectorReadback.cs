namespace DeepDungeon.Fsd.Runtime;

public enum AeTargetSelectorState { Unknown, Off, On }
public enum AeTargetSelectorVerificationResult { NotRequested, Pending, ConfirmedOff, StillEnabled, Unknown }

/// <summary>Reads only AE's named DTR entry, including hidden entries. Unknown text is not proof.</summary>
public sealed class AeTargetSelectorReadback
{
    public const string EntryTitle = "AEAssist-TargetSelector";
    private static readonly string[] EnabledModes =
    [ "当前血量百分比最高", "当前血量百分比最低", "当前血量数值最高", "当前血量数值最低",
      "总血量最高", "距离最近", "狩猎触发农怪", "狩猎跟车", "Fate模式", "深层迷宫模式" ];
    private long? _deadline;
    public bool Pending => _deadline.HasValue;

    public static AeTargetSelectorState Read(IEnumerable<(string Title, string? Text)> entries)
    {
        var matches = entries.Where(e => e.Title == EntryTitle).Take(2).ToArray();
        if (matches.Length != 1) return AeTargetSelectorState.Unknown;
        string? text = matches[0].Text?.Trim();
        if (text == "目标选择器: Off") return AeTargetSelectorState.Off;
        return EnabledModes.Any(m => text == m || text == $"智能({m})")
            ? AeTargetSelectorState.On : AeTargetSelectorState.Unknown;
    }

    public void CommandSent(long now) => _deadline = now + 2000;
    public void DeferIfPending(long now) { if (Pending) _deadline = now + 2000; }
    public void Reset() => _deadline = null;

    public AeTargetSelectorVerificationResult Observe(long now, AeTargetSelectorState state)
    {
        if (_deadline is not { } deadline) return AeTargetSelectorVerificationResult.NotRequested;
        if (state == AeTargetSelectorState.Off) { Reset(); return AeTargetSelectorVerificationResult.ConfirmedOff; }
        // AE writes its DTR text in its own update; do not reject a previous-frame mode label.
        if (now < deadline) return AeTargetSelectorVerificationResult.Pending;
        Reset();
        return state == AeTargetSelectorState.On ? AeTargetSelectorVerificationResult.StillEnabled
            : AeTargetSelectorVerificationResult.Unknown;
    }
}
