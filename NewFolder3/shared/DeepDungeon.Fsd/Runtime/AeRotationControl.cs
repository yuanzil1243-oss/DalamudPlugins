namespace DeepDungeon.Fsd.Runtime;

/// <summary>Explicit, idempotent AE controls; target selection remains the dungeon controller's responsibility.</summary>
public sealed class AeRotationControl(Func<bool> readRunning, Func<bool> readPull,
    Action<bool> setRunning, Action<bool> setPull, Func<bool, bool> setStopped)
{
    public bool? LastStopRequest { get; private set; }
    public FsdRotationState ReadState()
    {
        if (!readRunning() || !readPull() || LastStopRequest == true) return FsdRotationState.Off;
        return LastStopRequest == false ? FsdRotationState.On : FsdRotationState.Unknown;
    }

    public bool SetEnabled(bool enabled)
    {
        try
        {
            // Hold output while changing the main and pull switches, even for repeated enable requests.
            if (!Stop(true)) { FailClosed(); return false; }
            if (enabled)
            {
                setRunning(true);
                setPull(true);
                if (!readRunning() || !readPull() || !Stop(false)) { FailClosed(); return false; }
                return true;
            }
            setPull(false);
            setRunning(false);
            return !readRunning() && !readPull();
        }
        catch { FailClosed(); return false; }
    }

    private bool Stop(bool value)
    {
        bool accepted = setStopped(value);
        LastStopRequest = accepted ? value : null;
        return accepted;
    }

    private void FailClosed()
    {
        try { Stop(true); } catch { LastStopRequest = null; }
        try { setPull(false); } catch { }
        try { setRunning(false); } catch { }
    }
}
