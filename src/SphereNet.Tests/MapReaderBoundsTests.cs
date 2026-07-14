using SphereNet.MapData.Map;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Regression: coordinates outside the map must return a default cell instead of
/// crashing. Negative x/y used to slip past ReadBlock's bounds check (-3 / 8
/// truncates to block 0) and index MapBlock.Cells with a negative offset
/// (-3 % 8 = -3), throwing IndexOutOfRangeException — observed live when a
/// spawner near the map edge rolled an off-map candidate in FindSpawnPosition.
/// </summary>
public class MapReaderBoundsTests
{
    /// <summary>Write a minimal 8x8 (single-block) map0.mul: 4-byte header + 64 cells.</summary>
    private static string WriteSingleBlockMul(ushort tileId, sbyte z)
    {
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_test_map_{Guid.NewGuid():N}.mul");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(0u); // block header
        for (int i = 0; i < MapBlock.CellCount; i++)
        {
            bw.Write(tileId);
            bw.Write(z);
        }
        return path;
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(-3, 4)]   // -3 / 8 == 0: passes the block bounds check, cell index goes negative
    [InlineData(0, -3)]
    [InlineData(-9, -1)]
    [InlineData(8, 0)]    // past the right edge
    [InlineData(0, 8)]    // past the bottom edge
    public void GetCell_OutsideMap_ReturnsDefault(int x, int y)
    {
        string path = WriteSingleBlockMul(tileId: 42, z: 5);
        try
        {
            using var reader = new MapReader(path, 8, 8);
            var cell = reader.GetCell(x, y);
            Assert.Equal(0, cell.TileId);
            Assert.Equal(0, cell.Z);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void GetCell_InsideMap_ReturnsWrittenCell()
    {
        string path = WriteSingleBlockMul(tileId: 42, z: 5);
        try
        {
            using var reader = new MapReader(path, 8, 8);
            var cell = reader.GetCell(3, 4);
            Assert.Equal(42, cell.TileId);
            Assert.Equal(5, cell.Z);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
