using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// Gold dropped on the pack joins the gold already in it.
///
/// Upstream stacks on the way in: a stackable item added to a container with no slot
/// named walks the contents and merges with the first pile it matches
/// (CItemContainer::ContentAdd, CItemContainer.cpp:618-634). The client names no slot -
/// it sends -1,-1 - whenever the drop lands on the container itself or on the paperdoll.
///
/// Without it, gold looted from a corpse and dropped on the pack sat beside the gold
/// already there as a second pile, and the status bar counted only one of them.
///
/// A pile that cannot take the whole amount is topped up to its maximum and the
/// remainder carries on, which is what CItem::Stack does before returning false.
/// </summary>
public sealed class ContainerDropStackMergeTests
{
    private sealed record Bench(SphereNet.Game.World.GameWorld World,
                                SphereNet.Game.Clients.GameClient Client,
                                SphereNet.Game.Objects.Characters.Character Me,
                                Item Pack);

    private static Bench Build(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        // Gold is stackable because its tiledata says so (the Generic flag); a test
        // world has no .mul behind it, so say it here.
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 256, 256);
        map.SetSyntheticItemTile(0x0EED, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Generic, Weight = 0
        });
        world.MapData = map;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var me = world.CreateCharacter();
        me.IsPlayer = true; me.PrivLevel = PrivLevel.Player;
        me.Str = 100; me.Dex = 100; me.Int = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);
        return new Bench(world, client, me, pack);
    }

    /// <summary>A pile sitting in a pouch inside the pack - the same drag the client
    /// makes when a looted pile is moved onto the backpack, without depending on the
    /// corpse-reach rules this test is not about.</summary>
    private static Item Looted(Bench b, ushort amount)
    {
        var pouch = b.World.CreateItem();
        pouch.BaseId = 0x0E76;
        pouch.ItemType = ItemType.Container;
        Assert.True(b.Pack.TryAddItem(pouch));
        var g = Gold(b, amount);
        Assert.True(pouch.TryAddItem(g));
        return g;
    }

    private static Item Gold(Bench b, ushort amount)
    {
        var g = b.World.CreateItem();
        g.BaseId = 0x0EED;
        g.ItemType = ItemType.Gold;
        g.Amount = amount;
        return g;
    }

    /// <summary>The corpse-loot case.</summary>
    [Fact]
    public void GoldDroppedOnThePackJoinsThePileAlreadyThere()
    {
        var b = Build(8911);
        var have = Gold(b, 500);
        Assert.True(b.Pack.TryAddItem(have));

        var looted = Looted(b, 250);
        b.Client.Inventory.HandleItemPickup(looted.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(looted.Uid.Value, -1, -1, 0, b.Pack.Uid.Value);

        var gold = b.Pack.Contents.Where(i => i.ItemType == ItemType.Gold).ToList();
        Assert.True(gold.Count == 1 && gold[0].Amount == 750,
            $"expected one pile of 750, got [{string.Join(",", gold.Select(g => g.Amount))}]");
    }

    /// <summary>A named slot is a placement, not a merge - the player put it THERE.</summary>
    [Fact]
    public void ADropIntoANamedSlotStillPlacesIt()
    {
        var b = Build(8912);
        var have = Gold(b, 500);
        Assert.True(b.Pack.TryAddItem(have));

        var looted = Looted(b, 250);
        b.Client.Inventory.HandleItemPickup(looted.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(looted.Uid.Value, 40, 60, 0, b.Pack.Uid.Value);

        Assert.Equal(2, b.Pack.Contents.Count(i => i.ItemType == ItemType.Gold));
    }

    /// <summary>A pile that cannot take all of it is topped up and the rest stays.</summary>
    [Fact]
    public void AFullPileIsToppedUpAndTheRemainderStays()
    {
        var b = Build(8913);
        var have = Gold(b, 500);
        have.TrySetProperty("MAXAMOUNT", "600");
        Assert.True(b.Pack.TryAddItem(have));

        var looted = Looted(b, 250);
        looted.TrySetProperty("MAXAMOUNT", "600");
        b.Client.Inventory.HandleItemPickup(looted.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(looted.Uid.Value, -1, -1, 0, b.Pack.Uid.Value);

        var piles = b.Pack.Contents.Where(i => i.ItemType == ItemType.Gold)
                                   .Select(i => (int)i.Amount).OrderBy(a => a).ToList();
        Assert.Equal([150, 600], piles);
    }

    /// <summary>Things that do not stack are still separate items.</summary>
    [Fact]
    public void UnstackableThingsAreNotMerged()
    {
        var b = Build(8914);
        var a = b.World.CreateItem(); a.BaseId = 0x0F5E; a.ItemType = ItemType.WeaponSword;
        Assert.True(b.Pack.TryAddItem(a));

        var pouch = b.World.CreateItem();
        pouch.BaseId = 0x0E76;
        pouch.ItemType = ItemType.Container;
        Assert.True(b.Pack.TryAddItem(pouch));
        var c = b.World.CreateItem(); c.BaseId = 0x0F5E; c.ItemType = ItemType.WeaponSword;
        Assert.True(pouch.TryAddItem(c));
        b.Client.Inventory.HandleItemPickup(c.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(c.Uid.Value, -1, -1, 0, b.Pack.Uid.Value);

        Assert.Equal(2, b.Pack.Contents.Count(i => i.ItemType == ItemType.WeaponSword));
    }
}
