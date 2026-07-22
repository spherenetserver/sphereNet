using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 08 / C5 — a corrupt or truncated current save no longer aborts boot. Each
/// save rotates every logical file's .bakN together, so the loader falls back to
/// the newest intact aligned generation (never a mix of new and old files), and
/// errors clearly only when every generation is unreadable.
/// </summary>
public sealed class BootFallbackTests
{
    private static GameWorld MakeWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var w = new GameWorld(lf);
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    // Save a world holding a single item tagged with a generation marker.
    private static void SaveGeneration(WorldSaver saver, string dir, string genMarker)
    {
        var w = MakeWorld();
        var item = w.CreateItem();
        item.BaseId = 0x0EED;
        item.SetTag("GEN", genMarker);
        w.PlaceItem(item, new Point3D(1500, 1500, 0, 0));
        Assert.True(saver.Save(w, dir));
    }

    private static string? LoadedGeneration(GameWorld w) =>
        w.GetAllObjects().OfType<Item>().Where(i => !i.IsDeleted)
            .Select(i => i.TryGetTag("GEN", out var g) ? g : null)
            .FirstOrDefault(g => g != null);

    // Overwrite the CURRENT (level-0) data files for a logical base with garbage,
    // leaving the .bakN rotations and the manifest intact.
    private static void CorruptCurrentDataFiles(string dir, string baseName)
    {
        foreach (var f in Directory.GetFiles(dir, baseName + "*"))
        {
            string name = Path.GetFileName(f);
            if (name.Contains(".bak") || name.EndsWith(".tmp") || name.EndsWith(".manifest"))
                continue;
            if (!name.Contains(".sbin") && !name.Contains(".scp"))
                continue; // skip the manifest / non-data siblings
            File.WriteAllBytes(f, new byte[] { 0x00, 0x01, 0x02, 0x03 });
        }
    }

    // Delete the entire CURRENT (level-0) generation — every logical data file and
    // manifest — while leaving the .bakN rotations untouched, simulating a lost or
    // externally deleted current save.
    private static void DeleteCurrentGeneration(string dir)
    {
        foreach (var f in Directory.GetFiles(dir))
        {
            string name = Path.GetFileName(f);
            if (name.Contains(".bak")) continue; // keep the backup generations
            File.Delete(f);
        }
    }

    [Theory]
    [InlineData(SaveFormat.BinaryGz, 3)]
    [InlineData(SaveFormat.Binary, 3)]
    [InlineData(SaveFormat.BinaryGz, 0)]
    public void CorruptCurrentSave_FallsBackToPreviousGeneration(SaveFormat fmt, int shards)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_bootfb_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
            {
                Format = fmt,
                ShardCount = shards,
                BackupLevels = 5,
            };
            var loader = new WorldLoader(LoggerFactory.Create(_ => { }));

            SaveGeneration(saver, dir, "A");   // generation A
            SaveGeneration(saver, dir, "B");   // generation B (rotates A -> .bak1)

            // A healthy load takes the current generation (B).
            var healthy = MakeWorld();
            loader.Load(healthy, dir);
            Assert.Equal("B", LoadedGeneration(healthy));

            // Corrupt the current world data; the loader must fall back to the
            // aligned previous generation (A) instead of throwing or blanking.
            CorruptCurrentDataFiles(dir, "sphereworld");

            var recovered = MakeWorld();
            var (items, _) = loader.Load(recovered, dir);
            Assert.True(items >= 1);
            Assert.Equal("A", LoadedGeneration(recovered));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AllGenerationsCorrupt_ThrowsClearError_DoesNotSilentlyStartBlank()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_bootfb_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
            {
                Format = SaveFormat.BinaryGz,
                ShardCount = 3,
                BackupLevels = 5,
            };
            var loader = new WorldLoader(LoggerFactory.Create(_ => { }));

            SaveGeneration(saver, dir, "A"); // only one generation (no .bak yet)
            CorruptCurrentDataFiles(dir, "sphereworld");

            var world = MakeWorld();
            var ex = Record.Exception(() => loader.Load(world, dir));
            Assert.IsType<InvalidDataException>(ex);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void NoSaveDirectory_StartsFreshWithoutThrowing()
    {
        var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
        var world = MakeWorld();
        string missing = Path.Combine(Path.GetTempPath(), $"sphnet_missing_{System.Guid.NewGuid():N}");
        var (items, chars) = loader.Load(world, missing);
        Assert.Equal(0, items);
        Assert.Equal(0, chars);
    }

    // İş #3 — a MISSING current save (not merely a corrupt one) must still recover
    // from a surviving backup. The loader used to return a blank world the moment
    // the current generation was empty, ignoring good .bakN files on disk.
    [Theory]
    [InlineData(SaveFormat.BinaryGz, 3)]
    [InlineData(SaveFormat.BinaryGz, 0)]
    public void MissingCurrentSave_RecoversFromBackup_InsteadOfBlanking(SaveFormat fmt, int shards)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_bootfb_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var saver = new WorldSaver(LoggerFactory.Create(_ => { }))
            {
                Format = fmt,
                ShardCount = shards,
                BackupLevels = 5,
            };
            var loader = new WorldLoader(LoggerFactory.Create(_ => { }));

            SaveGeneration(saver, dir, "A");   // generation A
            SaveGeneration(saver, dir, "B");   // generation B (rotates A -> .bak1)

            // The whole current generation (B) is lost; only the .bak1 backup of A
            // survives. The loader must recover A rather than silently boot blank.
            DeleteCurrentGeneration(dir);

            var recovered = MakeWorld();
            var (items, _) = loader.Load(recovered, dir);
            Assert.True(items >= 1);
            Assert.Equal("A", LoadedGeneration(recovered));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // The genuine fresh-start case (an existing but empty save dir, no backups
    // anywhere) must still start blank and not throw — the recovery path only
    // engages when a backup actually exists.
    [Fact]
    public void EmptySaveDirectory_StartsFreshWithoutThrowing()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_empty_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
            var world = MakeWorld();
            var (items, chars) = loader.Load(world, dir);
            Assert.Equal(0, items);
            Assert.Equal(0, chars);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
