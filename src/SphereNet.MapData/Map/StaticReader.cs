using System.IO.MemoryMappedFiles;
using System.Collections.Concurrent;

namespace SphereNet.MapData.Map;

/// <summary>
/// Reads statics0.mul + staidx0.mul — static items placed on the map.
/// staidx0.mul: index file — per-block lookup (offset + length).
/// statics0.mul: data file — static items (7 bytes each).
/// Uses MemoryMappedFile so the OS pages out unused regions automatically,
/// same pattern as UopMapReader (saves significant RAM vs. BinaryReader).
/// </summary>
public sealed class StaticReader : IDisposable
{
    private readonly MemoryMappedFile _idxMmf;
    private readonly MemoryMappedViewAccessor _idxView;
    private readonly long _idxLength;
    private readonly MemoryMappedFile _dataMmf;
    private readonly MemoryMappedViewAccessor _dataView;
    private readonly long _dataLength;

    private readonly int _blockWidth;
    private readonly int _blockHeight;
    private readonly ConcurrentDictionary<long, StaticItem[]> _blockCache = new();

    private const int StaticEntrySize = 7; // tileId:2 + xOff:1 + yOff:1 + z:1 + hue:2
    private const int IdxEntrySize = 12;   // offset:4 + length:4 + extra:4

    public StaticReader(string idxPath, string dataPath, int mapWidth, int mapHeight)
    {
        _blockWidth = mapWidth / MapBlock.BlockSize;
        _blockHeight = mapHeight / MapBlock.BlockSize;

        // Four acquisitions in a row, and the second file is the one that fails: a
        // statics data file that is missing, empty or truncated. The index mapping was
        // already open by then and the constructor throws with no object to dispose, so
        // it stayed open for the life of the process - and on Windows a mapped file
        // cannot be deleted or replaced, which is what an operator meets when they try
        // to swap a bad map file out (review work item D06).
        MemoryMappedFile? idxMmf = null;
        MemoryMappedViewAccessor? idxView = null;
        MemoryMappedFile? dataMmf = null;
        MemoryMappedViewAccessor? dataView = null;
        try
        {
            _idxLength = new FileInfo(idxPath).Length;
            idxMmf = MemoryMappedFile.CreateFromFile(idxPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            idxView = idxMmf.CreateViewAccessor(0, _idxLength, MemoryMappedFileAccess.Read);

            _dataLength = new FileInfo(dataPath).Length;
            dataMmf = MemoryMappedFile.CreateFromFile(dataPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            dataView = dataMmf.CreateViewAccessor(0, _dataLength, MemoryMappedFileAccess.Read);

            _idxMmf = idxMmf; _idxView = idxView;
            _dataMmf = dataMmf; _dataView = dataView;
        }
        catch
        {
            dataView?.Dispose(); dataMmf?.Dispose();
            idxView?.Dispose(); idxMmf?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Read all static items in an 8x8 block.
    /// </summary>
    /// <summary>USEMAPDIFFS: put the patched static blocks (stadif) in front of the file's
    /// own, keyed by block number.</summary>
    public void ApplyDiff(Dictionary<int, StaticItem[]> diff)
    {
        foreach (var (blockNum, items) in diff)
            _blockCache[MakeBlockKey(blockNum / _blockHeight, blockNum % _blockHeight)] = items;
    }

    public StaticItem[] ReadBlock(int blockX, int blockY)
    {
        if (blockX < 0 || blockX >= _blockWidth || blockY < 0 || blockY >= _blockHeight)
            return [];

        long cacheKey = MakeBlockKey(blockX, blockY);
        return _blockCache.GetOrAdd(cacheKey, _ => ReadBlockUncached(blockX, blockY));
    }

    private StaticItem[] ReadBlockUncached(int blockX, int blockY)
    {
        if (blockX < 0 || blockX >= _blockWidth || blockY < 0 || blockY >= _blockHeight)
            return [];

        long idxOffset = ((long)blockX * _blockHeight + blockY) * IdxEntrySize;
        if (idxOffset + IdxEntrySize > _idxLength)
            return [];

        int dataOffset = _idxView.ReadInt32(idxOffset);
        int dataLength = _idxView.ReadInt32(idxOffset + 4);
        // idxOffset + 8 is "extra" — skipped.

        if (dataOffset < 0 || dataLength <= 0)
            return [];
        if ((long)dataOffset + dataLength > _dataLength)
            return [];

        int count = dataLength / StaticEntrySize;
        var items = new StaticItem[count];

        long pos = dataOffset;
        for (int i = 0; i < count; i++)
        {
            items[i] = new StaticItem
            {
                TileId = _dataView.ReadUInt16(pos),
                XOffset = _dataView.ReadByte(pos + 2),
                YOffset = _dataView.ReadByte(pos + 3),
                Z = (sbyte)_dataView.ReadByte(pos + 4),
                Hue = _dataView.ReadUInt16(pos + 5)
            };
            pos += StaticEntrySize;
        }

        return items;
    }

    public void ForEachStatic(int x, int y, Action<StaticItem> action)
    {
        int bx = x / MapBlock.BlockSize;
        int by = y / MapBlock.BlockSize;
        int offX = x % MapBlock.BlockSize;
        int offY = y % MapBlock.BlockSize;

        var allItems = ReadBlock(bx, by);
        foreach (var item in allItems)
        {
            if (item.XOffset == offX && item.YOffset == offY)
                action(item);
        }
    }

    public bool AnyStatic(int x, int y, Func<StaticItem, bool> predicate)
    {
        int bx = x / MapBlock.BlockSize;
        int by = y / MapBlock.BlockSize;
        int offX = x % MapBlock.BlockSize;
        int offY = y % MapBlock.BlockSize;

        var allItems = ReadBlock(bx, by);
        foreach (var item in allItems)
        {
            if (item.XOffset == offX && item.YOffset == offY && predicate(item))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Get static items at a specific world coordinate.
    /// </summary>
    public StaticItem[] GetStatics(int x, int y)
    {
        int bx = x / MapBlock.BlockSize;
        int by = y / MapBlock.BlockSize;
        int offX = x % MapBlock.BlockSize;
        int offY = y % MapBlock.BlockSize;

        var allItems = ReadBlock(bx, by);
        var filtered = new List<StaticItem>();
        foreach (var item in allItems)
        {
            if (item.XOffset == offX && item.YOffset == offY)
                filtered.Add(item);
        }
        return filtered.ToArray();
    }

    private static long MakeBlockKey(int blockX, int blockY) => ((long)blockX << 32) | (uint)blockY;

    public void Dispose()
    {
        _idxView.Dispose();
        _idxMmf.Dispose();
        _dataView.Dispose();
        _dataMmf.Dispose();
        _blockCache.Clear();
    }
}
