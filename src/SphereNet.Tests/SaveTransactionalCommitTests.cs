using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş H6 — the world save writes three logical files (sphereworld, spherechars,
/// spheredata). Each used to be committed to its live name before the next was
/// even written, so a crash mid-save could publish a new-generation sphereworld
/// beside an old-generation spherechars — a mixed, internally inconsistent world
/// the loader silently degrades into dangling-reference data loss. WritePrepared
/// now stages every file to .tmp first and commits them back-to-back, so a write
/// failure leaves the previous generation completely intact.
/// </summary>
public sealed class SaveTransactionalCommitTests
{
    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static WorldSaver Saver(int backupLevels = 0) =>
        new(LoggerFactory.Create(_ => { })) { Format = SaveFormat.Text, ShardCount = 0, BackupLevels = backupLevels };

    [Fact]
    public void NormalSave_RoundTrips_AndSecondSaveRotatesAllThreeFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_h6ok_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var world = MakeWorld();
            var ch = world.CreateCharacter();
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var item = world.CreateItem(); item.BaseId = 0x0EED;
            item.Position = new Point3D(1001, 1000, 0, 0);

            Assert.True(Saver(backupLevels: 1).Save(world, dir));
            // A second save must rotate each live file to .bak1 (all three together).
            Assert.True(Saver(backupLevels: 1).Save(world, dir));

            Assert.True(File.Exists(Path.Combine(dir, "sphereworld.scp.bak1")));
            Assert.True(File.Exists(Path.Combine(dir, "spherechars.scp.bak1")));
            Assert.True(File.Exists(Path.Combine(dir, "spheredata.scp.bak1")));

            var dst = MakeWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(dst, dir);
            Assert.NotNull(dst.FindChar(ch.Uid));
            Assert.NotNull(dst.FindItem(item.Uid));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WriteFailure_LeavesPreviousGenerationIntact_NoMixedGeneration()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_h6fail_{System.Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Generation A: one char + one grounded item.
            var world = MakeWorld();
            var ch = world.CreateCharacter();
            world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
            var itemA = world.CreateItem(); itemA.BaseId = 0x0EED;
            itemA.Position = new Point3D(1001, 1000, 0, 0);
            Assert.True(Saver().Save(world, dir));

            // Prepare a doomed generation B: add a second item so a committed
            // sphereworld would be observably different from generation A.
            var itemB = world.CreateItem(); itemB.BaseId = 0x0EED;
            itemB.Position = new Point3D(1002, 1000, 0, 0);

            // Force the spherechars write to fail: a directory sitting on its .tmp
            // path makes the writer throw. sphereworld is written (to .tmp) first but,
            // because commits are deferred, its live file is never touched.
            string charsTmp = Path.Combine(dir, "spherechars.scp.tmp");
            Directory.CreateDirectory(charsTmp);

            Assert.False(Saver().Save(world, dir));

            Directory.Delete(charsTmp, recursive: true); // let the reload proceed

            // The on-disk world must still be generation A in its entirety: itemB
            // never made it, proving sphereworld was not committed ahead of the
            // failed spherechars write (the old torn-commit bug).
            var dst = MakeWorld();
            new WorldLoader(LoggerFactory.Create(_ => { })).Load(dst, dir);
            Assert.NotNull(dst.FindChar(ch.Uid));
            Assert.NotNull(dst.FindItem(itemA.Uid));
            Assert.Null(dst.FindItem(itemB.Uid));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
