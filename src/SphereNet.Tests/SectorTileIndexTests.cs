using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>The sectors' per-tile index of ground items, which the walk check and
/// the path search read instead of scanning the whole sector: it follows an item
/// placed, moved within the sector, moved to another sector, picked up and deleted.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SectorTileIndexTests
{
    private static GameWorld World()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void TheTileIndexFollowsGroundItems()
    {
        var world = World();
        var item = world.CreateItem();
        item.BaseId = 0x0E75;
        world.PlaceItem(item, new Point3D(10, 10, 0, 0));
        Assert.Contains(item, world.GroundItemsOnTile(0, 10, 10));
        Assert.Empty(world.GroundItemsOnTile(0, 11, 10));

        // Moved within the same sector by its position alone.
        item.Position = new Point3D(12, 13, 0, 0);
        Assert.DoesNotContain(item, world.GroundItemsOnTile(0, 10, 10));
        Assert.Contains(item, world.GroundItemsOnTile(0, 12, 13));

        // Placed again, in another sector.
        world.PlaceItem(item, new Point3D(100, 100, 0, 0));
        Assert.DoesNotContain(item, world.GroundItemsOnTile(0, 12, 13));
        Assert.Contains(item, world.GroundItemsOnTile(0, 100, 100));

        // Into a container: no longer on the ground.
        var box = world.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = SphereNet.Core.Enums.ItemType.Container;
        world.PlaceItem(box, new Point3D(50, 50, 0, 0));
        Assert.True(box.TryAddItem(item));
        Assert.DoesNotContain(world.GroundItemsOnTile(0, 100, 100), i => i == item && i.IsOnGround);

        world.DeleteObject(box);
        Assert.DoesNotContain(world.GroundItemsOnTile(0, 50, 50), i => !i.IsDeleted && i == box);
    }

    [Fact]
    public void TheMultiAreaIndexFollowsGroundMultisInListOrder()
    {
        var world = World();
        SphereNet.Game.Objects.Items.Item Multi(int x, int y)
        {
            var m = world.CreateItem();
            m.BaseId = 0x0064;
            m.ItemType = SphereNet.Core.Enums.ItemType.Multi;
            world.PlaceItem(m, new Point3D((short)x, (short)y, 0, 0));
            return m;
        }
        var near = Multi(120, 100);    // a cell east of the probe's
        var here = Multi(100, 100);
        var far = Multi(300, 300);
        Assert.Equal([near, here], world.MultisNear(0, 100, 100));
        Assert.Empty(world.MultisNear(0, 200, 200));

        // A ship sails off: found where it is now, not where it was.
        near.Position = new Point3D(290, 300, 0, 0);
        Assert.Equal([here], world.MultisNear(0, 100, 100));
        Assert.Equal([near, far], world.MultisNear(0, 300, 300));

        world.DeleteObject(here);
        Assert.Empty(world.MultisNear(0, 100, 100));
    }
}
