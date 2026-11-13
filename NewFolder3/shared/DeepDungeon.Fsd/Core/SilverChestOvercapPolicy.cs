namespace DeepDungeon.Fsd.Core;

public static class SilverChestOvercapPolicy
{
    public static byte ReliefStone(int fate, int wisdom, int serenity, bool passageOpen, bool passageItemUsed, bool reserveFate = false)
    {
        if (fate + wisdom + serenity < 3) return 0;
        if (serenity == 3) return 3;
        if (passageOpen || passageItemUsed) return 0;
        return fate > 0 && !reserveFate ? (byte)1 : wisdom > 0 ? (byte)2 : (byte)0;
    }
}
