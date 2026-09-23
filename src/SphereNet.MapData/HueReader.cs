namespace SphereNet.MapData;

/// <summary>
/// Reader for hues.mul — the client's colour tables. The file is a run of
/// groups, each a 4-byte header followed by 8 entries of
/// 32 x ushort colours (1555) + ushort table start + ushort table end + 20-byte name.
/// A hue value N (1-based, after masking with 0x3FFF) is entry N-1; hue 0 means
/// "no hue". The whole file is small (well under 1 MB), so it is read once into
/// memory and every lookup afterwards is lock-free.
/// Layout follows the client's own loader (ClassicUO HuesLoader / HuesGroup).
/// </summary>
public sealed class HueReader
{
    public const int ColorsPerHue = 32;
    private const int EntriesPerGroup = 8;
    private const int EntrySize = ColorsPerHue * 2 + 2 + 2 + 20; // 88
    private const int GroupSize = 4 + EntriesPerGroup * EntrySize; // 708

    private readonly ushort[][] _tables;

    private HueReader(ushort[][] tables) => _tables = tables;

    /// <summary>Number of hue entries in the file.</summary>
    public int Count => _tables.Length;

    /// <summary>Load hues.mul; null when the file is missing or empty.</summary>
    public static HueReader? Load(string path)
    {
        if (!File.Exists(path))
            return null;
        byte[] data = File.ReadAllBytes(path);
        return FromBytes(data);
    }

    /// <summary>Parse an in-memory hues.mul image (also used by tests).</summary>
    public static HueReader? FromBytes(ReadOnlySpan<byte> data)
    {
        int groups = data.Length / GroupSize;
        if (groups == 0)
            return null;

        var tables = new ushort[groups * EntriesPerGroup][];
        for (int g = 0; g < groups; g++)
        {
            int groupOff = g * GroupSize + 4; // skip the group header
            for (int e = 0; e < EntriesPerGroup; e++)
            {
                int off = groupOff + e * EntrySize;
                var table = new ushort[ColorsPerHue];
                for (int c = 0; c < ColorsPerHue; c++)
                    table[c] = (ushort)(data[off + c * 2] | (data[off + c * 2 + 1] << 8));
                tables[g * EntriesPerGroup + e] = table;
            }
        }
        return new HueReader(tables);
    }

    /// <summary>The 32-colour table for a hue value (1-based; the caller masks
    /// the flag bits). Null for hue 0 or a value past the end of the file —
    /// the client draws those unhued too (HuesLoader.GetColor: color &lt; HuesCount).</summary>
    public ushort[]? GetColorTable(int hue)
    {
        if (hue <= 0 || hue > _tables.Length)
            return null;
        return _tables[hue - 1];
    }
}
