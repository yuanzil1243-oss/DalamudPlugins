using DeepDungeon.Fsd.Core;

namespace DeepDungeon.Fsd.Runtime;

public enum AetherpoolSource { Unknown, InDutyMemory, LogMessage, ConfirmedCraft }
public readonly record struct AetherpoolReading(ulong Character, uint Dungeon, int? Weapon, int? Armor,
    AetherpoolSource Source, DateTime ObservedAtUtc)
{
    public DateTime WeaponObservedAtUtc { get; init; }
    public DateTime ArmorObservedAtUtc { get; init; }
}

/// <summary>Character-scoped session cache. Never persists or reads arbitrary chat messages.</summary>
public sealed class AetherpoolStateStore
{
    private readonly Dictionary<uint, AetherpoolReading> _readings = new();
    public ulong Character { get; private set; }
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);
    public void SetCharacter(ulong character)
    {
        if (Character == character) return;
        _readings.Clear(); Character = character;
    }
    public bool TryGet(ulong character, uint dungeon, DateTime now, out AetherpoolReading reading)
    {
        reading = default;
        if (character == 0 || character != Character || !_readings.TryGetValue(dungeon, out reading) ||
            now < reading.ObservedAtUtc || now - reading.ObservedAtUtc > MaxAge) return false;
        if (now - reading.WeaponObservedAtUtc > MaxAge) reading = reading with { Weapon = null };
        if (now - reading.ArmorObservedAtUtc > MaxAge) reading = reading with { Armor = null };
        return reading.Weapon != null || reading.Armor != null;
    }
    public void Observe(ulong character, uint dungeon, int? weapon, int? armor, AetherpoolSource source, DateTime now)
    {
        SetCharacter(character);
        if (character == 0 || AetherpoolExchangePolicy.GetDungeon(dungeon) == null ||
            source is not (AetherpoolSource.InDutyMemory or AetherpoolSource.LogMessage) ||
            weapon is < 0 or > 99 || armor is < 0 or > 99 || weapon == null && armor == null) return;
        _readings.TryGetValue(dungeon, out var old);
        if (now - old.WeaponObservedAtUtc > MaxAge) old = old with { Weapon = null };
        if (now - old.ArmorObservedAtUtc > MaxAge) old = old with { Armor = null };
        // Delayed enhancement messages must not undo a later live reading or a confirmed deduction.
        // Logs cannot establish a post-craft state; only fresh in-duty memory can do that.
        if (source == AetherpoolSource.LogMessage && old.Source == AetherpoolSource.ConfirmedCraft) return;
        if (source == AetherpoolSource.LogMessage)
        {
            if (weapon < old.Weapon) weapon = null;
            if (armor < old.Armor) armor = null;
            if (weapon == null && armor == null) return;
        }
        _readings[dungeon] = new(character, dungeon, weapon ?? old.Weapon, armor ?? old.Armor, source, now)
        {
            WeaponObservedAtUtc = weapon.HasValue ? now : old.WeaponObservedAtUtc,
            ArmorObservedAtUtc = armor.HasValue ? now : old.ArmorObservedAtUtc
        };
    }
    public bool RecordConfirmedCraft(ulong character, uint dungeon, DateTime now)
    {
        if (!TryGet(character, dungeon, now, out var old) || old.Weapon is not >= 10 || old.Armor is not >= 10) return false;
        _readings[dungeon] = old with { Weapon = old.Weapon - 10, Armor = old.Armor - 10,
            Source = AetherpoolSource.ConfirmedCraft, ObservedAtUtc = now,
            WeaponObservedAtUtc = now, ArmorObservedAtUtc = now };
        return true;
    }
}

/// <summary>One irreversible dispatch at a time; uncertain outcomes never permit another craft.</summary>
public sealed class GripCraftReceipt
{
    public bool Pending { get; private set; }
    public bool ConfirmationSent { get; private set; }
    private int _baseline;
    private DateTime _started;
    public void Begin(int inventory, DateTime now)
    {
        if (Pending || inventory < 0) throw new InvalidOperationException("Craft receipt already pending or invalid.");
        Pending = true; ConfirmationSent = false; _baseline = inventory; _started = now;
    }
    public bool ConfirmOnce()
    {
        if (!Pending || ConfirmationSent) return false;
        ConfirmationSent = true; return true;
    }
    public bool Observe(int inventory, DateTime now, out bool completed, out string error)
    {
        completed = false; error = "";
        if (!Pending) return true;
        int delta = inventory - _baseline;
        if (delta != 0)
        {
            if (!ConfirmationSent || delta != 1) { error = "背包數量出現無法歸屬本次製作的變動。"; return false; }
            Pending = false; completed = true; return true;
        }
        if (now - _started > TimeSpan.FromSeconds(12))
        { error = "製作結果超時；不重複提交，請確認強化值與背包空間。"; return false; }
        return true;
    }
}

internal sealed partial class FarmingSession
{
    internal bool InitialGripExchangeChecked { get; set; }
}
