using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SphereNet.MapData.Map;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// One map reader, many threads (review finding B4).
///
/// A classic .mul block is a seek followed by 193 sequential reads on a single shared
/// BinaryReader. Two threads doing that at once do not each get their own block - they
/// interleave, so each reads bytes from wherever the other left the file pointer. The
/// review's experiment on a 256-block synthetic map with eight workers and 20,000 reads
/// came back with 12,197 wrong blocks and 3,506 exceptions; the exact counts move with
/// the scheduler, which is itself the point.
///
/// It matters because nothing keeps the map off worker threads: the parallel NPC
/// prestage reaches terrain lookups, so a creature can path across a tile whose height
/// and type came from somewhere else entirely.
///
/// The fixture makes every block self-describing - each cell's tile id encodes its own
/// block - so a torn read is not merely detected, it names the block it actually came
/// from.
/// </summary>
public sealed class MapReaderConcurrencyTests : IDisposable
{
    private const int Blocks = 16;                 // 16x16 blocks = 128x128 cells
    private const int CellsPerBlock = 64;

    private readonly ITestOutputHelper _out;
    private readonly string _path;

    public MapReaderConcurrencyTests(ITestOutputHelper output)
    {
        _out = output;
        _path = Path.Combine(Path.GetTempPath(), $"sphnet_map_{Guid.NewGuid():N}.mul");
        WriteSyntheticMap(_path);
    }

    public void Dispose()
    {
        try { File.Delete(_path); } catch (IOException) { }
    }

