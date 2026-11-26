namespace NewFolder3;

public sealed partial class Plugin
{
    private long nextTriggerCounterSave;
    private void FlushTriggerCounters()
    {
        long now = Environment.TickCount64;
        if (now < nextTriggerCounterSave) return;
        // Successful events flush on the next update, including when FSD is stopped.
        // Failed writes retain dirty in-memory totals and retry; no callback does file I/O.
        if (_configuration.TriggerCounters?.Flush() == false) nextTriggerCounterSave = now + 2000;
    }
}
