using SphereNet.Game.Objects.Items;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Game.Housing;

/// <summary>
/// A custom house design: the list of placed tiles plus a revision counter.
/// Persisted on the multi item as DESIGN_n tags ("tileId,dx,dy,dz,flags" —
/// same format the script ADDITEM/ADDMULTI verbs write) and DESIGN_REVISION,
/// so committed designs survive saves through the existing TAG pipeline.
/// </summary>
public sealed class HouseDesign
{
    public const string RevisionTag = "DESIGN_REVISION";
    private const string TilePrefix = "DESIGN_";

    public List<HouseDesignTile> Tiles { get; } = [];
    public uint Revision { get; set; } = 1;

    public HouseDesign Clone()
    {
        var copy = new HouseDesign { Revision = Revision };
        copy.Tiles.AddRange(Tiles);
        return copy;
    }

    /// <summary>Load the committed design from the multi item's tags.</summary>
    public static HouseDesign LoadFromTags(Item multi)
    {
        var design = new HouseDesign();
        if (multi.TryGetTag(RevisionTag, out string? revStr) && uint.TryParse(revStr, out uint rev))
            design.Revision = rev;

        for (int n = 0; multi.TryGetTag($"{TilePrefix}{n}", out string? entry); n++)
        {
            if (string.IsNullOrEmpty(entry))
                continue;
            var parts = entry.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 4)
                continue;
            if (!TryParseNumber(parts[0], out int tileId))
                continue;
            if (!int.TryParse(parts[1], out int dx) ||
                !int.TryParse(parts[2], out int dy) ||
                !int.TryParse(parts[3], out int dz))
                continue;
            design.Tiles.Add(new HouseDesignTile((ushort)tileId, (sbyte)dx, (sbyte)dy, (sbyte)dz));
        }
        return design;
    }

    /// <summary>Persist this design to the multi item's tags, replacing any
    /// previous DESIGN_n entries.</summary>
    public void SaveToTags(Item multi)
    {
        foreach (var (key, _) in multi.Tags.GetAll().ToArray())
        {
            if (key.StartsWith(TilePrefix, StringComparison.OrdinalIgnoreCase)
                && !key.Equals(RevisionTag, StringComparison.OrdinalIgnoreCase))
                multi.Tags.Remove(key);
        }

        for (int i = 0; i < Tiles.Count; i++)
        {
            var t = Tiles[i];
            multi.Tags.Set($"{TilePrefix}{i}", $"0x{t.TileId:X},{t.X},{t.Y},{t.Z},0");
        }
        multi.Tags.Set(RevisionTag, Revision.ToString());
    }

    /// <summary>Accepts decimal ("100") and hex ("0x64") tile ids — script
    /// ADDITEM entries may use either form.</summary>
    private static bool TryParseNumber(string text, out int value)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        return int.TryParse(text, out value);
    }
}
