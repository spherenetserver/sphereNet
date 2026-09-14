using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

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
    // The file HANDLES, not the streams. FileStream is not thread-safe, and
    // .SafeFileHandle is not a plain accessor: reading it flushes the stream and
    // re-seeks the handle to the stream's position, so calling it per read from the
    // parallel prestage path mutated shared state on every read. That is what the
    // intermittent IOException ("invalid parameter") on the handle was - the
    // positional reads themselves were already correct (review finding B4).
    // RandomAccess on a handle carries no cursor and is safe from many threads,
    // which is how MapReader has always done it.
    private readonly SafeFileHandle _idxHandle;
    private readonly SafeFileHandle _dataHandle;
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

    // Read once, for the same reason.
    private readonly long _idxLength;
    private readonly long _dataLength;

    public MultiReader(string idxPath, string dataPath)
    {
        _idxHandle = File.OpenHandle(idxPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            _dataHandle = File.OpenHandle(dataPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch
        {
            _idxHandle.Dispose();
            throw;
        }

        _idxLength = RandomAccess.GetLength(_idxHandle);
        _dataLength = RandomAccess.GetLength(_dataHandle);
        _componentSize = DetectComponentSize();
    }

    /// <summary>Decide 12- vs 16-byte records from the index. High Seas files have many
    /// block lengths divisible by 16 but not 12 (e.g. 608 % 16 == 0, 608 % 12 == 8), and
    /// original files the reverse. When every length is divisible by both, fall back to
    /// which interpretation yields plausible (small) component offsets.</summary>
    private int DetectComponentSize()
    {
        int entryCount = (int)(_idxLength / IdxEntrySize);
        int votes16 = 0, votes12 = 0, firstNonEmpty = -1;

        Span<byte> entry = stackalloc byte[IdxEntrySize];
        for (int id = 0; id < entryCount; id++)
        {
            if (!ReadAt(_idxHandle, (long)id * IdxEntrySize, entry))
                continue;
            int off = BinaryPrimitives.ReadInt32LittleEndian(entry);
            int len = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
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
        Span<byte> entry = stackalloc byte[IdxEntrySize];
        if (!ReadAt(_idxHandle, (long)multiId * IdxEntrySize, entry))
            return 0;
        int off = BinaryPrimitives.ReadInt32LittleEndian(entry);
        int len = BinaryPrimitives.ReadInt32LittleEndian(entry[4..]);
        if (off < 0 || len <= 0)
            return 0;

        int count = len / componentSize;
        int plausible = 0;
        Span<byte> rec = stackalloc byte[HighSeasComponentSize];
        for (int i = 0; i < count; i++)
        {
            var record = rec[..componentSize];
            if (!ReadAt(_dataHandle, (long)off + (long)i * componentSize, record))
                break;
            short dx = BinaryPrimitives.ReadInt16LittleEndian(record[2..]);
            short dy = BinaryPrimitives.ReadInt16LittleEndian(record[4..]);
            short dz = BinaryPrimitives.ReadInt16LittleEndian(record[6..]);
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
    /// yet (review finding B4, same class).
    ///
    /// The handle is passed in rather than fetched from the stream. Reading
    /// FileStream.SafeFileHandle flushes the stream and re-seeks the handle to the
    /// stream's position, so fetching it per read - which this method used to do -
    /// put the shared mutation straight back on the concurrent path. It surfaced as
    /// an intermittent IOException on the handle instead of as wrong data, which is
    /// the harder thing to spot.</summary>
    private static bool ReadAt(SafeFileHandle handle, long offset, Span<byte> into)
    {
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
        if (idxOffset + IdxEntrySize > _idxLength)
            return null;

        Span<byte> idx = stackalloc byte[IdxEntrySize];
        if (!ReadAt(_idxHandle, idxOffset, idx))
            return null;
        int dataOffset = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(idx);
        int dataLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(idx[4..]);
        // idx[8..] is "extra" — skipped.

        if (dataOffset < 0 || dataLength <= 0)
            return null;
        if ((long)dataOffset + dataLength > _dataLength)
            return null;

        int count = dataLength / _componentSize;
        var components = new MultiComponent[count];

        byte[] raw = new byte[dataLength];
        if (!ReadAt(_dataHandle, dataOffset, raw))
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
        _idxHandle.Dispose();
        _dataHandle.Dispose();
    }
}
