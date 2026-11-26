using DeepDungeon.Fsd.Core;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    internal FeatureTriggerCounters? TriggerCounters { get; private set; }
    private GcdTriggerConfirmation? gcdTriggerConfirmation;
    private GcdTriggerReceiptObserver? gcdTriggerObserver;
    private long nextGcdTriggerHookRetry;

    internal void AttachTriggerCounters(FeatureTriggerCounters counters)
    {
        DisposeGcdTriggerObservation();
        TriggerCounters = counters;
        gcdTriggerConfirmation = new(counters);
    }
    private void UpdateGcdTriggerObservation(bool eligible)
    {
        if (!eligible)
        {
            gcdTriggerConfirmation?.Reset();
            try { gcdTriggerObserver?.Dispose(); } catch { }
            gcdTriggerObserver = null;
            return;
        }
        if (gcdTriggerConfirmation == null || gcdTriggerObserver != null || Environment.TickCount64 < nextGcdTriggerHookRetry) return;
        try
        {
            gcdTriggerObserver = new(gcdTriggerConfirmation, () =>
                _configuration.Farming.GcdStallProtection && Service.Framework.IsInFrameworkUpdateThread &&
                IsGcdProtectionInDeepDungeon() && Service.LocalPlayer is { IsDead: false });
        }
        catch (Exception ex)
        {
            nextGcdTriggerHookRetry = Environment.TickCount64 + 10000;
            try { Service.Log.Warning(ex, "GCD trigger receipt observation unavailable; assist behavior unchanged."); } catch { }
        }
    }
    private void DisposeGcdTriggerObservation()
    {
        gcdTriggerConfirmation?.Reset();
        try { gcdTriggerObserver?.Dispose(); } catch { }
        gcdTriggerObserver = null;
    }
}
