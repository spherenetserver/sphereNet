using SphereNet.Game.Objects.Characters;

namespace SphereNet.Game.Trade;

/// <summary>
/// Source-X FEATURE_TOL_VIRTUALGOLD (FeatureTOL bit 0x02). With it on, a
/// character's money is a number (VIRTUALGOLD) rather than gold coins: gold dropped
/// in the bank is credited to it, vendors are paid from it and pay into it, the
/// status bar shows it, GOLD reads and writes it, and the trade window's gold and
/// platinum fields move it between the two traders. Off, every one of those paths
/// is the coin-based one it always was.
/// </summary>
public static class VirtualGold
{
    public const int FeatureBit = 0x02;

    /// <summary>One platinum is a billion gold (Source-X 1000000000LL).</summary>
    public const long PlatinumValue = 1_000_000_000L;

    /// <summary>Set from sphere.ini FeatureTOL at startup.</summary>
    public static bool Enabled { get; set; }

    public static long Get(Character ch) =>
        ch.TryGetTag("VIRTUALGOLD", out string? raw) && long.TryParse(raw, out long v) ? v : 0;

    public static void Set(Character ch, long amount)
    {
        amount = Math.Max(0, amount);
        if (amount == 0) ch.RemoveTag("VIRTUALGOLD");
        else ch.SetTag("VIRTUALGOLD", amount.ToString());
        ch.MarkDirty(SphereNet.Core.Enums.DirtyFlag.Stats);
    }

    public static void Add(Character ch, long amount) =>
        Set(ch, Get(ch) > long.MaxValue - amount ? long.MaxValue : Get(ch) + amount);

    /// <summary>Split a gold total into the (gold, platinum) pair the client shows.</summary>
    public static (uint Gold, uint Platinum) Split(long total)
    {
        total = Math.Max(0, total);
        return ((uint)(total % PlatinumValue), (uint)Math.Min(total / PlatinumValue, uint.MaxValue));
    }

    public static long Join(uint gold, uint platinum) => gold + platinum * PlatinumValue;
}
