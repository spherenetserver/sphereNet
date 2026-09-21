using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SpellbookSchoolParityTests
{
    private sealed class Fixture : IDisposable
    {
        private readonly Microsoft.Extensions.Logging.ILoggerFactory _logs = TestHarness.CreateLoggerFactory();
        public readonly GameWorld World = TestHarness.CreateWorld();
        public readonly Character Player;
        public readonly SpellEngine Engine;
        public readonly SpellDef Def;
        public string? Message;
        public Fixture(int spell)
        {
            Player = World.CreateCharacter(); Player.IsPlayer = true;
            Player.MaxMana = Player.Mana = 100;
            World.PlaceCharacter(Player, new Point3D(100, 100));
            var pack = World.CreateItem(); pack.ItemType = ItemType.Container;
            Player.Equip(pack, Layer.Pack);
            var registry = new SpellRegistry();
            Def = new SpellDef { Id = (SpellType)spell, Name = "Probe", Flags = SpellFlag.Good, HasScriptedStages = true, CastTimeBase = 1 };
            registry.Register(Def);
            Engine = new SpellEngine(World, registry);
            Engine.OnSysMessage = (_, text) => Message = text;
        }
        public Item Book(ItemType type)
        {
            var item = World.CreateItem(); item.ItemType = type;
            Player.Backpack!.AddItem(item);
            return item;
        }
        public int Start() => Engine.CastStart(Player, Def.Id, Player.Uid, Player.Position);
        public void Dispose() { ServerMessages.ClearOverrides(); _logs.Dispose(); }
    }

    [Theory]
    [InlineData(1, ItemType.Spellbook)]
    [InlineData(101, ItemType.SpellbookNecro)]
    [InlineData(201, ItemType.SpellbookPala)]
    [InlineData(401, ItemType.SpellbookBushido)]
    [InlineData(501, ItemType.SpellbookNinjitsu)]
    [InlineData(601, ItemType.SpellbookArcanist)]
    [InlineData(678, ItemType.SpellbookMystic)]
    [InlineData(701, ItemType.SpellbookMastery)]
    public void EverySchoolRequiresBookAndLearnedSpell(int spell, ItemType type)
    {
        using var f = new Fixture(spell);
        ServerMessages.SetOverride(Msg.SpellTryNobook, "missing book");
        ServerMessages.SetOverride(Msg.SpellTryNotyourbook, "missing spell");
        Assert.Equal(-1, f.Start());
        Assert.Equal("missing book", f.Message);
        var book = f.Book(type);
        Assert.Equal(-1, f.Start());
        Assert.Equal("missing spell", f.Message);
        Assert.True(book.TryLearnSpell(spell));
        Assert.True(f.Engine.HasSpellInBook(f.Player, spell));
        Assert.True(f.Start() >= 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    [InlineData(129)]
    public void SpellIdsDoNotWrapAroundClassicMask(int spell)
    {
        using var f = new Fixture(1);
        var book = f.Book(ItemType.Spellbook); book.More1 = uint.MaxValue; book.More2 = uint.MaxValue;
        Assert.False(f.Engine.HasSpellInBook(f.Player, spell));
    }

    [Fact]
    public void MysticismStartsAt678AndUsesFirstBit()
    {
        using var f = new Fixture(678);
        var book = f.Book(ItemType.SpellbookMystic);
        Assert.Equal(677, book.SpellbookOffset);
        Assert.False(book.TryLearnSpell(677));
        Assert.True(book.TryLearnSpell(678));
        Assert.Equal(1u, book.More1);
    }

    [Fact]
    public void MissingReagentUsesFormattedMessageOverride()
    {
        using var f = new Fixture(1);
        f.Def.Reagents.Add(0x7FFE, 1);
        ServerMessages.SetOverride(Msg.SpellTryNoregs, "missing=%s");
        Assert.Equal(-1, f.Start());
        Assert.Equal("missing=07FFE", f.Message);
    }

    [Fact]
    public void GmStillBypassesBookRequirement()
    {
        using var f = new Fixture(101);
        f.Player.PrivLevel = PrivLevel.GM;
        Assert.True(f.Start() >= 0);
    }
}
