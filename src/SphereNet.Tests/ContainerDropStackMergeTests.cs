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

    /// <summary>A script putting an item in a container stacks the same way a drop
    /// does. Upstream does it inside ContentAdd, so it happens however the item got
    /// there (CItemContainer.cpp:618-634); here the merge lived only in the client's
    /// drop handler, so CONT= left a second pile - which is what a script handing out
    /// gold produced, every time.</summary>
    [Fact]
    public void AScriptSettingContStacksToo()
    {
        var b = Build(8915);
        var have = Gold(b, 500);
        Assert.True(b.Pack.TryAddItem(have));

        var fresh = Gold(b, 250);
        b.World.PlaceItem(fresh, b.Me.Position);
        Assert.True(fresh.TrySetProperty("CONT", $"0{b.Pack.Uid.Value:X}"));

        var piles = b.Pack.Contents.Where(i => i.ItemType == ItemType.Gold)
                                   .Select(i => (int)i.Amount).ToList();
        Assert.Equal([750], piles);
    }

    /// <summary>And the remainder still lands when the pile cannot take it all.</summary>
    [Fact]
    public void AScriptContLeavesTheRemainder()
    {
        var b = Build(8916);
        var have = Gold(b, 500);
        have.TrySetProperty("MAXAMOUNT", "600");
        Assert.True(b.Pack.TryAddItem(have));

        var fresh = Gold(b, 250);
        fresh.TrySetProperty("MAXAMOUNT", "600");
        b.World.PlaceItem(fresh, b.Me.Position);
        fresh.TrySetProperty("CONT", $"0{b.Pack.Uid.Value:X}");

        var piles = b.Pack.Contents.Where(i => i.ItemType == ItemType.Gold)
                                   .Select(i => (int)i.Amount).OrderBy(a => a).ToList();
        Assert.Equal([150, 600], piles);
    }

    /// <summary>Dropping a pile on YOURSELF - the paperdoll, or your own body - merges
    /// it too. Upstream's pack add for a character takes no slot at all
    /// (GetPackSafe()->ContentAdd, the two-argument overload, which stacks), and every
    /// path that hands a character an item goes through it: the drop on oneself, a gift
    /// from an NPC, a bounce. None of them merged here, so gold and arrows dropped on
    /// oneself sat beside the pile already in the pack - which is how a shard reported
    /// it, for both.</summary>
    [Fact]
    public void GoldDroppedOnYourselfJoinsThePile()
    {
        var b = Build(8917);
        var have = Gold(b, 500);
        Assert.True(b.Pack.TryAddItem(have));

        var looted = Looted(b, 250);
        b.Client.Inventory.HandleItemPickup(looted.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(looted.Uid.Value, -1, -1, 0, b.Me.Uid.Value);

        var piles = b.Pack.Contents.Where(i => i.ItemType == ItemType.Gold)
                                   .Select(i => (int)i.Amount).ToList();
        Assert.Equal([750], piles);
    }

    /// <summary>Arrows behave the same way - the report named them alongside gold, and
    /// nothing about the merge is specific to coin.</summary>
    [Fact]
    public void ArrowsDroppedOnYourselfJoinThePile()
    {
        var b = Build(8918);
        b.World.MapData!.SetSyntheticItemTile(0x0F3F, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Generic, Weight = 0
        });

        Item Arrows(ushort n)
        {
            var it = b.World.CreateItem();
            it.BaseId = 0x0F3F;
            it.ItemType = ItemType.WeaponArrow;
            it.Amount = n;
            return it;
        }

        var have = Arrows(40);
        Assert.True(b.Pack.TryAddItem(have));

        var pouch = b.World.CreateItem();
        pouch.BaseId = 0x0E76; pouch.ItemType = ItemType.Container;
        Assert.True(b.Pack.TryAddItem(pouch));
        var loose = Arrows(15);
        Assert.True(pouch.TryAddItem(loose));

        b.Client.Inventory.HandleItemPickup(loose.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(loose.Uid.Value, -1, -1, 0, b.Me.Uid.Value);

        var piles = b.Pack.Contents.Where(i => i.ItemType == ItemType.WeaponArrow)
                                   .Select(i => (int)i.Amount).ToList();
        Assert.Equal([55], piles);
    }

    [Fact]
    public void PurchasedArrowsMergeWithLootWithoutPriceTags()
    {
        var b = Build(8932);
        SphereNet.Game.Trade.VendorEngine.World = b.World;
        b.World.MapData!.SetSyntheticItemTile(0x0F3F, new SphereNet.MapData.Tiles.ItemTileData
        { Flags = SphereNet.MapData.Tiles.TileFlag.Generic, Weight = 0 });
        var loot = b.World.CreateItem();
        loot.BaseId = 0x0F3F;
        loot.ItemType = ItemType.WeaponArrow;
        loot.Amount = 20;
        b.Pack.TryAddItem(loot);
        b.Pack.TryAddItem(Gold(b, 100));
        var vendor = b.World.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        b.World.PlaceCharacter(vendor, b.Me.Position);
        var stock = b.World.CreateItem();
        stock.ItemType = ItemType.Container;
        stock.BaseId = 0x0E75;
        vendor.Equip(stock, Layer.VendorStock);
        var row = b.World.CreateItem();
        row.BaseId = 0x0F3F;
        row.ItemType = ItemType.WeaponArrow;
        row.Name = "arrow%s";
        row.Amount = 10;
        row.Price = 3;
        stock.AddItem(row);

        int cost = SphereNet.Game.Trade.VendorEngine.ProcessBuy(b.Me, vendor,
            [new SphereNet.Game.Trade.TradeEntry { ItemUid = row.Uid, ItemId = row.BaseId, Amount = 5 }]);

        Assert.Equal(15, cost);
        Assert.Equal(25, loot.Amount);
        Assert.Single(b.Pack.Contents, i => i.ItemType == ItemType.WeaponArrow);
        Assert.False(loot.TryGetTag("PRICE", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveredAmmoPreservesPurchasedStackIdentity(bool groundRecovery)
    {
        var b = Build(8933);
        b.World.MapData!.SetSyntheticItemTile(0x0F3F, new SphereNet.MapData.Tiles.ItemTileData
        { Flags = SphereNet.MapData.Tiles.TileFlag.Generic, Weight = 0 });
        var original = b.World.CreateItem();
        original.BaseId = 0x0F3F;
        original.ItemType = ItemType.WeaponArrow;
        original.Amount = 20;
        original.Hue = new Color(42);
        original.SetTag("ITEMDEF", "i_arrow");
        original.SetTag("SPECIAL_AMMO", "enchanted");
        original.More1 = 123;
        original.Price = 7;
        b.Pack.AddItem(original);

        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        Item recovered;
        if (groundRecovery)
        {
            typeof(SphereNet.Game.Clients.ClientCombatHandler).GetMethod("DropRecoveredAmmo", flags)!
                .Invoke(b.Client.Combat, [original, b.Me.Position]);
            recovered = Assert.Single(b.World.GetItemsInRange(b.Me.Position, 0), i => i.BaseId == original.BaseId);
        }
        else
        {
            recovered = (Item)typeof(SphereNet.Game.Clients.ClientCombatHandler).GetMethod("CreateRecoveredAmmo", flags)!
                .Invoke(b.Client.Combat, [original])!;
            var corpse = b.World.CreateItem();
            corpse.ItemType = ItemType.Container;
            b.Pack.AddItem(corpse);
            corpse.AddItem(recovered);
        }
        original.Amount--; // the shot spent one unit; recovery must not mint ammo
        Assert.Equal(1, recovered.Amount);
        Assert.True(original.CanStackWith(recovered));
        Assert.Equal(original.Price, recovered.Price);
        Assert.Equal("enchanted", recovered.Tags.Get("SPECIAL_AMMO"));
        b.Client.Inventory.HandleItemPickup(recovered.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(recovered.Uid.Value, -1, -1, 0, b.Me.Uid.Value);
        Assert.Equal(20, original.Amount);
        Assert.True(recovered.IsDeleted);
    }

    [Theory]
    [InlineData("self", false)]
    [InlineData("pack", false)]
    [InlineData("pile", false)]
    [InlineData("self", true)]
    [InlineData("pack", true)]
    [InlineData("pile", true)]
    public void ArrowInstanceNameDoesNotPreventSourceXStacking(string target, bool vendorPrice)
    {
        var b = Build(8929);
        b.World.MapData!.SetSyntheticItemTile(0x0F3F, new SphereNet.MapData.Tiles.ItemTileData
        { Flags = SphereNet.MapData.Tiles.TileFlag.Generic, Weight = 0 });
        var existing = b.World.CreateItem();
        existing.BaseId = 0x0F3F;
        existing.ItemType = ItemType.WeaponArrow;
        existing.Amount = 40;
        if (vendorPrice)
        {
            existing.Price = 3;
        }
        b.Pack.TryAddItem(existing);
        var incoming = b.World.CreateItem();
        incoming.BaseId = 0x0F3F;
        incoming.ItemType = ItemType.WeaponArrow;
        incoming.Name = "arrow%s"; // ApplyInstanceMetadata stamps the script NAME.
        incoming.Amount = 15;
        b.Pack.TryAddItem(incoming);

        b.Client.Inventory.HandleItemPickup(incoming.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(incoming.Uid.Value, -1, -1, 0,
            target == "self" ? b.Me.Uid.Value : target == "pack" ? b.Pack.Uid.Value : existing.Uid.Value);

        Assert.Equal(55, existing.Amount);
        Assert.True(incoming.IsDeleted);
        Assert.Single(b.Pack.Contents);
        Assert.Contains(TestHarness.GetQueuedPackets(b.Client.NetState), p =>
            p.Span[0] == 0x25 && System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.Span[1..]) == existing.Uid.Value &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(p.Span[8..]) == 55);
    }

    [Theory]
    [InlineData(ObjAttributes.Newbie, false)]
    [InlineData(ObjAttributes.Magic, false)]
    [InlineData(ObjAttributes.Decay, true)]
    public void SourceXStackingComparesAttributesExceptDecay(ObjAttributes difference, bool merges)
    {
        var b = Build(8930);
        var a = Gold(b, 20);
        var other = Gold(b, 10);
        other.SetAttr(difference);
        Assert.Equal(merges, a.CanStackWith(other));
    }

    [Theory]
    [InlineData("PRICE", "3")] // Even a tag equal to native PRICE is custom identity.
    [InlineData("PRICE", "9")]
    [InlineData("OWNER", "123")]
    public void CustomTagsStillPreventStacking(string tag, string value)
    {
        var b = Build(8931);
        var a = Gold(b, 20);
        var other = Gold(b, 10);
        other.Price = 3;
        other.SetTag(tag, value);
        Assert.False(a.CanStackWith(other));
        Assert.False(other.CanStackWith(a));
    }

    /// <summary>Coin absorbed into a pile on the way into the pack still redraws the
    /// status bar. The add returns early once the pile has taken everything, and the
    /// status send sat after it - so the merge itself would have stopped the figure from
    /// moving.</summary>
    [Fact]
    public void AFullyAbsorbedPileStillRedrawsTheStatusBar()
    {
        var b = Build(8919);
        var have = Gold(b, 500);
        Assert.True(b.Pack.TryAddItem(have));
        var looted = Looted(b, 250);

        b.Client.Inventory.HandleItemPickup(looted.Uid.Value, 0);
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.Inventory.HandleItemDrop(looted.Uid.Value, -1, -1, 0, b.Me.Uid.Value);

        Assert.Contains(TestHarness.GetQueuedPackets(b.Client.NetState),
            pkt => pkt.Span[0] == 0x11);   // 0x11 StatusFull carries the gold
    }

    /// <summary>The remainder still lands in the pack when the pile cannot take it
    /// all - a drop on oneself must never eat the difference.</summary>
    [Fact]
    public void ADropOnYourselfLeavesTheRemainder()
    {
        var b = Build(8921);
        var have = Gold(b, 500);
        have.TrySetProperty("MAXAMOUNT", "600");
        Assert.True(b.Pack.TryAddItem(have));

        var looted = Looted(b, 250);
        looted.TrySetProperty("MAXAMOUNT", "600");
        b.Client.Inventory.HandleItemPickup(looted.Uid.Value, 0);
        b.Client.Inventory.HandleItemDrop(looted.Uid.Value, -1, -1, 0, b.Me.Uid.Value);

        var piles = b.Pack.Contents.Where(i => i.ItemType == ItemType.Gold)
                                   .Select(i => (int)i.Amount).OrderBy(a => a).ToList();
        Assert.Equal([150, 600], piles);
    }
}
