using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.Trade;
using Microsoft.Extensions.Logging;
using Xunit;

namespace SphereNet.Tests;

// Shares the VendorStateSerial collection with the other VendorEngine-driving
// class so the static VendorEngine.World is never clobbered mid-trade.
[Collection("VendorStateSerial")]
public class VendorTradeTests
{
    private static GameWorld CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        VendorEngine.World = world;
        return world;
    }

    private static (Character vendor, Item stock, Item stockItem) MakeVendorWithStock(
        GameWorld world, ushort baseId, ushort amount, string? price)
    {
        var vendor = new Character { Name = "vendor", NpcBrain = NpcBrainType.Vendor };
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));
        var stock = world.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var stockItem = world.CreateItem();
        stockItem.BaseId = baseId;
        stockItem.Amount = amount;
        if (price != null) stockItem.SetTag("PRICE", price);
        stock.AddItem(stockItem);
        return (vendor, stock, stockItem);
    }

    private static Character MakeBuyerWithGold(GameWorld world, int gold)
    {
        var player = new Character { Name = "buyer" };
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        var pack = world.CreateItem();
        player.Equip(pack, Layer.Pack);
        var coins = world.CreateItem();
        coins.BaseId = 0x0EED;
        coins.ItemType = ItemType.Gold;
        coins.Amount = (ushort)gold;
        pack.AddItem(coins);
        return player;
    }

    [Fact]
    public void Vendor_Buy_DecrementsVirtualStock()
    {
        var world = CreateWorld();
        var (vendor, _, stockItem) = MakeVendorWithStock(world, 0x0F0E, 10, "5");
        var player = MakeBuyerWithGold(world, 1000);

        int cost = VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = stockItem.Uid, ItemId = stockItem.BaseId, Amount = 3, Price = 5 }]);

        Assert.Equal(15, cost);            // 3 * 5
        Assert.Equal(10 - 3, stockItem.Amount); // virtual stock decremented
    }

    [Fact]
    public void Vendor_Buy_DepletedStockEntryIsRemoved()
    {
        var world = CreateWorld();
        var (vendor, _, stockItem) = MakeVendorWithStock(world, 0x0F0E, 4, "5");
        var player = MakeBuyerWithGold(world, 1000);

        int cost = VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = stockItem.Uid, ItemId = stockItem.BaseId, Amount = 4, Price = 5 }]);

        Assert.Equal(20, cost);
        Assert.True(stockItem.IsDeleted); // fully bought out → entry removed
    }

    [Fact]
    public void Vendor_Buy_RejectsSerialNotInVendorStock()
    {
        var world = CreateWorld();
        var (vendor, _, _) = MakeVendorWithStock(world, 0x0F0E, 10, "5");
        var player = MakeBuyerWithGold(world, 1000);

        // A real world item that is NOT inside this vendor's stock container.
        var rogue = world.CreateItem();
        rogue.BaseId = 0x0F0E;
        rogue.Amount = 1;
        world.PlaceItem(rogue, new Point3D(50, 50, 0, 0));

        int result = VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = rogue.Uid, ItemId = rogue.BaseId, Amount = 1, Price = 5 }]);

        Assert.Equal(-1, result); // crafted-serial buy rejected
    }

    [Fact]
    public void Vendor_Buy_RejectsAmountAboveStock()
    {
        var world = CreateWorld();
        var (vendor, _, stockItem) = MakeVendorWithStock(world, 0x0F0E, 2, "5");
        var player = MakeBuyerWithGold(world, 1000);

        int result = VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = stockItem.Uid, ItemId = stockItem.BaseId, Amount = 5, Price = 5 }]);

        Assert.Equal(-1, result);          // not enough in stock
        Assert.Equal(2, stockItem.Amount); // stock untouched on rejection
    }

    [Fact]
    public void Vendor_Buy_NoPriceTag_UsesFallbackPrice()
    {
        var world = CreateWorld();
        // No PRICE tag — server price must fall back instead of rejecting (price>0).
        var (vendor, _, stockItem) = MakeVendorWithStock(world, 0x0F0E, 10, price: null);
        var player = MakeBuyerWithGold(world, 100000);

        int cost = VendorEngine.ProcessBuy(player, vendor,
            [new TradeEntry { ItemUid = stockItem.Uid, ItemId = stockItem.BaseId, Amount = 1, Price = 0 }]);

        Assert.True(cost > 0);             // fallback (baseId/10+5) priced, not rejected
        Assert.Equal(9, stockItem.Amount);
    }

    [Fact]
    public void Vendor_Sell_RejectsNonEmptyContainer_NoItemLoss()
    {
        var world = CreateWorld();
        var vendor = new Character { Name = "vendor", NpcBrain = NpcBrainType.Vendor };
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0)); // no BUY list => buys anything

        var seller = new Character { Name = "seller" };
        world.PlaceCharacter(seller, new Point3D(101, 100, 0, 0));
        var pack = world.CreateItem();
        seller.Equip(pack, Layer.Pack);

        // A bag holding a valuable item, sitting in the seller's backpack.
        var bag = world.CreateItem();
        bag.ItemType = ItemType.Container;
        bag.BaseId = 0x0E75;
        pack.AddItem(bag);
        var loot = world.CreateItem();
        loot.BaseId = 0x0F0E;
        loot.Amount = 1;
        bag.AddItem(loot);

        int paid = VendorEngine.ProcessSell(seller, vendor,
            [new TradeEntry { ItemUid = bag.Uid, ItemId = bag.BaseId, Amount = 1, Price = 1 }]);

        // The sale is refused, and neither the bag nor its contents are destroyed —
        // without the guard, Delete()-ing the bag would have silently eaten the loot.
        Assert.Equal(0, paid);
        Assert.False(bag.IsDeleted);
        Assert.False(loot.IsDeleted);
    }

    [Fact]
    public void Vendor_Sell_AllowsEmptyContainer()
    {
        var world = CreateWorld();
        var vendor = new Character { Name = "vendor", NpcBrain = NpcBrainType.Vendor };
        vendor.SetTag("VENDOR_GOLD", "1000"); // W-F: purse always tracked — fund it
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

        var seller = new Character { Name = "seller" };
        world.PlaceCharacter(seller, new Point3D(101, 100, 0, 0));
        var pack = world.CreateItem();
        seller.Equip(pack, Layer.Pack);

        // An EMPTY container is still sellable (matches ServUO: only non-empty is barred).
        var emptyBag = world.CreateItem();
        emptyBag.ItemType = ItemType.Container;
        emptyBag.BaseId = 0x0E75;
        emptyBag.SetTag("PRICE", "10");
        pack.AddItem(emptyBag);

        int paid = VendorEngine.ProcessSell(seller, vendor,
            [new TradeEntry { ItemUid = emptyBag.Uid, ItemId = emptyBag.BaseId, Amount = 1, Price = 5 }]);

        Assert.True(paid > 0);
        Assert.True(emptyBag.IsDeleted); // sold and consumed
    }
}
