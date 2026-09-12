using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What SAVESTATICS writes, and what it must leave alone (port plan İŞ-43 /
/// PLAN-605).
///
/// Source-X CWorld::SaveStatics (CWorld.cpp:1233) walks SECTORS and writes every
/// ATTR_STATIC item it finds there, skipping multis (:1267-1273). Two rules follow
/// from that walk which a "every object with the flag" filter does not honour:
/// an item inside a container is not held by a sector and so is not a world static,
/// and a multi is a structure rather than a static.
///
/// Note on scope: the reference's output is a text script backup too, not a .mul.
/// SAVESTATICS has never been a map export in either engine - the terrain and
/// statics .mul files are a separate subject this does not touch.
/// </summary>
public sealed class SaveStaticsScopeParityTests
{
    private static GameWorld MakeWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Static(GameWorld world, Point3D at)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0B80;
        item.SetAttr(ObjAttributes.Static);
        world.PlaceItem(item, at);
        return item;
    }

    private static (int Count, string Text) Export(GameWorld world)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "spherenet_statics_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            string path = Path.Combine(tmp, "statics.scp");
            int count = new WorldSaver(LoggerFactory.Create(_ => { })).ExportStatics(world, path);
            return (count, File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AStaticOnTheGroundIsWritten()
    {
        var world = MakeWorld();
        Static(world, new Point3D(2010, 2000, 0, 0));

        Assert.Equal(1, Export(world).Count);
    }

    [Fact]
    public void AStaticFlaggedItemInsideAContainerIsNot()
    {
        // The reference iterates pSector->m_Items, so a contained item is never
        // reached. It has no world position to write, and importing it would
        // scatter it on the ground at whatever the writer guessed.
        var world = MakeWorld();
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.BaseId = 0x0E3C;
        world.PlaceItem(chest, new Point3D(2000, 2000, 0, 0));

        var packed = world.CreateItem();
        packed.BaseId = 0x0B80;
        packed.SetAttr(ObjAttributes.Static);
        Assert.True(chest.TryAddItem(packed));

        Assert.Equal(0, Export(world).Count);
    }

    [Fact]
    public void AStaticFlaggedMultiIsNotAStatic()
    {
        // CWorld.cpp:1270 skips multis outright: a house written into the statics
        // file would come back on import as a loose item, not a structure.
        var world = MakeWorld();
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.BaseId = 0x4064;
        multi.SetAttr(ObjAttributes.Static);
        world.PlaceItem(multi, new Point3D(2020, 2000, 0, 0));

        Assert.Equal(0, Export(world).Count);
    }

    [Fact]
    public void ACustomHouseMultiIsSkippedTheSameWay()
    {
        var world = MakeWorld();
        var multi = world.CreateItem();
        multi.ItemType = ItemType.MultiCustom;
        multi.BaseId = 0x4064;
        multi.SetAttr(ObjAttributes.Static);
        world.PlaceItem(multi, new Point3D(2030, 2000, 0, 0));

        Assert.Equal(0, Export(world).Count);
    }

    [Fact]
    public void TheGroundStaticIsStillWrittenAlongsideTheSkippedOnes()
    {
        // All three together: only the plain ground static survives the filter.
        var world = MakeWorld();
        var kept = Static(world, new Point3D(2010, 2000, 0, 0));

        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.BaseId = 0x0E3C;
        world.PlaceItem(chest, new Point3D(2000, 2000, 0, 0));
        var packed = world.CreateItem();
        packed.BaseId = 0x0B80;
        packed.SetAttr(ObjAttributes.Static);
        chest.TryAddItem(packed);

        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.BaseId = 0x4064;
        multi.SetAttr(ObjAttributes.Static);
        world.PlaceItem(multi, new Point3D(2020, 2000, 0, 0));

        var (count, text) = Export(world);

        Assert.Equal(1, count);
        Assert.Contains($"0{kept.Uid.Value:X}", text.ToUpperInvariant());
    }

    [Fact]
    public void AnItemWithoutTheFlagIsNeverWritten()
    {
        var world = MakeWorld();
        var loose = world.CreateItem();
        loose.BaseId = 0x0B80;
        world.PlaceItem(loose, new Point3D(2040, 2000, 0, 0));

        Assert.Equal(0, Export(world).Count);
    }
}
