using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using SphereNet.MapData.Map;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What a walk across the whole map costs the statics cache (review work item D06).
///
/// The readers are memory-mapped so the OS can page out map regions nobody is looking
/// at. The statics block cache sits in front of that and is a plain dictionary with no
/// eviction, so every block a player has ever walked past stays in managed memory for
/// the life of the process — which is the one thing the mapping was chosen to avoid.
///
/// The question the review asks is whether that is unbounded growth or a natural fill
/// bounded by the map. It is the second: the cache cannot exceed one entry per block.
/// That bound is the number worth writing down, because on a full-size map it is
/// 393,216 blocks, and "the whole statics file, in managed memory, eventually" is a
/// different claim from "200 MB".
/// </summary>
public sealed class StaticCacheGrowthTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    private const int MapSize = 1024;                       // tiles
    private const int Blocks = MapSize / 8;                 // per side
    private const int ItemsPerBlock = 4;
    /// <summary>Blocks on a full-size map0 (6144x4096): the bound this measurement is
    /// extrapolated to.</summary>
    private const int RealMapBlocks = (6144 / 8) * (4096 / 8);

    public StaticCacheGrowthTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_statcache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (string Idx, string Data) BuildStatics()
    {
        string idxPath = Path.Combine(_dir, "staidx0.mul");
        string dataPath = Path.Combine(_dir, "statics0.mul");

        int blockCount = Blocks * Blocks;
        var data = new byte[blockCount * ItemsPerBlock * 7];
        var idx = new byte[blockCount * 12];
        for (int b = 0; b < blockCount; b++)
        {
            int at = b * ItemsPerBlock * 7;
            for (int i = 0; i < ItemsPerBlock; i++)
            {
                int o = at + i * 7;
                BitConverter.GetBytes((ushort)(0x1000 + i)).CopyTo(data, o);
                data[o + 2] = (byte)i; data[o + 3] = (byte)i; data[o + 4] = 0;
            }
            BitConverter.GetBytes(at).CopyTo(idx, b * 12);
            BitConverter.GetBytes(ItemsPerBlock * 7).CopyTo(idx, b * 12 + 4);
        }
        File.WriteAllBytes(dataPath, data);
        File.WriteAllBytes(idxPath, idx);
        return (idxPath, dataPath);
    }

    private static int CacheCount(StaticReader reader)
    {
        var field = typeof(StaticReader).GetField("_blockCache",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return ((ConcurrentDictionary<long, StaticItem[]>)field.GetValue(reader)!).Count;
    }

    [Fact]
    public void AWalkOverTheWholeMapFillsTheCacheOncePerBlockAndNoFurther()
    {
        var (idx, data) = BuildStatics();
        using var reader = new StaticReader(idx, data, MapSize, MapSize);

        long before = GC.GetTotalMemory(forceFullCollection: true);

        for (int bx = 0; bx < Blocks; bx++)
            for (int by = 0; by < Blocks; by++)
                Assert.Equal(ItemsPerBlock, reader.ReadBlock(bx, by).Length);

        long afterFirst = GC.GetTotalMemory(forceFullCollection: true);
        int countAfterFirst = CacheCount(reader);

        // The same route again: every block is a hit, so nothing new may be retained.
        for (int bx = 0; bx < Blocks; bx++)
            for (int by = 0; by < Blocks; by++)
                reader.ReadBlock(bx, by);

        long afterSecond = GC.GetTotalMemory(forceFullCollection: true);
        int countAfterSecond = CacheCount(reader);

        long firstWalkBytes = afterFirst - before;
        double perBlock = (double)firstWalkBytes / countAfterFirst;
        _out.WriteLine(
            $"{Blocks}x{Blocks} blocks, {ItemsPerBlock} statics each: cache {countAfterFirst} entries, " +
            $"managed heap +{firstWalkBytes / 1024} KB on the first walk " +
            $"({perBlock:F0} bytes/block), +{(afterSecond - afterFirst) / 1024} KB on the second");
        _out.WriteLine(
            $"extrapolated to a full 6144x4096 map ({RealMapBlocks} blocks): " +
            $"{perBlock * RealMapBlocks / (1024 * 1024):F0} MB held after walking all of it");

        // Bounded by the map, not unbounded: one entry per block, and a second pass
        // over the same ground adds nothing.
        Assert.Equal(Blocks * Blocks, countAfterFirst);
        Assert.Equal(countAfterFirst, countAfterSecond);
        Assert.True(afterSecond - afterFirst < firstWalkBytes / 4,
            "a second walk over the same blocks kept allocating, so the cache is not holding them");
    }

    [Fact]
    public void AnEmptyBlockIsCachedToo()
    {
        // A block with no statics still costs an entry, which is why the bound is the
        // number of BLOCKS rather than the number of statics: an empty map region is
        // not free once it has been walked.
        string idxPath = Path.Combine(_dir, "staidx0.mul");
        string dataPath = Path.Combine(_dir, "statics0.mul");
        File.WriteAllBytes(dataPath, new byte[7]);
        var idx = new byte[64 * 12];
        for (int i = 0; i < 64; i++)
            BitConverter.GetBytes(-1).CopyTo(idx, i * 12);   // no statics anywhere
        File.WriteAllBytes(idxPath, idx);

        using var reader = new StaticReader(idxPath, dataPath, 64, 64);
        Assert.Empty(reader.ReadBlock(0, 0));
        Assert.Equal(1, CacheCount(reader));
        Assert.Empty(reader.ReadBlock(0, 0));
        Assert.Equal(1, CacheCount(reader));
    }
}
