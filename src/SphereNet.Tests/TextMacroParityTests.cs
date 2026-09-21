using System.Buffers.Binary;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class TextMacroParityTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _logs = TestHarness.CreateLoggerFactory();
        public readonly GameWorld World = TestHarness.CreateWorld();
        public readonly GameClient Client;
        public readonly Character Player;
        public Fixture()
        {
            Client = TestHarness.CreateClient(_logs, World, new AccountManager(_logs), 19547);
            Player = World.CreateCharacter(); Player.BodyId = 0x190; Player.IsPlayer = true;
            World.PlaceCharacter(Player, new Point3D(100, 100));
            TestHarness.AttachCharacter(Client, Player);
            var pack = World.CreateItem(); pack.BaseId = 0x0E75; pack.ItemType = ItemType.Container;
            Player.Backpack = pack; Player.Equip(pack, Layer.Pack);
            Client.BroadcastNearby = (_, _, packet, _) => Client.Send(packet);
        }
        public Item Book(ItemType type = ItemType.Spellbook, uint spells = 0)
        {
            var book = World.CreateItem(); book.BaseId = 0x0EFA; book.ItemType = type; book.More1 = spells;
            Player.Backpack!.TryAddItem(book);
            return book;
        }
        public uint? OpenedBook()
        {
            var packets = TestHarness.GetQueuedPackets(Client.NetState).Where(p => p.Span[0] == 0x24).ToArray();
            return packets.Length == 0 ? null : BinaryPrimitives.ReadUInt32BigEndian(Assert.Single(packets).Span[1..]);
        }
        public void Dispose() => _logs.Dispose();
    }

    [Theory]
    [InlineData(1, ItemType.Spellbook)]
    [InlineData(2, ItemType.SpellbookNecro)]
    [InlineData(3, ItemType.SpellbookPala)]
    [InlineData(4, ItemType.SpellbookBushido)]
    [InlineData(5, ItemType.SpellbookNinjitsu)]
    [InlineData(6, ItemType.SpellbookArcanist)]
    [InlineData(7, ItemType.SpellbookMystic)]
    [InlineData(8, ItemType.SpellbookMastery)]
    [InlineData(99, ItemType.Spellbook)]
    public void OpenBookMacroSelectsSchoolEvenIfBookIsEmpty(int school, ItemType type)
    {
        using var f = new Fixture();
        f.Book(type == ItemType.Spellbook ? ItemType.SpellbookNecro : ItemType.Spellbook);
        var selected = f.Book(type);
        f.Client.HandleTextCommand(0x43, school.ToString());
        Assert.Equal(selected.Uid.Value, f.OpenedBook());
    }

    [Fact]
    public void BookWithRequestedSpellWinsOverEmptyHandBook()
    {
        using var f = new Fixture();
        var hand = f.Book(); f.Player.Equip(hand, Layer.OneHanded);
        var selected = f.Book(spells: 1);
        f.Client.HandleTextCommand(0x43, "1");
        Assert.Equal(selected.Uid.Value, f.OpenedBook());
    }

    [Fact]
    public void HandBookWithSpellWinsOverPackBook()
    {
        using var f = new Fixture();
        var hand = f.Book(spells: 1); f.Player.Equip(hand, Layer.OneHanded);
        f.Book(spells: 1);
        f.Client.HandleTextCommand(0x43, "1");
        Assert.Equal(hand.Uid.Value, f.OpenedBook());
    }

    [Fact]
    public void BookMacroDoesNotSearchNestedContainers()
    {
        using var f = new Fixture();
        var nested = f.World.CreateItem(); nested.ItemType = ItemType.Container;
        f.Player.Backpack!.TryAddItem(nested);
        nested.TryAddItem(f.Book());
        f.Client.HandleTextCommand(0x43, "1");
        Assert.Null(f.OpenedBook());
    }

    [Theory]
    [InlineData("bow", 0x20)]
    [InlineData("SALUTE", 0x21)]
    [InlineData("bowing", 0x20)]
    public void EmoteMacroUsesNormalAnimationPath(string command, ushort action)
    {
        using var f = new Fixture();
        f.Client.HandleTextCommand(0xC7, command);
        var packet = Assert.Single(TestHarness.GetQueuedPackets(f.Client.NetState), p => p.Span[0] == 0x6E);
        Assert.Equal(action, BinaryPrimitives.ReadUInt16BigEndian(packet.Span[5..]));
    }
}
