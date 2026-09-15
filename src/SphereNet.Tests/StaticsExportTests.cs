using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Save;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// SERV.SAVESTATICS: what goes in the file, and what happens to the one already there
/// (parity matrix: SAVESTATICS).
///
/// The matrix listed "native static-map file generation" as the remaining slice, which
/// upstream does not do either: CWorld::SaveStatics walks the sectors and writes a
/// SCRIPT file of every ATTR_STATIC item (CWorld.cpp:1233), not statics0.mul. What it
/// does do and this did not is retire the previous file - it writes through
/// OpenScriptBackup, which rotates like every other save - and announce the save to
/// everyone before it starts.
///
/// Rotation is the part that matters. A statics export is what an operator runs twice
/// in a row while getting a region right, and the second run must not be able to
/// destroy the first.
/// </summary>
public sealed class StaticsExportTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public StaticsExportTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), $"sphnet_statics_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 512, 512);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    private static Item Static(GameWorld world, short x, short y)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(x, y, 0, 0));
        item.SetAttr(ObjAttributes.Static);
        return item;
    }

    private WorldSaver Saver(int backupLevels) =>
        new(LoggerFactory.Create(_ => { })) { Format = SaveFormat.Text, BackupLevels = backupLevels };

    private string Path0 => System.IO.Path.Combine(_dir, "spherestatics.scp");

    // ---- what goes in ----------------------------------------------------

    [Fact]
    public void OnlyStaticGroundItemsAreWritten()
    {
        var world = World();
        Static(world, 100, 100);
        Static(world, 101, 100);

        // Not static: an ordinary item lying next to them.
        var ordinary = world.CreateItem();
        ordinary.BaseId = 0x0EED;
        world.PlaceItem(ordinary, new Point3D(102, 100, 0, 0));

        // Static but a structure: upstream skips multis explicitly (CWorld.cpp:1270).
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        multi.ItemType = ItemType.Multi;
        world.PlaceItem(multi, new Point3D(103, 100, 0, 0));
        multi.SetAttr(ObjAttributes.Static);

        // Static but in a bag: nothing to write a world position for (:1267).
        var bag = world.CreateItem();
        bag.BaseId = 0x0E75;
        bag.ItemType = ItemType.Container;
        world.PlaceItem(bag, new Point3D(104, 100, 0, 0));
        var inside = world.CreateItem();
        inside.BaseId = 0x0EED;
        inside.SetAttr(ObjAttributes.Static);
        bag.AddItem(inside);

        int count = Saver(backupLevels: 1).ExportStatics(world, Path0);

        _out.WriteLine($"exported {count} of {world.GetAllObjects().Count()} objects");
        Assert.Equal(2, count);
    }

    [Fact]
    public void TheFileEndsWithItsEofMarker()
    {
        var world = World();
        Static(world, 100, 100);
        Saver(backupLevels: 1).ExportStatics(world, Path0);

        string text = File.ReadAllText(Path0);
        // Upstream closes the statics file with an [EOF] section (CWorld.cpp:1278), and
        // a reader that trusts it would otherwise keep looking for records.
        Assert.Contains("[EOF]", text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- what happens to the file already there ---------------------------

    [Fact]
    public void ASecondExportRetiresTheFirstInsteadOfEatingIt()
    {
        var world = World();
        Static(world, 100, 100);
        Static(world, 101, 100);
        Assert.Equal(2, Saver(backupLevels: 2).ExportStatics(world, Path0));
        string first = File.ReadAllText(Path0);

        // The operator changes their mind and exports a smaller selection.
        var narrower = World();
        Static(narrower, 200, 200);
        Assert.Equal(1, Saver(backupLevels: 2).ExportStatics(narrower, Path0));

        string backup = Path0 + ".bak1";
        _out.WriteLine($"after the second export: live file {new FileInfo(Path0).Length} bytes, " +
                       $"backup exists = {File.Exists(backup)}");

        Assert.True(File.Exists(backup), "the previous statics export was overwritten");
        Assert.Equal(first, File.ReadAllText(backup));
    }

    [Fact]
    public void ThirdExportPushesTheBackupsDownOneLevel()
    {
        var world = World();
        Static(world, 100, 100);
        var saver = Saver(backupLevels: 2);

        saver.ExportStatics(world, Path0);
        string oldest = File.ReadAllText(Path0);
        saver.ExportStatics(world, Path0);
        saver.ExportStatics(world, Path0);

        Assert.True(File.Exists(Path0 + ".bak1"));
        Assert.True(File.Exists(Path0 + ".bak2"));
        Assert.Equal(oldest, File.ReadAllText(Path0 + ".bak2"));
    }

    [Fact]
    public void BackupsOffKeepsOnlyTheLiveFile()
    {
        // BackupLevels=0 is a choice, not a failure: nothing is retired, and the
        // rotation must not leave stale levels behind either.
        var world = World();
        Static(world, 100, 100);
        var saver = Saver(backupLevels: 0);

        saver.ExportStatics(world, Path0);
        saver.ExportStatics(world, Path0);

        Assert.True(File.Exists(Path0));
        Assert.Empty(Directory.GetFiles(_dir, "*.bak*"));
    }

    [Fact]
    public void AFailedExportLeavesThePreviousOneReadable()
    {
        // The export validates what it wrote by reading it back before it publishes.
        // Whatever happens, the file that was there has been retired first, so the
        // operator still has it.
        var world = World();
        Static(world, 100, 100);
        Assert.Equal(1, Saver(backupLevels: 1).ExportStatics(world, Path0));
        string good = File.ReadAllText(Path0);

        var empty = World();
        Assert.Equal(0, Saver(backupLevels: 1).ExportStatics(empty, Path0));

        Assert.Equal(good, File.ReadAllText(Path0 + ".bak1"));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }
}
