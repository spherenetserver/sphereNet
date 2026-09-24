using System.Buffers.Binary;
using Microsoft.Win32.SafeHandles;

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
/// have fixed the correctness and would have serialised every map read behind it;
/// positional reads let the file system do what it is already good at.
/// </summary>
public sealed class MapReader : IDisposable
{
    private readonly SafeFileHandle _handle;
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

        _handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        _length = RandomAccess.GetLength(_handle);
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
        if (offset < 0 || offset + BlockDataSize > _length)
            return new MapBlock();

        Span<byte> raw = stackalloc byte[BlockDataSize];
        // A positional read may still come back short at a truncated file or across a
        // boundary the OS chooses to split; a partial block is not a block.
        int read = 0;
        while (read < BlockDataSize)
        {
            int n = RandomAccess.Read(_handle, raw[read..], offset + read);
            if (n <= 0) return new MapBlock();
            read += n;
        }

        var block = new MapBlock { Header = BinaryPrimitives.ReadUInt32LittleEndian(raw) };
        for (int i = 0; i < MapBlock.CellCount; i++)
        {
            int at = 4 + i * 3;
            block.Cells[i] = new MapCell
            {
                TileId = BinaryPrimitives.ReadUInt16LittleEndian(raw[at..]),
                Z = (sbyte)raw[at + 2],
            };
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
        var block = ReadBlock(bx, by);
        return block.GetCell(x % MapBlock.BlockSize, y % MapBlock.BlockSize);
    }

    public void Dispose() => _handle.Dispose();
}
