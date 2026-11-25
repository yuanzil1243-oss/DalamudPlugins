namespace DeepDungeon.Fsd.Runtime;

/// <summary>Leases readable output switches; disables AE targeting before each output boundary.</summary>
public sealed class AeOutputLease(Func<bool> readRunning, Func<bool> readPull,
    Action<bool> setRunning, Action<bool> setPull, Func<bool> disableTargetSelector)
{
    public const string TargetSelectorDisableCommand = "/aeTargetSelector off";
    private (bool Running, bool Pull)? original, expected;
    public bool IsOwned => original.HasValue;

    public bool TrySet(bool enabled)
    {
        try
        {
            // An explicit off command is idempotent. There is no selector read IPC,
            // so never toggle it or invent a prior value to restore at shutdown.
            if (!disableTargetSelector()) { Restore(); return false; }
            var live = (Running: readRunning(), Pull: readPull());
            if (expected is { } prior && live != prior)
            {
                Restore(); // Respect manual changes; do not keep fighting the user.
                return false;
            }
            original ??= live;
            expected = live;
            // Disable pull first so changing the main switch cannot briefly start output.
            WritePull(false);
            WriteRunning(enabled);
            WritePull(enabled);
            bool accepted = readRunning() == enabled && readPull() == enabled;
            if (!accepted) Restore();
            return accepted;
        }
        catch
        {
            Restore();
            return false;
        }
    }

    private void WriteRunning(bool value)
    {
        var prior = expected!.Value;
        expected = prior with { Running = value };
        setRunning(value);
    }

    private void WritePull(bool value)
    {
        var prior = expected!.Value;
        expected = prior with { Pull = value };
        setPull(value);
    }

    // Restore only values still equal to our last write. An external/manual change wins.
    public bool Restore()
    {
        if (original is not { } saved || expected is not { } ours) return true;
        bool success = true;
        try
        {
            if (readRunning() == ours.Running)
            {
                setRunning(saved.Running);
                success &= readRunning() == saved.Running;
            }
        }
        catch { success = false; }
        try
        {
            if (readPull() == ours.Pull)
            {
                setPull(saved.Pull);
                success &= readPull() == saved.Pull;
            }
        }
        catch { success = false; }
        // Keep ownership on transient IPC failure so a later update/dispose can retry.
        if (success) { original = expected = null; }
        return success;
    }
}
