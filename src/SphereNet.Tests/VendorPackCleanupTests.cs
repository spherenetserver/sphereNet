using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// VENDORPACKS: restocks used to re-run a vendor's gear and loot lines into its
/// backpack every ten minutes, and saves still carry packs of up to 255 pouches and
/// weapons. The cleanup touches only the NPCs a restock touches, only their backpack,
/// and only past the threshold.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VendorPackCleanupTests
{
    private static Character Vendor(GameWorld world, int packItems, short x)
    {
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Vendor;
        npc.Name = $"vendor{x}";
        world.PlaceCharacter(npc, new Point3D(x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        npc.Equip(pack, Layer.Pack);
        for (int i = 0; i < packItems; i++)
        {
            var bag = world.CreateItem();
            bag.BaseId = 0x0E76;
            bag.ItemType = ItemType.Container;
            pack.AddItem(bag);
            var gold = world.CreateItem();
            gold.BaseId = 0x0EED;
            bag.AddItem(gold);
        }
        return npc;
    }

    private static GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void TheReportCountsAndChangesNothing()
    {
        var world = World();
        var inflated = Vendor(world, 40, 100);
        Vendor(world, 3, 110);

        var r = SphereNet.Server.Program.ProcessVendorPacks(world, clear: false, min: 16);

        Assert.Equal(2, r.Vendors);
        Assert.Equal(1, r.Inflated);
        Assert.Equal(40, r.InflatedTopLevel);
        Assert.Equal(80, r.InflatedTotal);      // the bags and the gold in them
        Assert.Equal(0, r.Deleted);
        Assert.Equal(40, inflated.Backpack!.Contents.Count);
        Assert.Equal(inflated.Uid.Value, Assert.Single(r.Largest).Uid);
    }

    [Fact]
    public void ClearEmptiesOnlyRestockingVendorsPastTheThreshold()
    {
        var world = World();
        var inflated = Vendor(world, 40, 100);
        var normal = Vendor(world, 3, 110);
        var playerVendor = Vendor(world, 40, 120);
        playerVendor.SetTag("OWNER_UID", "040000001");     // a player's vendor: its goods
        var human = Vendor(world, 40, 130);
        human.NpcBrain = NpcBrainType.Human;               // never restocked
        var worn = world.CreateItem();
        worn.BaseId = 0x1517;
        inflated.Equip(worn, Layer.Shirt);

        var r = SphereNet.Server.Program.ProcessVendorPacks(world, clear: true, min: 16);

        Assert.Equal(1, r.Cleared);
        Assert.Equal(40, r.Deleted);
        Assert.Empty(inflated.Backpack!.Contents);
        Assert.False(inflated.Backpack.IsDeleted);          // the pack itself stays
        Assert.Same(worn, inflated.GetEquippedItem(Layer.Shirt));
        Assert.Equal(3, normal.Backpack!.Contents.Count);
        Assert.Equal(40, playerVendor.Backpack!.Contents.Count);
        Assert.Equal(40, human.Backpack!.Contents.Count);
    }

    [Fact]
    public void ClearZeroEmptiesEveryRestockingVendor()
    {
        var world = World();
        var small = Vendor(world, 3, 100);

        var r = SphereNet.Server.Program.ProcessVendorPacks(world, clear: true, min: 0);

        Assert.Equal(1, r.Cleared);
        Assert.Empty(small.Backpack!.Contents);
    }
}
