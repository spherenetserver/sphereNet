using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Buying or selling closes the shop window.
///
/// Upstream sends the buy-list packet with a count of zero when a purchase or a sale
/// completes (addVendorClose, CClientMsg.cpp:2386; called at CClientEvent.cpp:1413 and
/// :1566), and the client disposes the shop gump for that vendor
/// (CloseVendorInterface). Nothing closed it here, so the window stayed open over stock
/// that had already been bought and offered the player a list the vendor no longer had.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class VendorWindowCloseTests
{
    private static (SphereNet.Game.Clients.GameClient Client,
                    SphereNet.Game.Objects.Characters.Character Vendor,
                    Item Goods,
                    SphereNet.Game.World.GameWorld World) Shop(int port)
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), port);
        var buyer = world.CreateCharacter();
        buyer.IsPlayer = true;
        world.PlaceCharacter(buyer, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, buyer);

        // A vendor with something to sell: stock lives in the vendor stock container,
        // the way the vendor engine reads it.
        VendorEngine.World = world;
        var vendor = world.CreateCharacter();
        vendor.Name = "vendor";
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));
        var stock = world.CreateItem();
        vendor.Equip(stock, Layer.VendorStock);
        var goods = world.CreateItem();
        goods.BaseId = 0x0F0E;
        goods.Amount = 10;
        goods.SetTag("PRICE", "5");
        stock.AddItem(goods);

        // And a buyer who can pay for it.
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        buyer.Equip(pack, Layer.Pack);
        var coins = world.CreateItem();
        coins.BaseId = 0x0EED;
        coins.ItemType = ItemType.Gold;
        coins.Amount = 5000;
        pack.AddItem(coins);

        TestHarness.GetQueuedPackets(client.NetState).Clear();
        return (client, vendor, goods, world);
    }

    /// <summary>A completed purchase ends with the window closed.</summary>
    [Fact]
    public void BuyingClosesTheWindow()
    {
        var (client, vendor, goods, _) = Shop(5401);

        client.HandleVendorBuy(vendor.Uid.Value, 0x02,
            [new SphereNet.Network.Packets.Incoming.VendorBuyEntry
                { Layer = 0x1A, ItemSerial = goods.Uid.Value, Amount = 1 }]);

        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState),
            p => p.Span[0] == 0x3B);
    }

    /// <summary>And a sale does the same. The buyer offers something out of their own
    /// pack, which is what the sell list names.</summary>
    [Fact]
    public void SellingClosesTheWindow()
    {
        var (client, vendor, _, world) = Shop(5402);
        var owner = world.FindChar(new Serial(client.Character!.Uid.Value))!;
        var mine = world.CreateItem();
        mine.BaseId = 0x0F0E;
        mine.Amount = 1;
        Assert.True(owner.Backpack!.TryAddItem(mine));

        client.HandleVendorSell(vendor.Uid.Value,
            [new SphereNet.Network.Packets.Incoming.VendorSellEntry
                { ItemSerial = mine.Uid.Value, Amount = 1 }]);

        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState),
            p => p.Span[0] == 0x3B);
    }

    /// <summary>The packet names the vendor whose window to close, and carries a count
    /// of zero - which is what makes the client close it rather than redraw a list.</summary>
    [Fact]
    public void ThePacketNamesTheVendorAndCarriesNoItems()
    {
        var (client, vendor, goods, _) = Shop(5403);

        client.HandleVendorBuy(vendor.Uid.Value, 0x02,
            [new SphereNet.Network.Packets.Incoming.VendorBuyEntry
                { Layer = 0x1A, ItemSerial = goods.Uid.Value, Amount = 1 }]);

        var close = Assert.Single(TestHarness.GetQueuedPackets(client.NetState),
            p => p.Span[0] == 0x3B);

        Assert.Equal(8, close.Span.Length);          // header(3) + serial(4) + count(1)
        uint serial = (uint)((close.Span[3] << 24) | (close.Span[4] << 16) |
                             (close.Span[5] << 8) | close.Span[6]);
        Assert.Equal(vendor.Uid.Value, serial);
        Assert.Equal(0, close.Span[7]);
    }
}
