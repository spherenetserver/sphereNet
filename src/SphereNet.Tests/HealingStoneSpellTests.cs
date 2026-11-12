using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit.Abstractions;
using TriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class HealingStoneSpellTests(ITestOutputHelper output)
{
    private const SpellType Spell = SpellType.HealingStone;
    private const ushort StoneGraphic = 0x4078;
    private const short ManaCost = 4;

    private sealed class Fixture : IDisposable
    {
        private readonly ScriptRuntimeStack _runtime = ScriptTestBootstrap.CreateRuntimeStack();
        private readonly List<string> _messages = [];
        public GameWorld World { get; } = TestHarness.CreateWorld();
        public SpellEngine Engine { get; }
        public Character Caster { get; }
        public Item Pack => Caster.Backpack!;
        public int StoneDefinition { get; }
        public string CastDiagnostics =>
            $"CastStart refusal = {Engine.LastCastRefusal ?? "(none)"}, messages = {string.Join(" | ", _messages)}";

        public Fixture(bool hasDefinition = true, bool scripted = false, bool hasBackpack = true,
            IReadOnlyList<string>? referenceFiles = null)
        {
            var registry = new SpellRegistry();
            if (referenceFiles == null)
                LoadFixtureResources(_runtime.Resources, hasDefinition, scripted);
            else
                ScriptTestBootstrap.LoadFiles(_runtime.Resources, referenceFiles);
            new DefinitionLoader(_runtime.Resources, registry).LoadAll();

            StoneDefinition = _runtime.Resources.ResolveDefName("i_healing_stone").Index;
            Engine = new SpellEngine(World, registry)
            {
                TriggerDispatcher = _runtime.Dispatcher,
                OnSysMessage = (_, message) => _messages.Add(message),
            };
            Item.CreateTriggerSourceHook = (item, source) => _runtime.Dispatcher.FireItemTrigger(
                item, ItemTrigger.Create, new TriggerArgs { CharSrc = source, ItemSrc = item });

            Caster = World.CreateCharacter();
            Caster.Name = "Stone caster";
            Caster.IsPlayer = true;
            Caster.PrivLevel = PrivLevel.Player;
            Caster.Int = 100;
            Caster.MaxMana = Caster.Mana = 100;
            Caster.SetSkill(SkillType.Mysticism, 1200);
            Assert.True(World.PlaceCharacter(Caster, new Point3D(100, 100)));

            if (hasBackpack)
            {
                var pack = World.CreateItem();
                pack.BaseId = 0x0E75;
                pack.ItemType = ItemType.Container;
                Assert.True(Caster.Equip(pack, Layer.Pack));
                var book = World.CreateItem();
                book.ItemType = ItemType.SpellbookMystic;
                Assert.True(book.TryLearnSpell((int)Spell));
                Assert.True(pack.TryAddItem(book));
            }
            else
            {
                Character.SpellbookRequiredEnabled = false;
            }
        }

        public Item AddOldStone(Item container)
        {
            var item = World.CreateItem();
            Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, StoneDefinition, fireCreate: false));
            Assert.True(container.TryAddItem(item));
            return item;
        }

        public bool StartCast()
        {
            _messages.Clear();
            if (Engine.CastStart(Caster, Spell, Caster.Uid, Caster.Position) < 0)
                return false;
            // Isolate item creation and the ordinary player's bill from the skill roll.
            Caster.CastSkillSucceeded = true;
            return true;
        }
        public bool Cast() => StartCast() && Engine.CastDone(Caster);

        public GameClient ConnectClient()
        {
            var logs = _runtime.LoggerFactory;
            var client = TestHarness.CreateClient(logs, World, new AccountManager(logs), 679);
            client.SetEngines(spellEngine: Engine, triggerDispatcher: _runtime.Dispatcher);
            TestHarness.AttachCharacter(client, Caster);
            return client;
        }

        public void Dispose() => _runtime.LoggerFactory.Dispose();
    }

    private static void LoadFixtureResources(ResourceHolder resources, bool hasDefinition, bool scripted)
    {
        var file = Path.Combine(Path.GetTempPath(), $"healing_stone_{Guid.NewGuid():N}.scp");
        string spell = $"""
            [SPELL 679]
            DEFNAME=s_healing_stone
            NAME=Healing Stone
            FLAGS=0{(ulong)(scripted ? SpellFlag.Scripted : 0):X}
            MANAUSE={ManaCost}
            CAST_TIME=1.0
            SKILLREQ=MYSTICISM 10.0
            """;
        if (scripted)
            spell += "\nON=@Effect\nTAG.CustomHealingStone=1\nRETURN 0\n";
        if (hasDefinition)
            spell += """

                [ITEMDEF i_healing_stone]
                ID=04078
                NAME=healing stone
                TYPE=28
                ON=@Create
                TAG.CreatedFor=<SRC.NAME>
                MORE1=27
                """;

        try
        {
            File.WriteAllText(file, spell);
            resources.LoadResourceFile(file);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void HealingStoneRequiresTenMysticismFromTheScriptSkillRequirement()
    {
        using var f = new Fixture();
        f.Caster.SetSkill(SkillType.Magery, 1200);
        f.Caster.SetSkill(SkillType.Mysticism, 99);
        Assert.False(f.StartCast());
        Assert.Equal((short)100, f.Caster.Mana);

        f.Caster.SetSkill(SkillType.Mysticism, 100);
        Assert.True(f.StartCast());
    }

    [Fact]
    public void SuccessfulCastUsesItemDefinitionAndCreatesWithCasterAsSource()
    {
        using var f = new Fixture();

        Assert.True(f.Cast(), f.CastDiagnostics);

        var stone = Assert.Single(f.Pack.Contents, item => item.BaseId == StoneGraphic);
        Assert.Equal(ItemType.Spell, stone.ItemType);
        Assert.Equal(f.StoneDefinition, ItemDefHelper.ResolveInstanceDefIndex(stone));
        Assert.Equal(f.Caster.Name, stone.TagValue("CreatedFor"));
        Assert.Equal(27u, stone.More1);
        Assert.Equal((short)(100 - ManaCost), f.Caster.Mana);
    }

    [Fact]
    public void RecastReplacesMatchingStonesOnlyInBackpackSubtree()
    {
        using var f = new Fixture();
        Assert.True(f.Cast(), f.CastDiagnostics);
        var oldTopLevel = Assert.Single(f.Pack.Contents, item => item.BaseId == StoneGraphic);
        var bag = f.World.CreateItem();
        bag.ItemType = ItemType.Container;
        Assert.True(f.Pack.TryAddItem(bag));
        var oldNested = f.AddOldStone(bag);

        var unrelated = f.World.CreateItem();
        unrelated.BaseId = StoneGraphic;
        Assert.True(bag.TryAddItem(unrelated));
        var external = f.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(external, f.StoneDefinition, fireCreate: false));
        Assert.True(f.World.PlaceItem(external, new Point3D(101, 100)));

        Assert.True(f.Cast(), f.CastDiagnostics);

        Assert.True(oldTopLevel.IsDeleted);
        Assert.True(oldNested.IsDeleted);
        Assert.False(external.IsDeleted);
        Assert.Same(unrelated, Assert.Single(bag.Contents));
        var replacement = Assert.Single(f.Pack.Contents, item => item.BaseId == StoneGraphic);
        Assert.NotSame(oldTopLevel, replacement);
        Assert.Equal((short)(100 - ManaCost * 2), f.Caster.Mana);
    }

    [Fact]
    public void LosingManaDuringCastKeepsTheOlderStone()
    {
        using var f = new Fixture();
        var old = f.AddOldStone(f.Pack);
        int itemCount = f.World.TotalItems;
        Assert.True(f.StartCast(), f.CastDiagnostics);
        f.Caster.Mana = ManaCost - 1;

        Assert.False(f.Engine.CastDone(f.Caster));

        Assert.Equal((short)(ManaCost - 1), f.Caster.Mana);
        Assert.Same(old, Assert.Single(f.Pack.Contents, item => item.BaseId == StoneGraphic));
        Assert.Equal(itemCount, f.World.TotalItems);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void MissingDefinitionOrBackpackDoesNotChargeSuccessCost(bool hasDefinition, bool hasBackpack)
    {
        using var f = new Fixture(hasDefinition: hasDefinition, hasBackpack: hasBackpack);
        int itemCount = f.World.TotalItems;

        Assert.False(f.Cast());

        Assert.Equal((short)100, f.Caster.Mana);
        Assert.Equal(itemCount, f.World.TotalItems);
    }

    [Fact]
    public void FullBackpackDoesNotChargeSuccessCost()
    {
        using var f = new Fixture();
        while (f.Pack.ContentCount < Item.MaxContainerItems)
            Assert.True(f.Pack.TryAddItem(f.World.CreateItem()));
        int itemCount = f.World.TotalItems;

        Assert.False(f.Cast());

        Assert.Equal((short)100, f.Caster.Mana);
        Assert.Equal(itemCount, f.World.TotalItems);
    }

    [Fact]
    public void ScriptedHealingStoneUsesItsEffectEvenWithoutItemDefinition()
    {
        using var f = new Fixture(hasDefinition: false, scripted: true);
        int itemCount = f.World.TotalItems;

        Assert.True(f.Cast(), f.CastDiagnostics);

        Assert.Equal("1", f.Caster.TagValue("CustomHealingStone"));
        Assert.Equal((short)(100 - ManaCost), f.Caster.Mana);
        Assert.Equal(itemCount, f.World.TotalItems);
    }

    [Fact]
    public void StockScriptsXCastCreatesAChargedStoneWithAHealingCooldown()
    {
        string? items = TestRepo.Optional("oldSphere/Scripts-X-main/items/i_magic_mysticism.scp");
        string? spells = TestRepo.Optional("oldSphere/Scripts-X-main/spells/spells_mysticism.scp");
        if (Gate.Missing(output, "external script pack", items == null || spells == null))
            return;

        using var f = new Fixture(referenceFiles: [items!, spells!]);
        Character.ReagentsRequiredEnabled = false; // Reagent item definitions are outside these two files.
        f.Caster.SetSkill(SkillType.Focus, 1000);
        f.Caster.SetSkill(SkillType.Imbuing, 400);
        f.Caster.MaxHits = 150;
        f.Caster.Hits = 20;

        Assert.True(f.Cast(), f.CastDiagnostics);

        var stone = Assert.Single(f.Pack.Contents, item => item.BaseId == StoneGraphic);
        Assert.Equal(ItemType.Spell, stone.ItemType);
        Assert.Equal(f.StoneDefinition, ItemDefHelper.ResolveInstanceDefIndex(stone));
        Assert.Equal(128u, stone.More1);
        Assert.Equal(42u, stone.More2);
        Assert.Equal((short)(100 - ManaCost), f.Caster.Mana);

        var client = f.ConnectClient();
        client.HandleDoubleClick(stone.Uid.Value);
        Assert.Equal((short)62, f.Caster.Hits);
        Assert.Equal(86u, stone.More1);
        Assert.Equal(1, stone.MoreP.Z);

        client.HandleDoubleClick(stone.Uid.Value);
        Assert.Equal((short)62, f.Caster.Hits);
        Assert.Equal(86u, stone.More1);
    }
}
