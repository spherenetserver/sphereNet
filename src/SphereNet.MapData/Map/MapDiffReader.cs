using System.Buffers.Binary;

namespace SphereNet.MapData.Map;

/// <summary>
/// The legacy map patch files (Source-X CServerMapDiffCollection, USEMAPDIFFS):
/// mapdifl{n}.mul lists the terrain blocks that are replaced and mapdif{n}.mul holds
/// them (196 bytes each, as in map{n}.mul); stadifl{n}.mul lists the static blocks,
/// stadifi{n}.mul is their index (12 bytes: offset, length, extra) and stadif{n}.mul
/// their items (7 bytes each). A block number is blockX * blockHeight + blockY. A
/// client that reads these files sees the patched world, so the server has to read
/// the same one or the two disagree about every patched tile.
/// </summary>
public static class MapDiffReader
{
    private const int TerrainBlockSize = 196;
    private const int StaticIndexSize = 12;
    private const int StaticEntrySize = 7;

    public static Dictionary<int, MapBlock> LoadTerrain(string mulPath, int mapFile)
    {
        var result = new Dictionary<int, MapBlock>();
        string list = Path.Combine(mulPath, $"mapdifl{mapFile}.mul");
        string data = Path.Combine(mulPath, $"mapdif{mapFile}.mul");
        if (!File.Exists(list) || !File.Exists(data)) return result;

        byte[] ids = File.ReadAllBytes(list);
        byte[] blocks = File.ReadAllBytes(data);
        int count = Math.Min(ids.Length / 4, blocks.Length / TerrainBlockSize);
        for (int i = 0; i < count; i++)
        {
            int blockNum = BinaryPrimitives.ReadInt32LittleEndian(ids.AsSpan(i * 4));
            var raw = blocks.AsSpan(i * TerrainBlockSize, TerrainBlockSize);
            var block = new MapBlock { Header = BinaryPrimitives.ReadUInt32LittleEndian(raw) };
            for (int c = 0; c < MapBlock.CellCount; c++)
            {
                int at = 4 + c * 3;
                block.Cells[c] = new MapCell
                {
                    TileId = BinaryPrimitives.ReadUInt16LittleEndian(raw[at..]),
                    Z = (sbyte)raw[at + 2],
                };
            }
            result[blockNum] = block; // a later entry for the same block wins
        }
        return result;
    }

    public static Dictionary<int, StaticItem[]> LoadStatics(string mulPath, int mapFile)
    {
        var result = new Dictionary<int, StaticItem[]>();
        string list = Path.Combine(mulPath, $"stadifl{mapFile}.mul");
        string index = Path.Combine(mulPath, $"stadifi{mapFile}.mul");
        string data = Path.Combine(mulPath, $"stadif{mapFile}.mul");
        if (!File.Exists(list) || !File.Exists(index) || !File.Exists(data)) return result;

        byte[] ids = File.ReadAllBytes(list);
        byte[] idx = File.ReadAllBytes(index);
        byte[] items = File.ReadAllBytes(data);
        int count = Math.Min(ids.Length / 4, idx.Length / StaticIndexSize);
        for (int i = 0; i < count; i++)
        {
            int blockNum = BinaryPrimitives.ReadInt32LittleEndian(ids.AsSpan(i * 4));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(i * StaticIndexSize));
            int length = BinaryPrimitives.ReadInt32LittleEndian(idx.AsSpan(i * StaticIndexSize + 4));
            if (offset < 0 || length <= 0 || (long)offset + length > items.Length)
            {
                result[blockNum] = []; // a patch that empties the block
                continue;
            }
            int n = length / StaticEntrySize;
            var arr = new StaticItem[n];
            for (int k = 0; k < n; k++)
            {
                var e = items.AsSpan(offset + k * StaticEntrySize, StaticEntrySize);
                arr[k] = new StaticItem
                {
                    TileId = BinaryPrimitives.ReadUInt16LittleEndian(e),
                    XOffset = e[2],
                    YOffset = e[3],
                    Z = (sbyte)e[4],
                    Hue = BinaryPrimitives.ReadUInt16LittleEndian(e[5..]),
                };
            }
            result[blockNum] = arr;
        }
        return result;
    }
}
