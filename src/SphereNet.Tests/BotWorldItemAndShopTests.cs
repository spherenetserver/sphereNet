using System.Buffers.Binary;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Diagnostics;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What the bot client takes from the server's item and shop packets. The bot skipped
/// every 0x1A as a "mobile" (item serials carry 0x40000000) and read no 0xF3 at all, so
/// it never saw an item on the ground; and it read the 0x74 price list without serials,
/// so every purchase it made named item 0.
/// </summary>
public sealed class BotWorldItemAndShopTests
{
    private static void Put16(byte[] b, int at, int v) => BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at), (ushort)v);
    private static void Put32(byte[] b, int at, uint v) => BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at), v);

    [Fact]
    public void AGroundItemFrom0x1AIsKnown()
    {
        var bot = new BotClient(1, NullLogger.Instance);
        // [0x1A][len][serial|0x80000000 amount flag][graphic][amount][x][y][z]
        var p = new byte[16];
        p[0] = 0x1A; Put16(p, 1, p.Length);
        Put32(p, 3, 0x40001234 | 0x80000000);
        Put16(p, 7, 0x0EED); Put16(p, 9, 50); Put16(p, 11, 1500); Put16(p, 13, 1600); p[15] = 5;
        bot.ProcessIncomingPacket(p);

        var item = Assert.Contains(0x40001234u, (IDictionary<uint, BotKnownItem>)bot.World.KnownItems);
        Assert.Equal(0x0EED, item.ItemId);
        Assert.Equal(50, item.Amount);
        Assert.Equal(1500, item.X);
        Assert.Equal(0u, item.ContainerSerial);
    }

    [Fact]
    public void AGroundItemFrom0xF3IsKnown()
    {
        var bot = new BotClient(1, NullLogger.Instance);
        var p = new byte[26];
        p[0] = 0xF3; Put16(p, 1, 1); p[3] = 0;
        Put32(p, 4, 0x40005678); Put16(p, 8, 0x2006); Put16(p, 11, 1); Put16(p, 13, 1);
        Put16(p, 15, 1468); Put16(p, 17, 1670); p[19] = 0; Put16(p, 21, 0x0021);
        bot.ProcessIncomingPacket(p);

        var item = Assert.Contains(0x40005678u, (IDictionary<uint, BotKnownItem>)bot.World.KnownItems);
        Assert.Equal(0x2006, item.ItemId);
        Assert.Equal(1468, item.X);
        Assert.Equal(1670, item.Y);
    }

    /// <summary>The 0x74 entries pair with the shop container's 0x3C items read from
    /// the last one back (as the 2D client pairs them), and the vendor is the serial of
    /// the 0x24 that opens gump 0x30.</summary>
    [Fact]
    public void TheBuyListIsMatchedToTheShopContents()
    {
        var bot = new BotClient(1, NullLogger.Instance);
        const uint Shop = 0x40000100, Vendor = 0x00000200;

        // 0x3C: two items in the shop container (20 bytes each, 6.0.1.7+ layout).
        var c = new byte[5 + 2 * 20];
        c[0] = 0x3C; Put16(c, 1, c.Length); Put16(c, 3, 2);
        (uint Serial, int Graphic)[] stock = [(0x40000101, 0x0E21), (0x40000102, 0x170D)];
        for (int i = 0; i < 2; i++)
        {
            int o = 5 + i * 20;
            Put32(c, o, stock[i].Serial); Put16(c, o + 4, stock[i].Graphic);
            Put16(c, o + 7, 20); Put32(c, o + 14, Shop);
        }
        bot.ProcessIncomingPacket(c);

        // 0x74: entry 0 = the LAST 0x3C item.
        var names = new[] { "sandals", "bandage" };
        var prices = new[] { 29, 5 };
        var list = new List<byte> { 0x74, 0, 0, 0, 0, 0, 0, 2 };
        for (int i = 0; i < 2; i++)
        {
            var pr = new byte[4]; Put32(pr, 0, (uint)prices[i]);
            list.AddRange(pr);
            list.Add((byte)names[i].Length);
            list.AddRange(System.Text.Encoding.ASCII.GetBytes(names[i]));
        }
        var b = list.ToArray();
        Put16(b, 1, b.Length); Put32(b, 3, Shop);
        bot.ProcessIncomingPacket(b);

        // 0x24 (7-byte form): the shop window of the vendor.
        var open = new byte[7];
        open[0] = 0x24; Put32(open, 1, Vendor); Put16(open, 5, 0x0030);
        bot.ProcessIncomingPacket(open);

        var shop = bot.World.ActiveVendor;
        Assert.NotNull(shop);
        Assert.Equal(Vendor, shop!.VendorSerial);
        Assert.Equal(Shop, shop.ContainerSerial);
        Assert.Equal((0x40000102u, (ushort)0x170D, 29), (shop.Items[0].Serial, shop.Items[0].ItemId, shop.Items[0].Price));
        Assert.Equal((0x40000101u, (ushort)0x0E21, 5), (shop.Items[1].Serial, shop.Items[1].ItemId, shop.Items[1].Price));
    }
}
