namespace DeepDungeon.Fsd.Core;

/// <summary>Preparation cannot spend the later mandatory recovery budget. Mechanic holds spend neither budget.</summary>
public sealed class Pt99ColorRecoveryBudget
{
    public bool Required { get; private set; }
    public double ActiveSeconds { get; private set; }
    public double EligibleSeconds { get; private set; }
    public int Failures { get; private set; }
    public bool Exhausted => ActiveSeconds >= 35 || EligibleSeconds >= 120 || Failures >= 3;

    public void Reset(bool required = false)
    { Required = required; ActiveSeconds = EligibleSeconds = 0; Failures = 0; }

    public void SetRequired(bool required)
    {
        if (required && !Required) Reset(true);
        else Required = required;
    }

    public void Tick(double elapsed, bool mechanicBlocked, bool ownsMovement)
    {
        if (mechanicBlocked || !double.IsFinite(elapsed) || elapsed <= 0) return;
        elapsed = Math.Min(elapsed, 1);
        EligibleSeconds += elapsed;
        if (ownsMovement) ActiveSeconds += elapsed;
    }

    public void RecordFailure() => Failures++;
}
