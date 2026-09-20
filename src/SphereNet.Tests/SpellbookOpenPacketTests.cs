using System.Buffers.Binary;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Items;
using SphereNet.Network.Packets.Outgoing;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SpellbookOpenPacketTests
{
    [Theory]
    [InlineData(ItemType.Spellbook, 0x0EFA, 1)]
    [InlineData(ItemType.SpellbookNecro, 0x2253, 101)]
    [InlineData(ItemType.SpellbookPala, 0x2252, 201)]
    public void DoubleClickAnnouncesBookThenOpensSpellbookThenSendsContents(
        ItemType type, ushort graphic, ushort offset)
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 8980);
        client.NetState.ClientVersionNumber = 70_090_000;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        var book = world.CreateItem();
        book.BaseId = graphic;
        book.ItemType = type;
        book.More1 = 1;
        book.More2 = 0x80000000;
        pack.TryAddItem(book);
        TestHarness.ClearQueuedPackets(client.NetState);

        client.ItemUse.HandleDoubleClick(book.Uid.Value);

        var packets = TestHarness.GetQueuedPackets(client.NetState).ToArray();
        var open = Assert.Single(packets, p => p.Span[0] == 0x24);
        Assert.Equal(book.Uid.Value, BinaryPrimitives.ReadUInt32BigEndian(open.Span[1..]));
        Assert.Equal(0xFFFF, BinaryPrimitives.ReadUInt16BigEndian(open.Span[5..]));
        var content = Assert.Single(packets, p => p.Span[0] == 0xBF &&
            BinaryPrimitives.ReadUInt16BigEndian(p.Span[3..]) == 0x1B);
        Assert.Equal(offset, BinaryPrimitives.ReadUInt16BigEndian(content.Span[13..]));
        var item = Assert.Single(packets, p => p.Span[0] == 0x25);
        Assert.True(Array.IndexOf(packets, item) < Array.IndexOf(packets, open));
        Assert.True(Array.IndexOf(packets, open) < Array.IndexOf(packets, content));
    }

    [Fact]
    public void SpellMaskUsesSourceXLittleEndianByteOrder()
    {
        // Non-symmetric bytes catch both reversed halves and reversed bytes.
        var packet = new PacketSpellbookContent(0x40000001, 0x0EFA, 1,
            0x8040201008040201UL).Build();
        Assert.Equal(new byte[] { 1, 2, 4, 8, 16, 32, 64, 128 }, packet.Span[15..23].ToArray());
    }
}
