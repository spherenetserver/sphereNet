using System;
using System.IO;
using System.Linq;
using SphereNet.MapData.Map;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What a map reader leaves behind when its constructor fails (review work item D06).
///
/// A successful Dispose says nothing about this. Every one of these readers acquires
/// several things in sequence — a temp file, a file handle, a memory mapping, a second
/// mapping — and a constructor that throws half way through has already taken some of
/// them, with no object left for anyone to dispose. The failures are the ordinary ones:
/// a truncated or corrupt UOP, a statics index whose data file is missing or empty.
///
/// The check is deliberately not a handle count, which is noisy and platform-specific.
/// It is the consequence the operator meets: a file nobody can delete because something
/// still holds it, and a temp directory that grows by the size of a map every time a
/// shard fails to start.
/// </summary>
public sealed class MapReaderLifetimeTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public MapReaderLifetimeTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_maplife_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static int TempMapFileCount() =>
        Directory.GetFiles(Path.GetTempPath(), "spherenet_map_*.tmp").Length;

    /// <summary>A UOP container that parses far enough to start writing the extracted
    /// map, and then fails: the entry it names is compressed and the bytes are not.
    /// </summary>
    private string CorruptUop()
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

        // One block holding one entry, whose name hashes to map0 index 0 so it is kept.
        w.Write(1);                // files in this block
        w.Write(0L);               // no next block
        long dataOffset = fs.Position + 34;
        w.Write(dataOffset);       // file offset
        w.Write(0);                // header length
        w.Write(8);                // compressed length
        w.Write(196);              // decompressed length
        w.Write(HashOf("build/map0legacymul/00000000.dat"));
        w.Write(0u);               // data hash
        w.Write((short)1);         // zlib — but the bytes below are not
        w.Write(new byte[8]);
        return path;
    }

    /// <summary>The name hash UOP uses, mirrored from the reader's own CreateHash.</summary>
    private static ulong HashOf(string s)
    {
        var method = typeof(UopMapReader).GetMethod("CreateHash",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        return (ulong)method.Invoke(null, [s])!;
    }

    // ---- UOP ------------------------------------------------------------

    [Fact]
    public void ACorruptUopLeavesNoHalfExtractedMapBehind()
    {
        int before = TempMapFileCount();
        string uop = CorruptUop();

        var ex = Record.Exception(() => new UopMapReader(uop, 64, 64));
        int after = TempMapFileCount();

        _out.WriteLine($"corrupt UOP: {ex?.GetType().Name ?? "accepted"}; temp map files {before} -> {after}");

        // Failing is right; leaving the extraction behind is not. The extracted map is
        // the size of the map itself, and a shard that fails to start does it on every
        // restart attempt.
        Assert.NotNull(ex);
        Assert.Equal(before, after);
    }

    [Fact]
    public void AUopThatExtractsToNothingFailsWithoutHoldingItsTempFile()
    {
        // A container whose entries belong to another map: nothing matches, so the
        // extraction writes an empty file and the mapping cannot be created from it.
        string path = Path.Combine(_dir, "empty.uop");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var w = new BinaryWriter(fs))
        {
            w.Write(0x0050594Du);
            w.Write(5u); w.Write(0u);
            w.Write(28L); w.Write(100u); w.Write(0);
            w.Write(0);                // no files in this block
            w.Write(0L);               // no next block
        }

        int before = TempMapFileCount();
        var ex = Record.Exception(() => new UopMapReader(path, 64, 64));
        int after = TempMapFileCount();

        _out.WriteLine($"empty UOP: {ex?.GetType().Name ?? "accepted"}; temp map files {before} -> {after}");
        Assert.NotNull(ex);
        Assert.Equal(before, after);
    }

    // ---- statics --------------------------------------------------------

    [Fact]
    public void AStaticsIndexIsReleasedWhenItsDataFileCannotBeOpened()
    {
        string idx = Path.Combine(_dir, "staidx0.mul");
        File.WriteAllBytes(idx, new byte[12 * 64]);
        string data = Path.Combine(_dir, "statics0.mul");   // deliberately absent

        var ex = Record.Exception(() => new StaticReader(idx, data, 64, 64));
        _out.WriteLine($"missing statics data: {ex?.GetType().Name ?? "accepted"}");
        Assert.NotNull(ex);

        // The index was mapped before the data file was tried. If that mapping is still
        // open the file cannot be deleted - which is exactly what an operator meets
        // when they try to replace a bad map file without restarting the server.
        var delete = Record.Exception(() => File.Delete(idx));
        Assert.Null(delete);
    }

    [Fact]
    public void AnEmptyStaticsDataFileIsRefusedWithoutHoldingTheIndex()
    {
        string idx = Path.Combine(_dir, "staidx0.mul");
        File.WriteAllBytes(idx, new byte[12 * 64]);
        string data = Path.Combine(_dir, "statics0.mul");
        File.WriteAllBytes(data, []);                       // zero length

        var ex = Record.Exception(() => new StaticReader(idx, data, 64, 64));
        _out.WriteLine($"empty statics data: {ex?.GetType().Name ?? "accepted"}");
        Assert.NotNull(ex);

        Assert.Null(Record.Exception(() => File.Delete(idx)));
        Assert.Null(Record.Exception(() => File.Delete(data)));
    }

    [Fact]
    public void AGoodStaticsReaderStillWorksAndReleasesOnDispose()
    {
        // The control: every assertion above would pass just as well if the readers had
        // stopped opening anything at all.
        string idx = Path.Combine(_dir, "staidx0.mul");
        string data = Path.Combine(_dir, "statics0.mul");
        WriteOneStaticBlock(idx, data);

        using (var reader = new StaticReader(idx, data, 64, 64))
        {
            var items = reader.ReadBlock(0, 0);
            Assert.Single(items);
            Assert.Equal(0x1234, items[0].TileId);
        }

        Assert.Null(Record.Exception(() => File.Delete(idx)));
        Assert.Null(Record.Exception(() => File.Delete(data)));
    }

    /// <summary>One static item in block (0,0); every other block empty.</summary>
    private static void WriteOneStaticBlock(string idxPath, string dataPath)
    {
        var data = new byte[7];
        BitConverter.GetBytes((ushort)0x1234).CopyTo(data, 0);
        data[2] = 1; data[3] = 2; data[4] = 5;
        BitConverter.GetBytes((ushort)0).CopyTo(data, 5);
        File.WriteAllBytes(dataPath, data);

        var idx = new byte[12 * 64];
        BitConverter.GetBytes(0).CopyTo(idx, 0);     // offset
        BitConverter.GetBytes(7).CopyTo(idx, 4);     // length
        for (int i = 1; i < 64; i++)
            BitConverter.GetBytes(-1).CopyTo(idx, i * 12);
        File.WriteAllBytes(idxPath, idx);
    }
}