    /// <summary>Every cell of block N carries tile id N and height N, so a block read
    /// can be checked against the block that was asked for.</summary>
    private static void WriteSyntheticMap(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);
        for (int bx = 0; bx < Blocks; bx++)
        {
            for (int by = 0; by < Blocks; by++)
            {
                int id = bx * Blocks + by;
                w.Write((uint)id);                  // header
                for (int c = 0; c < CellsPerBlock; c++)
                {
                    w.Write((ushort)id);            // tile id
                    w.Write((sbyte)(id % 128));     // z
                }
            }
        }
    }

    [Fact]
    public void EightThreadsReadingAtOnceGetTheBlocksTheyAskedFor()
    {
        using var reader = new MapReader(_path, Blocks * 8, Blocks * 8);

        int wrong = 0, failed = 0;
        const int Workers = 8, ReadsEach = 2500;

        Parallel.For(0, Workers, w =>
        {
            var rng = new Random(1000 + w);
            for (int i = 0; i < ReadsEach; i++)
            {
                int bx = rng.Next(Blocks), by = rng.Next(Blocks);
                int expected = bx * Blocks + by;
                try
                {
                    var block = reader.ReadBlock(bx, by);
                    if (block.Header != (uint)expected ||
                        block.Cells[0].TileId != (ushort)expected ||
                        block.Cells[CellsPerBlock - 1].TileId != (ushort)expected)
                        System.Threading.Interlocked.Increment(ref wrong);
                }
                catch
                {
                    System.Threading.Interlocked.Increment(ref failed);
                }
            }
        });

        _out.WriteLine($"{Workers} workers x {ReadsEach} reads: wrong={wrong} exceptions={failed}");

        // Zero of each. A map read that is only usually right is worse than one that
        // fails: the caller cannot tell, and the wrong height or tile type goes
        // straight into a movement decision.
        Assert.Equal(0, failed);
        Assert.Equal(0, wrong);
    }

    [Fact]
    public void ConcurrentCellReadsAgreeWithTheirBlock()
    {
        using var reader = new MapReader(_path, Blocks * 8, Blocks * 8);
        int wrong = 0;

        Parallel.For(0, 8, w =>
        {
            var rng = new Random(2000 + w);
            for (int i = 0; i < 2000; i++)
            {
                int x = rng.Next(Blocks * 8), y = rng.Next(Blocks * 8);
                int expected = (x / 8) * Blocks + (y / 8);
                var cell = reader.GetCell(x, y);
                if (cell.TileId != (ushort)expected || cell.Z != (sbyte)(expected % 128))
                    System.Threading.Interlocked.Increment(ref wrong);
            }
        });

        _out.WriteLine($"concurrent GetCell: wrong={wrong}");

        // The path the engine actually uses. GetCell is one ReadBlock, so it inherits
        // whatever ReadBlock does under contention.
        Assert.Equal(0, wrong);
    }

    [Fact]
    public void ASingleThreadStillReadsEveryBlockCorrectly()
    {
        using var reader = new MapReader(_path, Blocks * 8, Blocks * 8);

        for (int bx = 0; bx < Blocks; bx++)
        {
            for (int by = 0; by < Blocks; by++)
            {
                var block = reader.ReadBlock(bx, by);
                int expected = bx * Blocks + by;
                Assert.Equal((uint)expected, block.Header);
                Assert.Equal((ushort)expected, block.Cells[0].TileId);
                Assert.Equal((sbyte)(expected % 128), block.Cells[CellsPerBlock - 1].Z);
            }
        }
    }

    [Fact]
    public void OutOfRangeAndTruncatedReadsStillAnswerEmpty()
    {
        using var reader = new MapReader(_path, Blocks * 8, Blocks * 8);

        // Past the map, negative, and a block index the file does not reach: each has
        // to answer an empty block rather than throwing or reading a neighbour.
        Assert.Equal(0u, reader.ReadBlock(Blocks, 0).Header);
        Assert.Equal(0u, reader.ReadBlock(-1, 0).Header);
        Assert.Equal(0u, reader.ReadBlock(0, Blocks).Header);
        Assert.Equal(default, reader.GetCell(-3, 5));
        Assert.Equal(default, reader.GetCell(5, -3));
    }

    [Fact]
    public void TheMultiReaderIsSafeOnItsFirstSightingToo()
    {
        // Same defect class, found while checking the neighbours. GetMulti caches, so
        // the shared-cursor read only happens on a multi's FIRST sighting - which is
        // exactly when a creature walks onto a ship or into a house nobody has touched
        // yet, on the parallel prestage path through WalkCheck.
        string idx = Path.Combine(Path.GetTempPath(), $"sphnet_mi_{Guid.NewGuid():N}.mul");
        string data = Path.Combine(Path.GetTempPath(), $"sphnet_md_{Guid.NewGuid():N}.mul");
        const int Multis = 2000, Parts = 8, ComponentSize = 12;
        try
        {
            using (var iw = new BinaryWriter(File.Create(idx)))
            using (var dw = new BinaryWriter(File.Create(data)))
            {
                for (int m = 0; m < Multis; m++)
                {
                    iw.Write(m * Parts * ComponentSize);      // offset
                    iw.Write(Parts * ComponentSize);          // length
                    iw.Write(0);                              // extra
                    for (int c = 0; c < Parts; c++)
                    {
                        dw.Write((ushort)(m + 1));            // tile id names its multi
                        dw.Write((short)c);
                        dw.Write((short)0);
                        dw.Write((short)0);
                        dw.Write(1u);
                    }
                }
            }

            using var reader = new SphereNet.MapData.Multi.MultiReader(idx, data);
            int wrong = 0;
            Parallel.For(0, 16, w =>
            {
                // Every worker sweeps the whole table from a different starting
                // point, so the COLD reads - the only ones that touch the file -
                // overlap. GetMulti caches, so a small table would race for a few
                // microseconds and then never again.
                for (int i = 0; i < Multis; i++)
                {
                    int id = (i + w * (Multis / 16)) % Multis;
                    var def = reader.GetMulti(id);
                    if (def == null || def.Components.Length != Parts ||
                        def.Components.Any(c => c.TileId != (ushort)(id + 1)))
                        System.Threading.Interlocked.Increment(ref wrong);
                }
            });

            _out.WriteLine($"concurrent GetMulti: wrong={wrong}");
            Assert.Equal(0, wrong);
        }
        finally
        {
            try { File.Delete(idx); } catch (IOException) { }
            try { File.Delete(data); } catch (IOException) { }
        }
    }

    [Fact]
    public void ATruncatedFileDoesNotReadPastItsEnd()
    {
        string shortPath = _path + ".short";
        var all = File.ReadAllBytes(_path);
        File.WriteAllBytes(shortPath, all.Take(all.Length - 100).ToArray());
        try
        {
            using var reader = new MapReader(shortPath, Blocks * 8, Blocks * 8);

            // The last block is now incomplete. Reading it must be an empty block, not
            // a short read padded with whatever the buffer held.
            var last = reader.ReadBlock(Blocks - 1, Blocks - 1);
            _out.WriteLine($"truncated last block header={last.Header}");
            Assert.Equal(0u, last.Header);

            // Everything before it is untouched.
            Assert.Equal(0u, reader.ReadBlock(0, 0).Header);
            Assert.Equal(1u, reader.ReadBlock(0, 1).Header);
        }
        finally { try { File.Delete(shortPath); } catch (IOException) { } }
    }
}
