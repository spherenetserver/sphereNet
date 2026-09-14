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
    /// <summary>Concurrent because GetMulti is reached from the parallel NPC prestage
    /// through WalkCheck: a plain dictionary written from two threads at once can
    /// corrupt its own buckets, which is a worse failure than the stale read it looks
    /// like.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, MultiDef> _cache = new();

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

    /// <summary>Read a run of bytes from a file by POSITION, holding no cursor.
    ///
    /// The reads below used to be a seek on a shared BinaryReader followed by a
    /// sequence of small reads from it - the same shape the classic map reader had, and
    /// the same failure: two threads interleave and each parses the other's bytes.
    /// GetMulti caches, so it mostly happens on a multi's FIRST sighting, which is
    /// exactly when a creature walks onto a ship or into a house nobody has touched
    /// yet (review finding B4, same class).</summary>
    private static bool ReadAt(BinaryReader reader, long offset, Span<byte> into)
    {
        var handle = (reader.BaseStream as FileStream)?.SafeFileHandle;
        if (handle == null) return false;
        int read = 0;
        while (read < into.Length)
        {
            int n = RandomAccess.Read(handle, into[read..], offset + read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private MultiDef? ReadMulti(int multiId)
    {
        long idxOffset = (long)multiId * IdxEntrySize;
        if (idxOffset + IdxEntrySize > _idxReader.BaseStream.Length)
            return null;

        Span<byte> idx = stackalloc byte[IdxEntrySize];
        if (!ReadAt(_idxReader, idxOffset, idx))
            return null;
        int dataOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(idx);
        int dataLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(idx[4..]);
        // idx[8..] is "extra" — skipped.

        if (dataOffset < 0 || dataLength <= 0)
            return null;
        if ((long)dataOffset + dataLength > _dataReader.BaseStream.Length)
            return null;

        int count = dataLength / _componentSize;
        var components = new MultiComponent[count];

        byte[] raw = new byte[dataLength];
        if (!ReadAt(_dataReader, dataOffset, raw))
            return null;

        for (int i = 0; i < count; i++)
        {
            var span = raw.AsSpan(i * _componentSize);
            ushort tileId = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(span);
            short dx = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(span[2..]);
            short dy = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(span[4..]);
            short dz = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(span[6..]);
            uint flags = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            uint shipAccess = _componentSize == HighSeasComponentSize
                ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(span[12..])
                : 0u;

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
