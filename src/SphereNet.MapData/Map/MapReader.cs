using System.IO.MemoryMappedFiles;

namespace SphereNet.MapData.Map;

/// <summary>
/// Reads map0.mul — terrain data organized in 8x8 blocks.
/// Each block = 4 byte header + 64 cells * 3 bytes (tileId:2 + z:1) = 196 bytes.
///
/// Reads are OFFSET-BASED and hold no cursor, so any number of threads may read at
/// once. The previous implementation seeked a shared BinaryReader and then took 193
/// sequential reads from it: two threads doing that at the same time did not each get
/// their own block, they interleaved, and each came back with bytes from wherever the
/// other had left the file pointer. On a synthetic map with eight workers and 20,000
/// reads that produced roughly ten thousand wrong blocks and three thousand exceptions
/// (review finding B4).
///
/// It reached real gameplay because nothing keeps the map off worker threads - the
/// parallel NPC prestage resolves terrain - so a creature could path across a tile
/// whose height and type came from a different part of the world. A lock would also
/// have fixed the correctness and would have serialised every map read behind it.
///
/// The file is memory mapped, as the statics and the UOP terrain already are. The
/// offset-based reads that fixed B4 were positional file reads: one system call per
/// terrain lookup, and every lookup read and allocated a whole 196-byte block to
/// return one 3-byte cell. Line-of-sight walks a lookup per tile, so in a real-socket
/// load run (200 clients, 2,000 NPCs) those calls were about a fifth of the main
/// loop's time. A view read is a memory copy and needs no cursor either.
/// <see cref="GetCell"/> now reads only its own cell.
/// </summary>
public sealed class MapReader : IDisposable
{
    private readonly MemoryMappedFile? _mmf;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly long _length;
    private readonly int _width;
    private readonly int _height;
    private readonly int _blockWidth;
    private readonly int _blockHeight;

    private const int BlockDataSize = 196; // 4 + 64*3

    public int Width => _width;
    public int Height => _height;

    public MapReader(string filePath, int width, int height)
    {
        _width = width;
        _height = height;
        _blockWidth = width / MapBlock.BlockSize;
        _blockHeight = height / MapBlock.BlockSize;

        // A missing file still throws FileNotFoundException here, as before. An empty
        // file cannot be mapped; it simply has no blocks.
        _length = new FileInfo(filePath).Length;
        if (_length == 0)
            return;

        MemoryMappedFile? mmf = null;
        try
        {
            mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            _view = mmf.CreateViewAccessor(0, _length, MemoryMappedFileAccess.Read);
            _mmf = mmf;
        }
        catch
        {
            mmf?.Dispose();
            throw;
        }
    }

    /// <summary>USEMAPDIFFS patch blocks (mapdif), keyed by block number; they replace
    /// the file's own block.</summary>
    public Dictionary<int, MapBlock>? Diff { get; set; }

    public MapBlock ReadBlock(int blockX, int blockY)
    {
        if (blockX < 0 || blockX >= _blockWidth || blockY < 0 || blockY >= _blockHeight)
            return new MapBlock();
        if (Diff != null && Diff.TryGetValue(blockX * _blockHeight + blockY, out var patched))
            return patched;

        long offset = ((long)blockX * _blockHeight + blockY) * BlockDataSize;
        // A block the file does not hold in full (a truncated file) is not a block.
        if (_view == null || offset < 0 || offset + BlockDataSize > _length)
            return new MapBlock();

        var block = new MapBlock { Header = _view.ReadUInt32(offset) };
        long pos = offset + 4;
        for (int i = 0; i < MapBlock.CellCount; i++)
        {
            block.Cells[i] = new MapCell
            {
                TileId = _view.ReadUInt16(pos),
                Z = _view.ReadSByte(pos + 2),
            };
            pos += 3;
        }

        return block;
    }

    public MapCell GetCell(int x, int y)
    {
        // Negative coords must be rejected here: -3 / 8 truncates to block 0
        // (passing ReadBlock's bounds check) while -3 % 8 = -3 indexes the
        // cell array with a negative offset.
        if (x < 0 || y < 0 || x >= _width || y >= _height)
            return default;
        int bx = x / MapBlock.BlockSize;
        int by = y / MapBlock.BlockSize;
        if (bx >= _blockWidth || by >= _blockHeight)
            return default;
        int cx = x % MapBlock.BlockSize;
        int cy = y % MapBlock.BlockSize;
        if (Diff != null && Diff.TryGetValue(bx * _blockHeight + by, out var patched))
            return patched.GetCell(cx, cy);

        long offset = ((long)bx * _blockHeight + by) * BlockDataSize;
        // Same rule as ReadBlock: a cell of a block the file holds only in part reads
        // as empty, exactly as reading that block whole would.
        if (_view == null || offset + BlockDataSize > _length)
            return default;
        long pos = offset + 4 + (cy * MapBlock.BlockSize + cx) * 3;
        return new MapCell { TileId = _view.ReadUInt16(pos), Z = _view.ReadSByte(pos + 2) };
    }

    public void Dispose()
    {
        _view?.Dispose();
        _mmf?.Dispose();
    }
}
