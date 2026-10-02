using System.Reflection;
using SphereNet.MapData.Map;

namespace SphereNet.Tests;

/// <summary>
/// GetCell reads one terrain cell in place (no whole-block read, no allocation). It
/// must answer exactly what reading the block and picking the cell answers - for every
/// cell, for a block a mapdif patch replaced, for a block the file holds only in part,
/// and for an empty file - on both the MUL and the UOP reader.
/// </summary>
public sealed class MapCellReadTests : IDisposable
{
    private const int Blocks = 4; // 4x4 blocks = 32x32 tiles
    private readonly string _dir;

    public MapCellReadTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_cellread_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Every cell distinct: tile = block*64 + cell + 1, z = cell - 32.</summary>
    private static byte[] MapBytes(int blockCount)
    {
        var data = new byte[blockCount * 196];
        for (int b = 0; b < blockCount; b++)
        {
            BitConverter.GetBytes((uint)(b + 1)).CopyTo(data, b * 196);
            for (int c = 0; c < 64; c++)
            {
                int at = b * 196 + 4 + c * 3;
                BitConverter.GetBytes((ushort)(b * 64 + c + 1)).CopyTo(data, at);
                data[at + 2] = unchecked((byte)(sbyte)(c - 32));
            }
        }
        return data;
    }

    private static void AssertCellsMatchBlocks(Func<int, int, MapCell> getCell, Func<int, int, MapBlock> readBlock)
    {
        for (int y = 0; y < Blocks * 8; y++)
        for (int x = 0; x < Blocks * 8; x++)
        {
            var expected = readBlock(x / 8, y / 8).GetCell(x % 8, y % 8);
            var actual = getCell(x, y);
            Assert.Equal(expected.TileId, actual.TileId);
            Assert.Equal(expected.Z, actual.Z);
        }
    }

    [Fact]
    public void MulCellReadsMatchBlockReads()
    {
        string path = Path.Combine(_dir, "map0.mul");
        File.WriteAllBytes(path, MapBytes(Blocks * Blocks));
        using var reader = new MapReader(path, Blocks * 8, Blocks * 8);

        AssertCellsMatchBlocks(reader.GetCell, reader.ReadBlock);
        // Spot value: block (1,2) is block number 1*4+2 = 6, cell (3,5) is index 43.
        var cell = reader.GetCell(8 + 3, 16 + 5);
        Assert.Equal(6 * 64 + 43 + 1, cell.TileId);
        Assert.Equal(43 - 32, cell.Z);
        Assert.Equal(default, reader.GetCell(Blocks * 8, 0));
        Assert.Equal(default, reader.GetCell(-1, 0));
    }

    [Fact]
    public void MulCellReadHonoursMapDiffPatches()
    {
        string path = Path.Combine(_dir, "map0.mul");
        File.WriteAllBytes(path, MapBytes(Blocks * Blocks));
        using var reader = new MapReader(path, Blocks * 8, Blocks * 8);
        var patch = new MapBlock();
        for (int i = 0; i < MapBlock.CellCount; i++)
            patch.Cells[i] = new MapCell { TileId = 0x1234, Z = 7 };
        reader.Diff = new Dictionary<int, MapBlock> { [1 * Blocks + 2] = patch };

        AssertCellsMatchBlocks(reader.GetCell, reader.ReadBlock);
        Assert.Equal(0x1234, reader.GetCell(8 + 1, 16 + 1).TileId);
    }

    [Fact]
    public void MulCellOfAPartlyStoredBlockReadsEmptyLikeTheBlock()
    {
        // The last block is cut short: as a block it is empty, so its cells are too.
        string path = Path.Combine(_dir, "map0.mul");
        var bytes = MapBytes(Blocks * Blocks);
        File.WriteAllBytes(path, bytes.AsSpan(0, bytes.Length - 50).ToArray());
        using var reader = new MapReader(path, Blocks * 8, Blocks * 8);

        AssertCellsMatchBlocks(reader.GetCell, reader.ReadBlock);
        Assert.Equal(default, reader.GetCell(Blocks * 8 - 1, Blocks * 8 - 1));
    }

    [Fact]
    public void AnEmptyMulFileHasNoTerrainAndIsReleasedOnDispose()
    {
        string path = Path.Combine(_dir, "map0.mul");
        File.WriteAllBytes(path, []);
        using (var reader = new MapReader(path, Blocks * 8, Blocks * 8))
        {
            Assert.Equal(default, reader.GetCell(3, 3));
            Assert.Equal(0u, reader.ReadBlock(0, 0).Header);
        }
        File.Delete(path);
    }

    [Fact]
    public void AMissingMulFileStillThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() => new MapReader(Path.Combine(_dir, "nope.mul"), 64, 64));
    }

    [Fact]
    public void TheMulFileIsReleasedOnDispose()
    {
        string path = Path.Combine(_dir, "map0.mul");
        File.WriteAllBytes(path, MapBytes(Blocks * Blocks));
        using (var reader = new MapReader(path, Blocks * 8, Blocks * 8))
            Assert.NotEqual(default, reader.GetCell(1, 1));
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void UopCellReadsMatchBlockReads()
    {
        string path = WriteUop(MapBytes(Blocks * Blocks));
        using var reader = new UopMapReader(path, Blocks * 8, Blocks * 8);
        AssertCellsMatchBlocks(reader.GetCell, reader.ReadBlock);

        var patch = new MapBlock();
        for (int i = 0; i < MapBlock.CellCount; i++)
            patch.Cells[i] = new MapCell { TileId = 0x0BEE, Z = -5 };
        reader.Diff = new Dictionary<int, MapBlock> { [0] = patch };
        AssertCellsMatchBlocks(reader.GetCell, reader.ReadBlock);
        Assert.Equal(0x0BEE, reader.GetCell(2, 2).TileId);
    }

    /// <summary>A one-entry, uncompressed UOP container holding <paramref name="mul"/>
    /// as map0's first chunk.</summary>
    private string WriteUop(byte[] mul)
    {
        string path = Path.Combine(_dir, "map0LegacyMUL.uop");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        w.Write(0x0050594Du);      // "MYP"
        w.Write(5u);               // version
        w.Write(0u);               // timestamp
        w.Write(28L);              // first block offset
        w.Write(100u);             // block size
        w.Write(1);                // file count
        w.Write(1);                // files in this block
        w.Write(0L);               // no next block
        long dataOffset = fs.Position + 34;
        w.Write(dataOffset);       // file offset
        w.Write(0);                // header length
        w.Write(mul.Length);       // compressed length
        w.Write(mul.Length);       // decompressed length
        w.Write(HashOf("build/map0legacymul/00000000.dat"));
        w.Write(0u);               // data hash
        w.Write((short)0);         // stored, not compressed
        w.Write(mul);
        return path;
    }

    private static ulong HashOf(string s)
    {
        var method = typeof(UopMapReader).GetMethod("CreateHash", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (ulong)method.Invoke(null, [s])!;
    }
}
