namespace SphereNet.MapData.Multi;

/// <summary>
/// A single component of a multi-structure (house/ship).
/// </summary>
public readonly struct MultiComponent
{
    public ushort TileId { get; init; }
    public short XOffset { get; init; }
    public short YOffset { get; init; }
    public short ZOffset { get; init; }
    public uint Flags { get; init; }

    /// <summary>High Seas+ trailing dword (rope item used to enter/exit galleons).
    /// 0 for original 12-byte multi records.</summary>
    public uint ShipAccess { get; init; }

    /// <summary>Client-parity visibility (ClassicUO MultiLoader .mul path:
    /// <c>IsVisible = Flags != 0</c>): flags != 0 marks the STATIC parts the
    /// client draws (hull, walls, roof); flags == 0 marks the invisible
    /// placeholders that dynamic server items replace (doors, tiller, planks,
    /// hatch). This was inverted — a placed ship materialised ONLY the five
    /// placeholder pieces and no hull.</summary>
    public bool IsVisible => Flags != 0;
}

/// <summary>
/// Complete multi-structure definition loaded from multi.mul.
/// </summary>
public sealed class MultiDef
{
    public int MultiId { get; }
    public MultiComponent[] Components { get; }

    public MultiDef(int multiId, MultiComponent[] components)
    {
        MultiId = multiId;
        Components = components;
    }

    public (short MinX, short MinY, short MaxX, short MaxY) GetBounds()
    {
        if (Components.Length == 0)
            return (0, 0, 0, 0);

        short minX = short.MaxValue, minY = short.MaxValue;
        short maxX = short.MinValue, maxY = short.MinValue;

        foreach (var c in Components)
        {
            if (c.XOffset < minX) minX = c.XOffset;
            if (c.YOffset < minY) minY = c.YOffset;
            if (c.XOffset > maxX) maxX = c.XOffset;
            if (c.YOffset > maxY) maxY = c.YOffset;
        }

        return (minX, minY, maxX, maxY);
    }
}
