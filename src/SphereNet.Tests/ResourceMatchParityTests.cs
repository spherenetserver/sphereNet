using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// One resource walk and one resource match for crafting, the CONTCONSUME / RESCOUNT /
/// RESTEST / SKILLTEST script surface and gold - Source-X CItem::IsResourceMatch
/// (CItem.cpp:6034) and CContainer::ContentConsumeTest / ContentConsume
/// (CContainer.cpp:374-473).
///
///   * the walk never reaches into a locked (or otherwise unsearchable) box, and the
///     test and the spend walk the same tree;
///   * a recipe input is the ITEMDEF it names, not its graphic - two definitions on one
///     art id are two resources; a TYPE input stays a TYPE;
///   * boards count as logs and leather as hides (never the other way) unless
///     EF_Item_Strict_Comparison is set;
///   * gold skips a locked box but still pays from the bank, and PAYFROMPACKONLY keeps
///     it to the pack.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ResourceMatchParityTests
{
    private sealed class ServerConsole : SphereNet.Core.Interfaces.ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public void SysMessage(string text) { }
        public string GetName() => "server";
    }

    private static readonly string Script = $$"""
        [TYPEDEFS]
        t_ingot={{(int)ItemType.Ingot}}

        [ITEMDEF 01bf2]
        DEFNAME=i_ingot_iron
        NAME=iron ingot
        TYPE=t_ingot

        [ITEMDEF i_audit_rare]
        ID=01bf2
        NAME=rare ingot
        TYPE=t_ingot

        [ITEMDEF i_rare_widget]
        ID=0f51
        NAME=rare widget
        SKILLMAKE=Tinkering 0.0
        RESOURCES=5 i_audit_rare

        [ITEMDEF i_iron_widget]
        ID=0f52
        NAME=iron widget
        SKILLMAKE=Tinkering 0.0
        RESOURCES=5 i_ingot_iron

        [ITEMDEF i_any_widget]
        ID=0f53
        NAME=any widget
        SKILLMAKE=Tinkering 0.0
        RESOURCES=5 t_ingot

        [ITEMDEF 01bdd]
        DEFNAME=i_log
        NAME=log

        [ITEMDEF 01bd7]
        DEFNAME=i_board
        NAME=board

        [ITEMDEF 01078]
        DEFNAME=i_hides
        NAME=hides

        [ITEMDEF 01067]
        DEFNAME=i_leather
        NAME=leather

        [ITEMDEF i_log_thing]
        ID=0f54
        NAME=log thing
        SKILLMAKE=Carpentry 0.0
        RESOURCES=3 i_log

        [ITEMDEF i_board_thing]
        ID=0f55
        NAME=board thing
        SKILLMAKE=Carpentry 0.0
        RESOURCES=3 i_board

        [ITEMDEF i_hide_thing]
        ID=0f56
        NAME=hide thing
        SKILLMAKE=Tailoring 0.0
        RESOURCES=3 i_hides

        [ITEMDEF i_leather_thing]
        ID=0f57
        NAME=leather thing
        SKILLMAKE=Tailoring 0.0
        RESOURCES=3 i_leather

        [ITEMDEF i_numeric_widget]
        ID=0f58
        NAME=numeric widget
        SKILLMAKE=Tinkering 0.0
        RESOURCES=2 i_ingot_iron, 3 01bdd, 4 i_log

        [ITEMDEF i_skilled_widget]
        ID=0f59
        NAME=skilled widget
        SKILLMAKE=Tinkering 0.0
        RESOURCES=Carpentry 50.0, 1 i_ingot_iron
        """;

    private sealed record Bench(GameWorld World, CraftingEngine Engine, ResourceHolder Resources,
        Character Crafter, Item Pack);

    private static Bench Load()
    {
        using var loggerFactory = LoggerFactory.Create(_ => { });
        string path = Path.Combine(Path.GetTempPath(), $"spherenet_resmatch_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, Script);
        try
        {
            var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>())
            {
                ScpBaseDir = Path.GetDirectoryName(path) ?? ""
            };
            resources.LoadResourceFile(path);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

            var world = new GameWorld(loggerFactory);
            world.InitMap(0, 6144, 4096);
            SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
            Item.ResolveWorld = () => world;

            var engine = new CraftingEngine(world);
            Assert.Equal(9, engine.LoadRecipesFromDefs(resources));

            var crafter = world.CreateCharacter();
            crafter.IsPlayer = true;
            crafter.PrivLevel = PrivLevel.GM;        // removes the random craft failure only
            crafter.SetSkill(SkillType.Tinkering, 1000);
            crafter.SetSkill(SkillType.Carpentry, 1000);
            crafter.SetSkill(SkillType.Tailoring, 1000);
            world.PlaceCharacter(crafter, new Point3D(100, 100, 0, 0));

            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            crafter.Equip(pack, Layer.Pack);
            return new Bench(world, engine, resources, crafter, pack);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static int Def(Bench b, string defname)
    {
        var rid = b.Resources.ResolveDefName(defname);
        Assert.True(rid.IsValid, defname);
        return rid.Index;
    }

    private static CraftRecipe Recipe(Bench b, string defname) =>
        b.Engine.TryGetRecipe(Def(b, defname)) ?? throw new Xunit.Sdk.XunitException(defname);

    /// <summary>A stack made FROM a definition, as NEWITEM / a craft / a loot roll makes
    /// it (the definition's identity is stamped on the instance).</summary>
    private static Item Stack(Bench b, Item container, string defname, int amount)
    {
        int index = Def(b, defname);
        var item = b.World.CreateItem();
        item.BaseId = ItemDefHelper.CreateGraphic(DefinitionLoader.GetItemDef(index), index);
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, index, fireCreate: false));
        item.Amount = (ushort)amount;
        Assert.True(container.TryAddItem(item));
        return item;
    }

    private static Item Box(Bench b, Item parent, bool locked)
    {
        var box = b.World.CreateItem();
        box.BaseId = 0x0E75;
        box.ItemType = locked ? ItemType.ContainerLocked : ItemType.Container;
        Assert.True(parent.TryAddItem(box));
        return box;
    }

    private static int Stock(Bench b, string defname) =>
        CraftingEngine.CountStock(b.Crafter, b.Resources.ResolveDefName(defname));

    // --- E05: a recipe input is its definition, not its graphic -------------

    [Fact]
    public void ARecipeInputKeepsTheDefinitionItNamed()
    {
        var b = Load();
        var input = Assert.Single(Recipe(b, "i_rare_widget").Resources);

        Assert.Equal(ResType.ItemDef, input.Resource.Type);
        Assert.Equal(Def(b, "i_audit_rare"), input.Resource.Index);
        Assert.NotEqual(0x1BF2, input.Resource.Index);
        Assert.Equal((ushort)0x1BF2, input.ItemId);       // the graphic stays for the UI
    }

    [Fact]
    public void ACommonIngotOnTheSameGraphicDoesNotPayForARareOne()
    {
        var b = Load();
        var rare = Recipe(b, "i_rare_widget");
        var iron = Stack(b, b.Pack, "i_ingot_iron", 5);

        Assert.Equal(0, Stock(b, "i_audit_rare"));
        Assert.False(b.Engine.CanCraft(b.Crafter, rare, checkWorkSite: false));
        Assert.Null(b.Engine.TryCraft(b.Crafter, rare));
        Assert.Equal(5, iron.Amount);
        Assert.False(iron.IsDeleted);
    }

    [Fact]
    public void EachDefinitionPaysOnlyForItsOwnRecipe()
    {
        var b = Load();
        var iron = Stack(b, b.Pack, "i_ingot_iron", 5);
        var rare = Stack(b, b.Pack, "i_audit_rare", 5);

        Assert.Equal(5, Stock(b, "i_ingot_iron"));
        Assert.Equal(5, Stock(b, "i_audit_rare"));

        Assert.True(CraftingEngine.TryConsumeResourcePart(b.Crafter, Recipe(b, "i_rare_widget"), 100, test: false));
        Assert.True(rare.IsDeleted);
        Assert.Equal(5, iron.Amount);

        Assert.False(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_rare_widget"), checkWorkSite: false));
        Assert.True(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_iron_widget"), checkWorkSite: false));
    }

    [Fact]
    public void ATypeInputStillTakesEveryDefinitionOfThatType()
    {
        var b = Load();
        var anyRecipe = Recipe(b, "i_any_widget");
        var input = Assert.Single(anyRecipe.Resources);
        Assert.Equal(ResType.TypeDef, input.Resource.Type);
        Assert.Equal(ItemType.Ingot, input.Type);

        Stack(b, b.Pack, "i_ingot_iron", 3);
        Stack(b, b.Pack, "i_audit_rare", 2);

        Assert.Equal(5, CraftingEngine.CountStock(b.Crafter, ResourceMatch.ForType(ItemType.Ingot)));
        Assert.True(b.Engine.CanCraft(b.Crafter, anyRecipe, checkWorkSite: false));
    }

    [Fact]
    public void TheScriptSurfaceMatchesByDefinitionToo()
    {
        var b = Load();
        var iron = Stack(b, b.Pack, "i_ingot_iron", 4);
        var rare = Stack(b, b.Pack, "i_audit_rare", 6);

        Assert.True(b.Pack.TryGetProperty("RESCOUNT.i_audit_rare", out string rareCount));
        Assert.True(b.Pack.TryGetProperty("RESCOUNT.i_ingot_iron", out string ironCount));
        Assert.Equal(("6", "4"), (rareCount, ironCount));

        Assert.True(b.Pack.TryExecuteCommand("CONTCONSUME", "5 i_audit_rare", new ServerConsole()));
        Assert.Equal(1, rare.Amount);
        Assert.Equal(4, iron.Amount);
    }

    // --- E04: the locked box is neither counted nor spent -------------------

    [Fact]
    public void ALockedBoxIsNotSpentForACraftItWasNotCountedFor()
    {
        var b = Load();
        var recipe = Recipe(b, "i_iron_widget");
        var open = Stack(b, b.Pack, "i_ingot_iron", 5);
        var locked = Stack(b, Box(b, b.Pack, locked: true), "i_ingot_iron", 5);

        Assert.Equal(5, Stock(b, "i_ingot_iron"));
        Assert.NotNull(b.Engine.TryCraft(b.Crafter, recipe));

        Assert.True(open.IsDeleted);
        Assert.False(locked.IsDeleted);
        Assert.Equal(5, locked.Amount);
    }

    [Fact]
    public void ATypeResourceLeavesTheLockedBoxAloneToo()
    {
        var b = Load();
        var recipe = Recipe(b, "i_any_widget");
        var open = Stack(b, b.Pack, "i_ingot_iron", 5);
        var locked = Stack(b, Box(b, b.Pack, locked: true), "i_audit_rare", 5);

        Assert.True(CraftingEngine.TryConsumeResourcePart(b.Crafter, recipe, 100, test: false));
        Assert.True(open.IsDeleted);
        Assert.Equal(5, locked.Amount);
    }

    [Fact]
    public void OnlyLockedStockIsNotEnough()
    {
        var b = Load();
        var recipe = Recipe(b, "i_iron_widget");
        var locked = Stack(b, Box(b, b.Pack, locked: true), "i_ingot_iron", 10);

        Assert.False(b.Engine.CanCraft(b.Crafter, recipe, checkWorkSite: false));
        Assert.Null(b.Engine.TryCraft(b.Crafter, recipe));
        Assert.Equal(10, locked.Amount);
    }

    [Fact]
    public void AnOpenInnerBagIsStillReached()
    {
        var b = Load();
        var nested = Stack(b, Box(b, Box(b, b.Pack, locked: false), locked: false), "i_ingot_iron", 5);

        Assert.Equal(5, Stock(b, "i_ingot_iron"));
        Assert.True(CraftingEngine.TryConsumeResourcePart(b.Crafter, Recipe(b, "i_iron_widget"), 100, test: false));
        Assert.True(nested.IsDeleted);
    }

    [Fact]
    public void ContConsumeAndItsCountsSkipALockedBox()
    {
        var b = Load();
        var locked = Stack(b, Box(b, b.Pack, locked: true), "i_ingot_iron", 5);
        var open = Stack(b, b.Pack, "i_ingot_iron", 2);

        Assert.True(b.Pack.TryGetProperty("RESCOUNT.i_ingot_iron", out string count));
        Assert.Equal("2", count);
        Assert.True(b.Pack.TryGetProperty("RESTEST 3 i_ingot_iron", out string three));
        Assert.True(b.Pack.TryGetProperty("RESTEST 2 i_ingot_iron", out string two));
        Assert.Equal(("0", "1"), (three, two));

        Assert.True(b.Pack.TryExecuteCommand("CONTCONSUME", "5 i_ingot_iron", new ServerConsole()));
        Assert.True(open.IsDeleted);
        Assert.Equal(5, locked.Amount);

        Assert.True(b.Pack.TryExecuteCommand("CONTCONSUME", "5 t_ingot", new ServerConsole()));
        Assert.Equal(5, locked.Amount);
    }

    // --- E06: boards for logs, leather for hides - one way, unless strict ----

    [Fact]
    public void BoardsStandInForLogsButNotTheOtherWay()
    {
        var b = Load();
        var boards = Stack(b, b.Pack, "i_board", 3);

        Assert.Equal(3, Stock(b, "i_log"));
        Assert.True(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_log_thing"), checkWorkSite: false));
        Assert.True(CraftingEngine.TryConsumeResourcePart(b.Crafter, Recipe(b, "i_log_thing"), 100, test: false));
        Assert.True(boards.IsDeleted);

        var logs = Stack(b, b.Pack, "i_log", 3);
        Assert.Equal(0, Stock(b, "i_board"));
        Assert.False(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_board_thing"), checkWorkSite: false));
        Assert.Equal(3, logs.Amount);
    }

    [Fact]
    public void LeatherStandsInForHidesButNotTheOtherWay()
    {
        var b = Load();
        var leather = Stack(b, b.Pack, "i_leather", 3);

        Assert.Equal(3, Stock(b, "i_hides"));
        Assert.True(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_hide_thing"), checkWorkSite: false));
        Assert.True(b.Pack.TryExecuteCommand("CONTCONSUME", "3 i_hides", new ServerConsole()));
        Assert.True(leather.IsDeleted);

        var hides = Stack(b, b.Pack, "i_hides", 3);
        Assert.Equal(0, Stock(b, "i_leather"));
        Assert.False(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_leather_thing"), checkWorkSite: false));
        Assert.True(b.Pack.TryExecuteCommand("CONTCONSUME", "3 i_leather", new ServerConsole()));
        Assert.Equal(3, hides.Amount);
    }

    [Fact]
    public void StrictComparisonTurnsTheAlternativesOff()
    {
        var b = Load();
        var boards = Stack(b, b.Pack, "i_board", 3);
        var leather = Stack(b, b.Pack, "i_leather", 3);
        try
        {
            ResourceMatch.StrictComparison = true;

            Assert.Equal(0, Stock(b, "i_log"));
            Assert.Equal(0, Stock(b, "i_hides"));
            Assert.False(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_log_thing"), checkWorkSite: false));
            Assert.False(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_hide_thing"), checkWorkSite: false));
            Assert.True(b.Pack.TryExecuteCommand("CONTCONSUME", "3 i_log, 3 i_hides", new ServerConsole()));
            Assert.Equal(3, boards.Amount);
            Assert.Equal(3, leather.Amount);

            // Each still is itself.
            Assert.Equal(3, Stock(b, "i_board"));
            Assert.Equal(3, Stock(b, "i_leather"));
        }
        finally
        {
            ResourceMatch.StrictComparison = false;
        }
    }

    // --- E11: gold in a locked box is not money -----------------------------

    private static Item Gold(Bench b, Item container, int amount)
    {
        var gold = b.World.CreateItem();
        gold.BaseId = 0x0EED;
        gold.ItemType = ItemType.Gold;
        gold.Amount = (ushort)amount;
        Assert.True(container.TryAddItem(gold));
        return gold;
    }

    private static Item Bank(Bench b)
    {
        var bank = b.World.CreateItem();
        bank.BaseId = 0x09AB;
        bank.ItemType = ItemType.EqBankBox;
        b.Crafter.Equip(bank, Layer.BankBox);
        return bank;
    }

    private static void WithVendorWorld(Bench b, bool packOnly, Action body)
    {
        var savedWorld = VendorEngine.World;
        bool savedPackOnly = VendorEngine.PayFromPackOnly;
        try
        {
            VendorEngine.World = b.World;
            VendorEngine.PayFromPackOnly = packOnly;
            body();
        }
        finally
        {
            VendorEngine.World = savedWorld;
            VendorEngine.PayFromPackOnly = savedPackOnly;
        }
    }

    [Fact]
    public void GoldInALockedBoxIsNotSpendable()
    {
        var b = Load();
        WithVendorWorld(b, packOnly: false, () =>
        {
            var box = Box(b, b.Pack, locked: true);
            var gold = Gold(b, box, 100);

            Assert.Equal(0, VendorEngine.CountGold(b.Crafter));
            VendorEngine.RemoveGold(b.Crafter, 40);
            Assert.Equal(100, gold.Amount);

            // Unlocked, the same coins are money again.
            box.ItemType = ItemType.Container;
            Assert.Equal(100, VendorEngine.CountGold(b.Crafter));
            VendorEngine.RemoveGold(b.Crafter, 40);
            Assert.Equal(60, gold.Amount);
        });
    }

    [Fact]
    public void BankedGoldStillPaysButNotFromALockedBoxInTheBank()
    {
        var b = Load();
        WithVendorWorld(b, packOnly: false, () =>
        {
            var bank = Bank(b);
            var banked = Gold(b, bank, 50);
            var lockedInBank = Gold(b, Box(b, bank, locked: true), 70);
            var loose = Gold(b, b.Pack, 10);

            Assert.Equal(60, VendorEngine.CountGold(b.Crafter));
            Assert.Equal(60, b.Crafter.CountCarriedGold());

            VendorEngine.RemoveGold(b.Crafter, 30);   // the pack first, then the bank
            Assert.True(loose.IsDeleted);
            Assert.Equal(30, banked.Amount);
            Assert.Equal(70, lockedInBank.Amount);
        });
    }

    [Fact]
    public void PayFromPackOnlyKeepsToThePack()
    {
        var b = Load();
        WithVendorWorld(b, packOnly: true, () =>
        {
            var banked = Gold(b, Bank(b), 50);
            var loose = Gold(b, b.Pack, 10);
            var locked = Gold(b, Box(b, b.Pack, locked: true), 70);

            Assert.Equal(10, VendorEngine.CountGold(b.Crafter));
            VendorEngine.RemoveGold(b.Crafter, 30);
            Assert.True(loose.IsDeleted);
            Assert.Equal(50, banked.Amount);
            Assert.Equal(70, locked.Amount);
        });
    }

    [Fact]
    public void GoldIsReachedInsideOrdinaryNestedPouches()
    {
        var b = Load();
        WithVendorWorld(b, packOnly: false, () =>
        {
            var deep = Gold(b, Box(b, Box(b, b.Pack, locked: false), locked: false), 25);
            Assert.Equal(25, VendorEngine.CountGold(b.Crafter));
            VendorEngine.RemoveGold(b.Crafter, 25);
            Assert.True(deep.IsDeleted);
        });
    }

    // --- gold is a TYPE, not a graphic ----------------------------------------

    [Fact]
    public void ACoinGraphicWithoutTheGoldTypeIsNotMoney()
    {
        var b = Load();
        WithVendorWorld(b, packOnly: false, () =>
        {
            var fake = b.World.CreateItem();
            fake.BaseId = 0x0EED;                 // no definition here gives it t_gold
            fake.Amount = 100;
            Assert.True(b.Pack.TryAddItem(fake));
            Assert.NotEqual(ItemType.Gold, fake.ItemType);

            Assert.Equal(0, VendorEngine.CountGold(b.Crafter));
            Assert.Equal(0, b.Crafter.CountCarriedGold());
            VendorEngine.RemoveGold(b.Crafter, 10);
            Assert.Equal(100, fake.Amount);

            var real = Gold(b, b.Pack, 5);
            Assert.Equal(5, VendorEngine.CountGold(b.Crafter));
        });
    }

    // --- MATOVERRIDE (ResourceConsume only) -------------------------------------

    [Fact]
    public void MatOverrideRedirectsAnItemResourceForResourceConsume()
    {
        var b = Load();
        var rare = Stack(b, b.Pack, "i_audit_rare", 8);
        b.Crafter.SetTag("MATOVERRIDE_i_ingot_iron", "i_audit_rare");

        // Skill_MakeItem's ResourceConsume test and spend honour it ...
        Assert.True(b.Engine.CanCraft(b.Crafter, Recipe(b, "i_iron_widget"), checkWorkSite: false));
        Assert.NotNull(b.Engine.TryCraft(b.Crafter, Recipe(b, "i_iron_widget")));
        Assert.Equal(3, rare.Amount);

        // ... ResourceConsumePart (repair, failed-craft loss) does not.
        Assert.False(CraftingEngine.TryConsumeResourcePart(b.Crafter, Recipe(b, "i_iron_widget"), 100, test: true));

        // CONSUME on the character is ResourceConsume too.
        Assert.True(b.Crafter.TryExecuteCommand("CONSUME", "2 i_ingot_iron", new ServerConsole()));
        Assert.Equal(1, rare.Amount);
    }

    // --- skill entries in a resource list ---------------------------------------

    [Fact]
    public void ConsumeStopsAtASkillTheCharacterLacks()
    {
        var b = Load();
        var iron = Stack(b, b.Pack, "i_ingot_iron", 10);
        var rare = Stack(b, b.Pack, "i_audit_rare", 10);

        Assert.True(b.Crafter.TryExecuteCommand("CONSUME", "2 i_ingot_iron, Tinkering 50.0, 3 i_audit_rare", new ServerConsole()));
        Assert.Equal((8, 7), ((int)iron.Amount, (int)rare.Amount));

        // Short of the skill: entries before it were spent, entries after it are not.
        b.Crafter.SetSkill(SkillType.Tinkering, 400);
        Assert.True(b.Crafter.TryExecuteCommand("CONSUME", "1 i_ingot_iron, Tinkering 50.0, 3 i_audit_rare", new ServerConsole()));
        Assert.Equal((7, 7), ((int)iron.Amount, (int)rare.Amount));

        // A repeated resource replaces its earlier entry (CResourceQtyArray::Load).
        b.Crafter.SetSkill(SkillType.Tinkering, 1000);
        Assert.True(b.Crafter.TryExecuteCommand("CONSUME", "5 i_ingot_iron, 2 i_ingot_iron", new ServerConsole()));
        Assert.Equal(5, iron.Amount);
    }

    [Fact]
    public void RestestOnAContainerSkipsSkillEntries()
    {
        var b = Load();
        Stack(b, b.Pack, "i_ingot_iron", 2);

        Assert.True(b.Pack.TryGetProperty("RESTEST Tinkering 100.0, 2 i_ingot_iron", out string withItems));
        Assert.True(b.Pack.TryGetProperty("RESTEST Tinkering 100.0", out string skillOnly));
        Assert.True(b.Pack.TryGetProperty("RESTEST 3 i_ingot_iron", out string shortOne));
        Assert.Equal(("1", "1", "0"), (withItems, skillOnly, shortOne));
    }

    [Fact]
    public void ASkillInRecipeResourcesIsALevelNotAStock()
    {
        var b = Load();
        var recipe = Recipe(b, "i_skilled_widget");
        Assert.Equal(2, recipe.Resources.Count);
        var iron = Stack(b, b.Pack, "i_ingot_iron", 1);

        b.Crafter.SetSkill(SkillType.Carpentry, 400);
        Assert.False(b.Engine.CanCraft(b.Crafter, recipe, checkWorkSite: false));

        b.Crafter.SetSkill(SkillType.Carpentry, 500);
        Assert.True(b.Engine.CanCraft(b.Crafter, recipe, checkWorkSite: false));
        Assert.NotNull(b.Engine.TryCraft(b.Crafter, recipe));
        Assert.True(iron.IsDeleted);
        Assert.Equal(500, b.Crafter.GetSkill(SkillType.Carpentry));
    }

    // --- a bare number ends a RESOURCES list -------------------------------------

    [Fact]
    public void ANumericResourcesEntryEndsTheList()
    {
        var b = Load();
        var recipe = Recipe(b, "i_numeric_widget");

        var only = Assert.Single(recipe.Resources);
        Assert.Equal(Def(b, "i_ingot_iron"), only.Resource.Index);
    }

    // --- spell reagents on the same walk ---------------------------------------

    private static (SpellEngine Engine, SpellDef Def, List<string> Messages) Spell(Bench b,
        params (ResourceId Rid, int Qty)[] reagents)
    {
        var def = new SpellDef
        {
            Id = SpellType.Strength,
            Name = "Strength",
            Flags = SpellFlag.TargChar | SpellFlag.Good,
            ManaCost = 0,
        };
        foreach (var (rid, qty) in reagents)
            def.Reagents[rid] = qty;
        var registry = new SpellRegistry();
        registry.Register(def);
        var engine = new SpellEngine(b.World, registry);
        var messages = new List<string>();
        engine.OnSysMessage = (_, text) => messages.Add(text);

        b.Crafter.PrivLevel = PrivLevel.Player;
        b.Crafter.Int = 100;
        b.Crafter.MaxMana = 100;
        b.Crafter.Mana = 100;
        b.Crafter.SetSkill(SkillType.Magery, 1200);
        var book = b.World.CreateItem();
        book.ItemType = ItemType.Spellbook;
        book.More1 = 1u << ((int)SpellType.Strength - 1);
        Assert.True(b.Pack.TryAddItem(book));
        return (engine, def, messages);
    }

    private static int Cast(Bench b, SpellEngine engine) =>
        engine.CastStart(b.Crafter, SpellType.Strength, b.Crafter.Uid, b.Crafter.Position);

    [Fact]
    public void ReagentsInALockedBoxDoNotPayForASpell()
    {
        var b = Load();
        var (engine, _, _) = Spell(b, (b.Resources.ResolveDefName("i_ingot_iron"), 1));
        Stack(b, Box(b, b.Pack, locked: true), "i_ingot_iron", 5);

        Assert.Equal(-1, Cast(b, engine));

        Stack(b, b.Pack, "i_ingot_iron", 1);
        Assert.True(Cast(b, engine) >= 0);
    }

    [Fact]
    public void AReagentIsItsDefinitionNotItsGraphic()
    {
        var b = Load();
        var (engine, _, _) = Spell(b, (b.Resources.ResolveDefName("i_audit_rare"), 1));
        Stack(b, b.Pack, "i_ingot_iron", 5);         // same graphic, other definition

        Assert.Equal(-1, Cast(b, engine));
        Stack(b, b.Pack, "i_audit_rare", 1);
        Assert.True(Cast(b, engine) >= 0);
    }

    [Fact]
    public void TheMissingReagentNamedIsTheLastOneShort()
    {
        var b = Load();
        var (engine, _, messages) = Spell(b,
            (b.Resources.ResolveDefName("i_log"), 1),
            (b.Resources.ResolveDefName("i_ingot_iron"), 1),
            (b.Resources.ResolveDefName("i_hides"), 1));
        Stack(b, b.Pack, "i_ingot_iron", 1);

        Assert.Equal(-1, Cast(b, engine));
        Assert.Contains("hides", messages.Last());
    }

    [Fact]
    public void ARealReagentSpendTakesWhatIsThereOfEachEntry()
    {
        // ResourceConsumePart with fTest false spends every entry on its own - the
        // reference's cast-completion pass therefore takes the reagents that are
        // present even when another one is short.
        var b = Load();
        var iron = Stack(b, b.Pack, "i_ingot_iron", 3);
        var list = new List<ResourceMatch.ResourceQty>
        {
            new(b.Resources.ResolveDefName("i_log"), 1),
            new(b.Resources.ResolveDefName("i_ingot_iron"), 2),
        };

        Assert.Equal(0, ResourceMatch.ResourceConsumePart(b.Crafter, list, 1, 100, test: false));
        Assert.Equal(1, iron.Amount);
    }
}
