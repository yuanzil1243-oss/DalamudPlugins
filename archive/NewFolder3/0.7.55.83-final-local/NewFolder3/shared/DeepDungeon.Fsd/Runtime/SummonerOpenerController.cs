namespace DeepDungeon.Fsd.Runtime;

public enum SummonerOpenerStage { Idle, StoppingOutput, AwaitingEffect, Complete, Failed }

public static class SummonerOpenerActionPolicy
{
    public const uint TranceActionId = 3581, BahamutActionId = 7427,
        PhoenixActionId = 25831, SolarBahamutActionId = 36992;

    public static uint Select(uint level, bool tranceReady, bool bahamutReady, uint adjustedBahamut,
        bool phoenixReady)
    {
        if (level < 58) return 0;
        if (level < 70) return tranceReady ? TranceActionId : 0;
        if (bahamutReady && adjustedBahamut != 0) return adjustedBahamut;
        if (level >= 80 && phoenixReady) return PhoenixActionId;
        return 0;
    }
}

/// <summary>Owns one bounded summon opener attempt per combat, then hands control back to the rotation provider.</summary>
public sealed class SummonerOpenerController
{
    private DateTime _deadline;
    public SummonerOpenerStage Stage { get; private set; }
    public ulong TargetId { get; private set; }
    public uint ActionId { get; private set; }
    public bool HoldsOutput => Stage is SummonerOpenerStage.StoppingOutput or SummonerOpenerStage.AwaitingEffect;

    public bool TryBegin(ulong targetId, uint actionId, bool eligible, DateTime now)
    {
        if (!eligible || targetId == 0 || actionId == 0 || Stage != SummonerOpenerStage.Idle) return false;
        TargetId = targetId;
        ActionId = actionId;
        _deadline = now.AddSeconds(1.5);
        Stage = SummonerOpenerStage.StoppingOutput;
        return true;
    }

    public uint Tick(DateTime now, bool aeConfirmedOff, bool summonEffectConfirmed)
    {
        if (Stage == SummonerOpenerStage.StoppingOutput)
        {
            if (now >= _deadline) { Stage = SummonerOpenerStage.Failed; return 0; }
            if (aeConfirmedOff) return ActionId;
        }
        else if (Stage == SummonerOpenerStage.AwaitingEffect)
        {
            if (summonEffectConfirmed) Stage = SummonerOpenerStage.Complete;
            else if (now >= _deadline) Stage = SummonerOpenerStage.Failed;
        }
        return 0;
    }

    public void RecordCast(bool accepted, DateTime now)
    {
        if (Stage != SummonerOpenerStage.StoppingOutput) return;
        Stage = accepted ? SummonerOpenerStage.AwaitingEffect : SummonerOpenerStage.Failed;
        _deadline = now.AddSeconds(2);
    }

    public void ResetCombat()
    {
        Stage = SummonerOpenerStage.Idle;
        TargetId = 0;
        ActionId = 0;
        _deadline = default;
    }

    public void Abort() { if (HoldsOutput) Stage = SummonerOpenerStage.Failed; }
}
