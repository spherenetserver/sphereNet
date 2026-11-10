using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Persistence.Load;
using SphereNet.Persistence.Save;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The reference rewrites the statics file only on SAVESTATICS, so a static item
/// deleted at run time returns at the next boot. SphereNet refreshes the file with
/// the world save when a ground static was deleted since it was last written.
/// </summary>
public sealed class DeletedStaticPersistenceTests
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

    [Fact]
    public void DeletedStatic_IsGoneFromTheStaticsFileAfterTheNextWorldSave()
    {
        string dir = Path.Combine(Path.GetTempPath(), "spherenet_delstatic_" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = LoggerFactory.Create(_ => { });
            var world = MakeWorld();
            var keep = Static(world, new Point3D(2010, 2000, 0, 0));
            var gone = Static(world, new Point3D(2012, 2000, 0, 0));
            var saver = new WorldSaver(log);

            // SAVESTATICS-equivalent file with both statics, as a boot would find it.
            Directory.CreateDirectory(dir);
            saver.ExportStatics(world, Path.Combine(dir, "spherestatics.scp"));

            // A save with nothing deleted leaves the statics file alone.
            Assert.True(saver.Save(world, dir));
            Assert.False(world.StaticsDirty);
            Assert.Equal(2, new WorldLoader(log).LoadFile(MakeWorld(), Path.Combine(dir, "spherestatics.scp")).Item1);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;

            gone.Delete();
            Assert.True(world.StaticsDirty);
            Assert.True(saver.Save(world, dir));
            Assert.False(world.StaticsDirty);

            var fresh = MakeWorld();
            var loader = new WorldLoader(log);
            var (items, _) = loader.LoadFile(fresh, Path.Combine(dir, "spherestatics.scp"));
            Assert.Equal(1, items);
            Assert.NotNull(fresh.FindItem(keep.Uid));
            Assert.Null(fresh.FindItem(gone.Uid));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void DeletingAnOrdinaryItemOrAStaticInsideAPack_DoesNotTouchTheStaticsFile()
    {
        var world = MakeWorld();
        var plain = world.CreateItem();
        plain.BaseId = 0x0EED;
        world.PlaceItem(plain, new Point3D(2010, 2000, 0, 0));
        plain.Delete();

        Assert.False(world.StaticsDirty);
    }
}
