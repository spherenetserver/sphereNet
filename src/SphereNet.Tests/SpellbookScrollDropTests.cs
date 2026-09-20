using System.Buffers.Binary;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SpellbookScrollDropTests
{
    private static (GameWorld World, GameClient Client, Item Pack, Item Book, Item Scroll)
        Build(int spell, ItemType bookType = ItemType.Spellbook, ushort amount = 1)
    {
        var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 8981);
        client.NetState.ClientVersionNumber = 70_090_000;
        var spells = new SpellRegistry();
        spells.Register(new SpellDef { Id = (SpellType)spell, ScrollItemId = 0x1F2D });
        client.SetEngines(spellEngine: new SpellEngine(world, spells));
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.Str = 100;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);
        var book = world.CreateItem();
        book.BaseId = 0x0EFA;
        book.ItemType = bookType;
        pack.TryAddItem(book);
        var scroll = world.CreateItem();
        scroll.BaseId = 0x1F2D;
        scroll.ItemType = ItemType.Scroll;
        scroll.Amount = amount;
        // No MORE1: Source-X resolves SCROLL_ITEM from the spell definitions.
        pack.TryAddItem(scroll);
        return (world, client, pack, book, scroll);
    }

    private static void Drop(GameClient client, Item book, Item scroll)
    {
        client.Inventory.HandleItemPickup(scroll.Uid.Value, 0);
        client.Inventory.HandleItemDrop(scroll.Uid.Value, -1, -1, 0, book.Uid.Value);
    }

    [Theory]
    [InlineData(1, ItemType.Spellbook, 0)]
    [InlineData(32, ItemType.Spellbook, 31)]
    [InlineData(33, ItemType.Spellbook, 32)]
    [InlineData(64, ItemType.Spellbook, 63)]
    [InlineData(101, ItemType.SpellbookNecro, 0)]
    public void DropLearnsCorrectBitConsumesOneAndReopensWithSameContent(int spell, ItemType type, int bit)
    {
        var b = Build(spell, type);
        Drop(b.Client, b.Book, b.Scroll);
        ulong expected = 1UL << bit;
        Assert.Equal(expected, ((ulong)b.Book.More2 << 32) | b.Book.More1);
        Assert.True(b.Scroll.IsDeleted);
        Assert.Empty(b.Book.Contents);
        TestHarness.ClearQueuedPackets(b.Client.NetState);
        b.Client.ItemUse.HandleDoubleClick(b.Book.Uid.Value);
        var packet = Assert.Single(TestHarness.GetQueuedPackets(b.Client.NetState), p => p.Span[0] == 0xBF);
        Assert.Equal(expected, BinaryPrimitives.ReadUInt64LittleEndian(packet.Span[15..]));
    }

    [Fact]
    public void StackConsumesOneAndDuplicateConsumesNothing()
    {
        var b = Build(1, amount: 5);
        Drop(b.Client, b.Book, b.Scroll);
        Assert.Equal(4, b.Scroll.Amount);
        Assert.Equal(b.Pack.Uid, b.Scroll.ContainedIn);
        Drop(b.Client, b.Book, b.Scroll);
        Assert.Equal(4, b.Scroll.Amount);
        Assert.Equal(b.Pack.Uid, b.Scroll.ContainedIn);
        Assert.Empty(b.Book.Contents);
        Assert.Equal(1u, b.Book.More1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WrongSchoolOrUnrecognizedItemIsReturnedWithoutLearning(bool wrongSchool)
    {
        var b = Build(101);
        if (!wrongSchool) b.Scroll.BaseId = 0x0F7A;
        Drop(b.Client, b.Book, b.Scroll);
        Assert.False(b.Scroll.IsDeleted);
        Assert.Equal(b.Pack.Uid, b.Scroll.ContainedIn);
        Assert.Empty(b.Book.Contents);
        Assert.Equal(0u, b.Book.More1);
        Assert.Equal(0u, b.Book.More2);
    }

    [Theory]
    [InlineData(SphereNet.Core.Configuration.SaveFormat.Text)]
    [InlineData(SphereNet.Core.Configuration.SaveFormat.Binary)]
    public void LearnedSpellSurvivesSaveAndLoad(SphereNet.Core.Configuration.SaveFormat format)
    {
        var b = Build(64);
        Drop(b.Client, b.Book, b.Scroll);
        string directory = Path.Combine(Path.GetTempPath(), "spn_book_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var logs = TestHarness.CreateLoggerFactory();
            var saver = new SphereNet.Persistence.Save.WorldSaver(logs) { Format = format, ShardCount = 0 };
            Assert.True(saver.Save(b.World, directory));
            var loaded = TestHarness.CreateWorld();
            new SphereNet.Persistence.Load.WorldLoader(logs).Load(loaded, directory);
            var book = loaded.FindItem(b.Book.Uid);
            Assert.NotNull(book);
            Assert.Equal(0u, book.More1);
            Assert.Equal(0x80000000u, book.More2);
            Assert.Empty(book.Contents);
        }
        finally { Directory.Delete(directory, true); }
    }
}
