namespace DeepDungeon.Fsd.Core;

public sealed record DamageEvidence(DateTime AtUtc, uint SourceId, uint SourceNameId, uint SourceBaseId,
    string Monster, uint ActionId, byte ActionType, string Skill, int Damage, uint Sequence, uint HpBefore);

public static class DeathEvidencePolicy
{
    public static int DecodeDamage(byte high, byte flags, ushort low) => low + ((flags & 0x40) != 0 ? high * 65536 : 0);
    public static DamageEvidence? RecentDamage(IEnumerable<DamageEvidence> events, DateTime deathAt) =>
        events.Where(e => e.Damage > 0 && e.AtUtc <= deathAt && deathAt - e.AtUtc <= TimeSpan.FromSeconds(3))
            .OrderBy(e => e.AtUtc).LastOrDefault();
    public static bool IsLethalCandidate(DamageEvidence? damage) =>
        damage is { HpBefore: > 0 } && damage.Damage >= damage.HpBefore;
}
