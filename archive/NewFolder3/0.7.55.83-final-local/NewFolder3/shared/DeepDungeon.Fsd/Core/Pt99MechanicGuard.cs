namespace DeepDungeon.Fsd.Core;

/// <summary>Drain Aether owns color timing. Spatial mechanics use live BMR route safety instead of a blanket hold.</summary>
public sealed class Pt99MechanicGuard
{
    public DateTime BlockedUntil { get; private set; }
    public DateTime SpatialUntil { get; private set; }
    public bool IsBlocked(DateTime now) => now < BlockedUntil;
    public static bool IsDrainAether(uint action) => action is 44088 or 44089 or 44090 or 44091 or 44092 or 44093;

    public void ObserveCast(uint action, double remaining, DateTime now)
    {
        if (!double.IsFinite(remaining)) return;
        // Auto-attack visuals do not occupy movement. Spatial deadlines are advisory;
        // only Drain Aether reserves the entire color-timing window.
        double aftermath = IsDrainAether(action) ? 1.5 : action switch
        {
            44074 or 44075 or 44077 or 44076 or 44079 => 15, // crystal/exaflare sequence
            44081 or 44082 or 44083 => 5, // pull, bind and the delayed central explosion
            44085 or 44086 or 45118 => 7, // consecutive Spinelash lines
            44061 or 44068 or 44062 or 45115 => 2, // ground circles
            44063 or 44069 or 44064 or 44070 or 44065 or 44066 or 44067 or 44071 or 44072 or 44073 => 1.5,
            _ => -1
        };
        if (aftermath < 0) return;
        var until = now.AddSeconds(Math.Clamp(remaining, 0, 30) + aftermath);
        if (IsDrainAether(action))
        {
            if (until > BlockedUntil) BlockedUntil = until;
        }
        else if (until > SpatialUntil) SpatialUntil = until;
    }

    public void Reset() => BlockedUntil = SpatialUntil = default;
}
