namespace SphereNet.Network.Packets.Outgoing;

/// <summary>One tile of a custom house design. X/Y are offsets relative to
/// the multi center; Z is absolute within the house (story 1 floor = 7,
/// story 2 = 27, ... — see CustomHousingEngine.LevelToZ). Visible mirrors the
/// Source-X component flag: fixture tiles (doors/containers) of a committed
/// design are invisible — the real items materialized on commit replace them
/// for rendering, walking and LOS.</summary>
public readonly record struct HouseDesignTile(ushort TileId, sbyte X, sbyte Y, sbyte Z, bool Visible = true);

/// <summary>0xBF sub 0x20 — switch the client's house-customization mode on
/// (flag 0x04) or off (flag 0x05) for the given foundation serial. While on,
/// the client opens its design UI and sends 0xD7 encoded design commands.</summary>
public sealed class PacketHouseCustomizationMode : PacketWriter
{
    private readonly uint _houseSerial;
    private readonly bool _begin;

    public PacketHouseCustomizationMode(uint houseSerial, bool begin) : base(0xBF)
    {
        _houseSerial = houseSerial;
        _begin = begin;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(17);
        buf.WriteUInt16(0x0020);
        buf.WriteUInt32(_houseSerial);
        buf.WriteByte(_begin ? (byte)0x04 : (byte)0x05);
        buf.WriteUInt16(0x0000);
        buf.WriteUInt16(0xFFFF);
        buf.WriteByte(0xFF);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>0xBF sub 0x1D — house design revision notice. A client whose
/// cached revision differs replies with 0xBF sub 0x1E (query design details),
/// which the server answers with a 0xD8 design stream.</summary>
public sealed class PacketHouseDesignVersion : PacketWriter
{
    private readonly uint _houseSerial;
    private readonly uint _revision;

    public PacketHouseDesignVersion(uint houseSerial, uint revision) : base(0xBF)
    {
        _houseSerial = houseSerial;
        _revision = revision;
    }

    public override PacketBuffer Build()
    {
        var buf = CreateVariable(13);
        buf.WriteUInt16(0x001D);
        buf.WriteUInt32(_houseSerial);
        buf.WriteUInt32(_revision);
        buf.WriteLengthAt(1);
        return buf;
    }
}

/// <summary>
/// 0xD8 — custom house design stream (server → client).
///
/// Layout (verified against the ClassicUO parser):
///   [0xD8][len:2][compression=0x03][enableResponse:1][serial:4][revision:4]
///   [tileCount:2][planeBytes:2][planeCount:1] then per plane:
///   [header:4][zlib-compressed tile data]
///
/// Plane header bit packing (dlen = raw byte length, clen = compressed):
///   bits 28-31 mode | 24-27 planeZ | 16-23 dlen low byte | 8-15 clen low byte
///   | 4-7 dlen high nibble | 0-3 clen high nibble
///
/// All planes are emitted in mode 0 (5 bytes per tile: tileId:2 BE, x:1,
/// y:1, z:1 — z explicit per tile), so the per-floor bitfield modes are not
/// needed; planeZ is left 0 and ignored by the client in this mode.
/// </summary>
public sealed class PacketHouseDesignDetailed : PacketWriter
{
    // dlen is a 12-bit field (max 4095) → at most 819 5-byte tiles per plane.
    private const int MaxTilesPerPlane = 750;

    private readonly uint _houseSerial;
    private readonly uint _revision;
    private readonly IReadOnlyList<HouseDesignTile> _tiles;

    public PacketHouseDesignDetailed(uint houseSerial, uint revision,
        IReadOnlyList<HouseDesignTile> tiles) : base(0xD8)
    {
        _houseSerial = houseSerial;
        _revision = revision;
        _tiles = tiles;
    }

    public override PacketBuffer Build()
    {
        var planes = new List<(byte[] Compressed, int RawLen)>();
        for (int start = 0; start < _tiles.Count; start += MaxTilesPerPlane)
        {
            int count = Math.Min(MaxTilesPerPlane, _tiles.Count - start);
            byte[] raw = new byte[count * 5];
            int o = 0;
            for (int i = 0; i < count; i++)
            {
                var t = _tiles[start + i];
                raw[o++] = (byte)(t.TileId >> 8);
                raw[o++] = (byte)t.TileId;
                raw[o++] = (byte)t.X;
                raw[o++] = (byte)t.Y;
                raw[o++] = (byte)t.Z;
            }
            planes.Add((ZlibUtil.Compress(raw), raw.Length));
        }

        int planeBytes = 0;
        foreach (var (compressed, _) in planes)
            planeBytes += 4 + compressed.Length;

        var buf = CreateVariable(17 + planeBytes);
        buf.WriteByte(0x03); // compression type: zlib planes
        buf.WriteByte(0x00); // enable response
        buf.WriteUInt32(_houseSerial);
        buf.WriteUInt32(_revision);
        buf.WriteUInt16((ushort)_tiles.Count);
        buf.WriteUInt16((ushort)planeBytes);
        buf.WriteByte((byte)planes.Count);

        foreach (var (compressed, rawLen) in planes)
        {
            int clen = compressed.Length;
            uint header = ((uint)(rawLen & 0xFF) << 16)
                | ((uint)(clen & 0xFF) << 8)
                | ((uint)((rawLen >> 8) & 0xF) << 4)
                | (uint)((clen >> 8) & 0xF);
            buf.WriteUInt32(header); // mode 0, planeZ 0 (z explicit per tile)
            buf.WriteBytes(compressed);
        }

        buf.WriteLengthAt(1);
        return buf;
    }
}
