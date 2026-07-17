namespace SphereNet.MapData.Multi;

/// <summary>
/// Reads multi.mul + multi.idx — house/ship structure definitions.
/// multi.idx: index file — per-multi lookup (offset + length + extra).
/// multi.mul: data file — multi components. Two on-disk layouts exist and are
/// AUTO-DETECTED here: the original 12-byte record (pre-High Seas) and the 16-byte
/// High Seas+ record (adds a trailing ship-access dword). Reading a 16-byte file as
/// 12-byte shifts every component offset and massively inflates footprints, which then
/// fails the map-bounds check in PlaceHouse/PlaceShip ("Cannot place here"). Mirrors
/// Source-X CUOInstall::DetectMulVersions / CServerMap CUOMulti::Load.
/// </summary>
public sealed class MultiReader : IDisposable
{
    private readonly BinaryReader _idxReader;
    private readonly BinaryReader _dataReader;
    private readonly Dictionary<int, MultiDef> _cache = [];

    private const int OriginalComponentSize = 12; // tileId:2 + dx:2 + dy:2 + dz:2 + visible:4
    private const int HighSeasComponentSize = 16; // ... + shipAccess:4
    private const int IdxEntrySize = 12;          // offset:4 + length:4 + extra:4

    // A real multi component offset never approaches this; a wrong record size scrambles
    // deltas into thousands, so this cleanly separates the two interpretations.
    private const int PlausibleOffsetLimit = 256;

    private readonly int _componentSize;

    /// <summary>The detected on-disk component-record size (12 = original, 16 = High Seas).</summary>
    public int ComponentSize => _componentSize;

    public MultiReader(string idxPath, string dataPath)
    {
        var idxStream = new FileStream(idxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _idxReader = new BinaryReader(idxStream);

        try
        {
            var dataStream = new FileStream(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            _dataReader = new BinaryReader(dataStream);
        }
        catch
        {
            _idxReader.Dispose();
            throw;
        }

        _componentSize = DetectComponentSize();
    }

    /// <summary>Decide 12- vs 16-byte records from the index. High Seas files have many
    /// block lengths divisible by 16 but not 12 (e.g. 608 % 16 == 0, 608 % 12 == 8), and
    /// original files the reverse. When every length is divisible by both, fall back to
    /// which interpretation yields plausible (small) component offsets.</summary>
    private int DetectComponentSize()
    {
        int entryCount = (int)(_idxReader.BaseStream.Length / IdxEntrySize);
        int votes16 = 0, votes12 = 0, firstNonEmpty = -1;

        for (int id = 0; id < entryCount; id++)
        {
            _idxReader.BaseStream.Seek((long)id * IdxEntrySize, SeekOrigin.Begin);
            int off = _idxReader.ReadInt32();
            int len = _idxReader.ReadInt32();
            _idxReader.ReadInt32(); // extra
            if (off < 0 || len <= 0)
                continue;
            if (firstNonEmpty < 0)
                firstNonEmpty = id;

            bool d12 = len % OriginalComponentSize == 0;
            bool d16 = len % HighSeasComponentSize == 0;
            if (d16 && !d12) votes16++;
            else if (d12 && !d16) votes12++;
        }

        if (votes16 > votes12) return HighSeasComponentSize;
        if (votes12 > votes16) return OriginalComponentSize;

        // Fully ambiguous (all lengths divisible by both, or an empty index): pick the
        // interpretation whose first record has more plausible offsets, defaulting to
        // the legacy 12-byte layout.
        if (firstNonEmpty >= 0 &&
            CountPlausibleOffsets(firstNonEmpty, HighSeasComponentSize) >
            CountPlausibleOffsets(firstNonEmpty, OriginalComponentSize))
            return HighSeasComponentSize;
        return OriginalComponentSize;
    }

    // Read one record with the given component size and count components whose deltas
    // land inside a sane multi footprint. The correct size scores higher.
    private int CountPlausibleOffsets(int multiId, int componentSize)
    {
        _idxReader.BaseStream.Seek((long)multiId * IdxEntrySize, SeekOrigin.Begin);
        int off = _idxReader.ReadInt32();
        int len = _idxReader.ReadInt32();
        if (off < 0 || len <= 0)
            return 0;

        int count = len / componentSize;
        int plausible = 0;
        _dataReader.BaseStream.Seek(off, SeekOrigin.Begin);
        for (int i = 0; i < count; i++)
        {
            _dataReader.ReadUInt16();          // tileId
            short dx = _dataReader.ReadInt16();
            short dy = _dataReader.ReadInt16();
            short dz = _dataReader.ReadInt16();
            _dataReader.ReadUInt32();          // visible
            if (componentSize == HighSeasComponentSize)
                _dataReader.ReadUInt32();      // shipAccess
            if (Math.Abs((int)dx) <= PlausibleOffsetLimit &&
                Math.Abs((int)dy) <= PlausibleOffsetLimit &&
                Math.Abs((int)dz) <= PlausibleOffsetLimit)
                plausible++;
        }
        return plausible;
    }

    public MultiDef? GetMulti(int multiId)
    {
        if (_cache.TryGetValue(multiId, out var cached))
            return cached;

        var multi = ReadMulti(multiId);
        if (multi != null)
            _cache[multiId] = multi;

        return multi;
    }

    private MultiDef? ReadMulti(int multiId)
    {
        long idxOffset = (long)multiId * IdxEntrySize;
        if (idxOffset + IdxEntrySize > _idxReader.BaseStream.Length)
            return null;

        _idxReader.BaseStream.Seek(idxOffset, SeekOrigin.Begin);
        int dataOffset = _idxReader.ReadInt32();
        int dataLength = _idxReader.ReadInt32();
        _idxReader.ReadInt32(); // extra

        if (dataOffset < 0 || dataLength <= 0)
            return null;

        int count = dataLength / _componentSize;
        var components = new MultiComponent[count];

        _dataReader.BaseStream.Seek(dataOffset, SeekOrigin.Begin);
        for (int i = 0; i < count; i++)
        {
            ushort tileId = _dataReader.ReadUInt16();
            short dx = _dataReader.ReadInt16();
            short dy = _dataReader.ReadInt16();
            short dz = _dataReader.ReadInt16();
            uint flags = _dataReader.ReadUInt32();
            uint shipAccess = _componentSize == HighSeasComponentSize ? _dataReader.ReadUInt32() : 0u;

            components[i] = new MultiComponent
            {
                TileId = tileId,
                XOffset = dx,
                YOffset = dy,
                ZOffset = dz,
                Flags = flags,
                ShipAccess = shipAccess
            };
        }

        return new MultiDef(multiId, components);
    }

    public void Dispose()
    {
        _idxReader.Dispose();
        _dataReader.Dispose();
    }
}
