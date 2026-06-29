using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Regressions for two world-integrity bugs:
///   * FindRegion poisoning an 8x8 cache cell with a null result, so a later
///     in-region query in the same cell wrongly returned null.
///   * High-level delete paths (script REMOVE, FINDLAYER.REMOVE, container
///     DELETE/EMPTY, item-use consumption) calling the low-level Item.Delete()
///     flag-set instead of fully unlinking from the world, leaving dead
///     references in the object table, parent containers and equipment slots.
/// </summary>
public class WorldUnlinkAndRegionTests
{
    [Fact]
    public void FindRegion_NullResultDoesNotPoisonSameCellInRegionQuery()
    {
        var world = TestHarness.CreateWorld();

        // West edge at x=4 is deliberately NOT 8-aligned, so the 8x8 cell
        // covering x=0..7 straddles the region boundary: x<4 is outside,
        // x>=4 is inside.
        var region = new Region { Name = "unaligned_region", MapIndex = 0 };
        region.AddRect(4, 0, 100, 100);
        world.AddRegion(region);

        var outside = new Point3D(0, 50, 0, 0); // x>>3 == 0, outside the rect
        var inside = new Point3D(5, 50, 0, 0);   // same 8x8 cell, inside the rect

        // Resolve the region-less tile first. The old code cached this null for
        // the whole cell, so the following in-region lookup read the stale null.
        Assert.Null(world.FindRegion(outside));
        Assert.Same(region, world.FindRegion(inside));

        // The outside tile must still resolve to null (no poisoning the other way).
        Assert.Null(world.FindRegion(outside));
    }

    [Fact]
    public void ItemRemoveFromWorld_EquippedItem_ClearsSlotAndObjectTable()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var sword = world.CreateItem();
        sword.BaseId = 0x0F5E;
        Assert.True(ch.Equip(sword, Layer.OneHanded));
        Assert.Same(sword, ch.GetEquippedItem(Layer.OneHanded));
        Assert.Same(sword, world.FindItem(sword.Uid));

        sword.RemoveFromWorld();

        Assert.True(sword.IsDeleted);
        Assert.Null(world.FindItem(sword.Uid));          // gone from object table
        Assert.Null(ch.GetEquippedItem(Layer.OneHanded)); // slot no longer holds a dead item
    }

    [Fact]
    public void ItemRemoveFromWorld_ContainedItem_UnlinksFromParentAndObjectTable()
    {
        var world = TestHarness.CreateWorld();
        var box = world.CreateItem();
        box.ItemType = ItemType.Container;
        world.PlaceItem(box, new Point3D(100, 100, 0, 0));

        var gem = world.CreateItem();
        box.AddItem(gem);
        Assert.Same(gem, world.FindItem(gem.Uid));
        Assert.Single(box.Contents);

        gem.RemoveFromWorld();

        Assert.True(gem.IsDeleted);
        Assert.Null(world.FindItem(gem.Uid));
        Assert.Empty(box.Contents);
    }

    [Fact]
    public void ContainerDeleteCommand_RemovesChildFromObjectTable()
    {
        var world = TestHarness.CreateWorld();
        var box = world.CreateItem();
        box.ItemType = ItemType.Container;
        world.PlaceItem(box, new Point3D(100, 100, 0, 0));

        var child = world.CreateItem();
        box.AddItem(child);
        var childUid = child.Uid;

        // Script "DELETE 1" used to flag the child and drop it from the
        // parent list, but left it registered in the world object table.
        // (DELETE ignores the console source, so null! is safe here.)
        Assert.True(box.TryExecuteCommand("DELETE", "1", null!));

        Assert.Empty(box.Contents);
        Assert.Null(world.FindItem(childUid));
        Assert.True(child.IsDeleted);
    }
}
