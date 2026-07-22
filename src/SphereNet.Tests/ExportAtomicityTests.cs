using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Formats;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 16 / M6 — object/world exports (used by destructive restore as its rollback
/// snapshot) wrote straight to the final path with FileMode.Create, so a crash
/// mid-write left a truncated snapshot over the previous good one. Exports now
/// write a sibling .tmp, validate its record count, and atomically move it over
/// the final — a failed export leaves the previous final intact and throws, so
/// the destructive restore aborts before deleting any live object.
/// </summary>
public sealed class ExportAtomicityTests
{
    private static GameWorld MakeWorld()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static int CountRecords(string path)
    {
        using var r = SaveIO.OpenReader(path);
        int n = 0;
        while (r.NextRecord(out _))
        {
            while (r.NextProperty(out _, out _)) { }
            n++;
        }
        return n;
    }

    [Fact]
    public void ExportObjects_WritesFinalAtomically_NoStrayTmp()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_exp_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var world = MakeWorld();
            var a = world.CreateItem(); a.BaseId = 0x0EED; world.PlaceItem(a, new Point3D(1000, 1000, 0, 0));
            var b = world.CreateItem(); b.BaseId = 0x0EED; world.PlaceItem(b, new Point3D(1001, 1000, 0, 0));
            var saver = new WorldSaver(LoggerFactory.Create(_ => { }));

            string path = Path.Combine(dir, "export.scp");
            int n = saver.ExportObjects(new ObjBase[] { a, b }, path);

            Assert.Equal(2, n);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".tmp")); // temp cleaned up on success
            Assert.Equal(2, CountRecords(path));       // the final re-reads intact

            // A re-export atomically replaces the final (one object now).
            int n2 = saver.ExportObjects(new ObjBase[] { a }, path);
            Assert.Equal(1, n2);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(1, CountRecords(path));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RestoreFile_BackupSnapshotFails_AbortsWithoutDeletingLiveObjects()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"sphnet_restore_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var world = MakeWorld();
            var live = world.CreateItem();
            live.BaseId = 0x0EED;
            world.PlaceItem(live, new Point3D(1000, 1000, 0, 0));
            uint serial = live.Uid.Value;

            // A restore file naming the SAME serial → the restore would delete the
            // live object and replace it. It must not, if the rollback snapshot
            // can't be written.
            string restorePath = Path.Combine(dir, "restore.scp");
            using (var w = SaveIO.OpenWriter(restorePath, SaveFormat.Text))
            {
                w.BeginRecord("WORLDITEM");
                w.WriteProperty("SERIAL", $"0{serial:X8}");
                w.WriteProperty("ID", "0EED");
                w.WriteProperty("P", "1000,1000,0,0");
                w.EndRecord();
            }

            var loader = new WorldLoader(LoggerFactory.Create(_ => { }));
            // Simulate an export whose validation fails (or the process is killed):
            // the backup writer throws instead of producing a good snapshot.
            int BackupThatFails(IReadOnlyList<ObjBase> objs, string p)
                => throw new InvalidDataException("simulated snapshot export failure");

            var ex = Record.Exception(() => loader.RestoreFile(world, restorePath, null, BackupThatFails));
            Assert.NotNull(ex);

            // The live object is untouched — the destructive phase never ran.
            var stillThere = world.FindItem(new Serial(serial));
            Assert.NotNull(stillThere);
            Assert.False(stillThere!.IsDeleted);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
