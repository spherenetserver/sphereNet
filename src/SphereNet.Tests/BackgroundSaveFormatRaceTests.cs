using System;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Save;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A save that has started writing keeps the settings it started with (review finding
/// B3).
///
/// In background mode the world walk stays on the main loop and the shard/encode/write
/// phase moves to a worker thread. Both phases read Format and ShardCount from the
/// SAME mutable saver, and SAVEFORMAT changes those fields from the main loop the
/// moment the command arrives. Queuing the command onto the main loop does not help:
/// the writer is on another thread either way.
///
/// The window is real rather than theoretical because the writer reads the fields at
/// several different moments - the extension when it picks file names, the encoder
/// when it opens each shard, the shard count when it partitions - so a change landing
/// mid-write does not switch the save over, it splits it. The test drives that window
/// deterministically with a barrier instead of hoping to hit it.
/// </summary>
public sealed class BackgroundSaveFormatRaceTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public BackgroundSaveFormatRaceTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_fmtrace_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static GameWorld World(int items)
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        var ch = w.CreateCharacter();
        w.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        for (int i = 0; i < items; i++)
        {
            var it = w.CreateItem();
            it.BaseId = 0x0EED;
            w.PlaceItem(it, new Point3D((short)(1001 + i), 1000, 0, 0));
        }
        return w;
    }

    private string[] LiveFiles() =>
        Directory.GetFiles(_dir)
            .Select(Path.GetFileName)
            .Where(n => n != null && !n.Contains(".bak", StringComparison.OrdinalIgnoreCase)
                                  && !n.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    [Fact]
    public void AFormatChangeDuringTheWriteDoesNotSplitTheSaveInHalf()
    {
        var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };

        // The main loop's half: capture the world, then hand the write to a worker.
        var prepared = saver.Prepare(World(12));

        var writerReached = new ManualResetEventSlim(false);
        var formatChanged = new ManualResetEventSlim(false);
        Exception? failure = null;

        var writer = new Thread(() =>
        {
            try
            {
                writerReached.Set();
                // Stand exactly where a real background writer stands when an operator
                // types SAVEFORMAT: the write has begun and has not finished.
                formatChanged.Wait(TimeSpan.FromSeconds(5));
                saver.WritePrepared(prepared, _dir);
            }
            catch (Exception ex) { failure = ex; }
        })
        { IsBackground = true, Name = "race-writer" };

        writer.Start();
        Assert.True(writerReached.Wait(TimeSpan.FromSeconds(5)));

        // SAVEFORMAT arrives on the main loop and mutates the shared saver.
        saver.Format = SaveFormat.Binary;
        saver.ShardCount = 4;
        formatChanged.Set();

        Assert.True(writer.Join(TimeSpan.FromSeconds(30)), "the background write did not finish");
        Assert.Null(failure);

        var files = LiveFiles();
        _out.WriteLine("after the raced write: " + string.Join(", ", files));

        // The save was prepared as a single Text file. It must land as one, whatever
        // the live saver was told afterwards - a half-Text, half-Binary generation is
        // not something any loader can be asked to make sense of, and the extension
        // says one thing while the bytes say another.
        Assert.Contains("sphereworld.scp", files);
        Assert.DoesNotContain(files, f => f.EndsWith(".sbin", StringComparison.OrdinalIgnoreCase));

        // And it has to be readable as what it claims to be.
        var back = World(0);
        var (items, _) = new SphereNet.Persistence.Load.WorldLoader(
            LoggerFactory.Create(_ => { })).Load(back, _dir);
        _out.WriteLine($"reloaded {items} items");
        Assert.Equal(12, items);
    }

    [Fact]
    public void TheNextSaveDoesUseTheNewFormat()
    {
        var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        Assert.True(saver.Save(World(4), _dir));

        saver.Format = SaveFormat.Binary;
        Assert.True(saver.Save(World(4), _dir));

        var files = LiveFiles();
        _out.WriteLine("after the format change: " + string.Join(", ", files));

        // The control. Pinning the settings to the prepared save must not make the
        // setting unchangeable - it only stops a change reaching a write already under
        // way.
        Assert.Contains("sphereworld.sbin", files);
        Assert.DoesNotContain("sphereworld.scp", files);
    }

    [Fact]
    public void AShardCountChangeDuringTheWriteIsAlsoIgnored()
    {
        var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
        { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = 0 };
        var prepared = saver.Prepare(World(12));

        var reached = new ManualResetEventSlim(false);
        var changed = new ManualResetEventSlim(false);
        var writer = new Thread(() =>
        {
            reached.Set();
            changed.Wait(TimeSpan.FromSeconds(5));
            saver.WritePrepared(prepared, _dir);
        })
        { IsBackground = true };

        writer.Start();
        Assert.True(reached.Wait(TimeSpan.FromSeconds(5)));
        saver.ShardCount = 4;
        changed.Set();
        Assert.True(writer.Join(TimeSpan.FromSeconds(30)));

        var files = LiveFiles();
        _out.WriteLine("after the raced write: " + string.Join(", ", files));

        // Partitioning decides which object goes in which file. Changing it halfway
        // through would scatter records across a layout the manifest does not
        // describe.
        Assert.Contains("sphereworld.scp", files);
        Assert.DoesNotContain(files, f => f.StartsWith("sphereworld.0", StringComparison.OrdinalIgnoreCase));
    }
}
