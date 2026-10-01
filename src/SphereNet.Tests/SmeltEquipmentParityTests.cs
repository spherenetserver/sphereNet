using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Smelting anything that is not ore, and what the fire refuses.
///
/// Source-X Skill_Mining_Smelt (CCharSkill.cpp:1057-1287) takes any item: an item
/// whose definition is not t_ore gives back the ITEMDEF entries of its RESOURCES
/// list - gems bounced out at once, ingots behind the ingot's minimum Mining and a
/// roll - and is consumed whole before the ingots are handed over. Magic, blessed
/// and blessed2 inputs are refused before anything is spent (:1126). SphereNet
/// smelted ore only and burnt a blessed pile like any other.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SmeltEquipmentParityTests : IDisposable
{
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_smelt_{Guid.NewGuid():N}.scp");

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private const ushort Ingot = 0x1BEF;
    private const ushort HardIngot = 0x1BE3;
    private const ushort Gem = 0x0F0F;
    private const ushort Cloth = 0x175D;
    private const ushort OreTile = 0x19B9;
    private const ushort Sword = 0x13B9;          // 5 ingots, 2 gems
    private const ushort GemFirstSword = 0x13B7;  // 2 gems, 5 ingots
    private const ushort HardSword = 0x1441;      // 3 hard ingots, 1 gem
    private const ushort Robe = 0x1F03;           // cloth only
    private const ushort Mace = 0x0F5C;           // 4 ingots

    private const string Defs = """
        [ITEMDEF 01bef]
        DEFNAME=i_smt_ingot
        NAME=test ingot
        TYPE=t_ingot

        [ITEMDEF 01be3]
        DEFNAME=i_smt_ingot_hard
        NAME=hard ingot
        TYPE=t_ingot
        TDATA1=90.0
        TDATA2=100.0

        [ITEMDEF 0f0f]
        DEFNAME=i_smt_gem
        NAME=test gem
        TYPE=t_gem

        [ITEMDEF 0175d]
        DEFNAME=i_smt_cloth
        NAME=test cloth
        TYPE=t_cloth

        [ITEMDEF 019b9]
        DEFNAME=i_smt_ore
        NAME=test ore
        TYPE=t_ore
        TDATA1=i_smt_ingot

        [ITEMDEF 013b9]
        DEFNAME=i_smt_sword
        NAME=test sword
        TYPE=t_weapon_sword
        RESOURCES=5 i_smt_ingot, 2 i_smt_gem

        [ITEMDEF 013b7]
        DEFNAME=i_smt_sword_gemfirst
        NAME=gem sword
        TYPE=t_weapon_sword
        RESOURCES=2 i_smt_gem, 5 i_smt_ingot

        [ITEMDEF 01441]
        DEFNAME=i_smt_sword_hard
        NAME=hard sword
        TYPE=t_weapon_sword
        RESOURCES=3 i_smt_ingot_hard, 1 i_smt_gem

        [ITEMDEF 01f03]
        DEFNAME=i_smt_robe
        NAME=test robe
        TYPE=t_clothing
        RESOURCES=12 i_smt_cloth

        [ITEMDEF 0f5c]
        DEFNAME=i_smt_mace
        NAME=test mace
        TYPE=t_weapon_mace_smith
        RESOURCES=4 i_smt_ingot
        """;

    private sealed record Bench(GameWorld World, GameClient Client, Character Me, Item Pack, Item Forge);

    private Bench Setup(TriggerDispatcher? triggers = null)
    {
        var lf = LoggerFactory.Create(_ => { });
        File.WriteAllText(_defFile, Defs);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 8131);
        if (triggers != null)
            client.SetEngines(triggerDispatcher: triggers);

        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.Str = 100; me.MaxHits = 100; me.Hits = 100;
        me.Dex = 100; me.Stam = 100; me.Int = 100;
        me.SetSkill(SkillType.Mining, 1000);
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        me.Backpack = pack;
        me.Equip(pack, Layer.Pack);

        var forge = world.CreateItem();
        forge.ItemType = ItemType.Forge;
        world.PlaceItem(forge, new Point3D(101, 100, 0, 0));

        return new Bench(world, client, me, pack, forge);
    }

    private static Item Make(Bench bench, ushort defId, ushort amount = 1)
    {
        var item = bench.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, defId));
        item.Amount = amount;
        Assert.True(bench.Pack.TryAddItem(item));
        return item;
    }

    private static void MiningRolls(bool succeed) =>
        Character.OnSkillUseQuick = (_, skill, _, result) =>
            skill == (int)SkillType.Mining ? (succeed ? 1 : 0) : result;

    /// <summary>Double-click the forge, target the item (CClientTarg.cpp:1763).</summary>
    private static void SmeltAtForge(Bench bench, Item item)
    {
        bench.Client.HandleDoubleClick(bench.Forge.Uid.Value);
        Assert.True(bench.Client.HasPendingTarget);
        bench.Client.HandleTargetResponse(0, bench.Client.ActiveTargetCursorId,
            item.Uid.Value, item.X, item.Y, item.Z, 0);
    }

    private static int CountOf(Bench bench, ushort id) =>
        bench.Pack.Contents.Where(i => !i.IsDeleted && i.BaseId == id).Sum(i => (int)i.Amount);

    // --- E21: equipment gives back its RESOURCES -------------------------

    [Fact]
    public void AWeaponSmeltsBackIntoItsIngotsAndGems()
    {
        MiningRolls(true);
        var bench = Setup();
        var sword = Make(bench, Sword);

        SmeltAtForge(bench, sword);

        Assert.True(sword.IsDeleted);
        Assert.Equal(5, CountOf(bench, Ingot));
        Assert.Equal(2, CountOf(bench, Gem));
    }

    [Fact]
    public void TheResourcesAreMultipliedByTheStackAmount()
    {
        // iResourceQty *= iOreQty (:1209): the listed amount per piece times the pile.
        MiningRolls(true);
        var bench = Setup();
        var maces = Make(bench, Mace, amount: 3);

        SmeltAtForge(bench, maces);

        Assert.True(maces.IsDeleted);
        Assert.Equal(12, CountOf(bench, Ingot));
    }

    [Fact]
    public void GemsComeOutWithoutARollAndAnIngotBelowTheMinimumIsSkipped()
    {
        // A multi-resource item goes on past an ingot the smith may not smelt
        // (:1233-1236); the gem still bounces and the item is still consumed.
        int rolls = 0;
        Character.OnSkillUseQuick = (_, _, _, _) => { rolls++; return 1; };
        var bench = Setup();
        // TDATA1=90.0 is 900 - Exp_GetVal skips the dot, the way the pack writes
        // every ingot's bar. It used to read as 0, so any smith smelted any ingot.
        Assert.Equal(900u, DefinitionLoader.GetItemDef(HardIngot)!.TData1);
        Assert.Equal(1000u, DefinitionLoader.GetItemDef(HardIngot)!.TData2);
        bench.Me.SetSkill(SkillType.Mining, 100);
        var sword = Make(bench, HardSword);

        SmeltAtForge(bench, sword);

        Assert.Equal(0, rolls);
        Assert.True(sword.IsDeleted);
        Assert.Equal(0, CountOf(bench, HardIngot));
        Assert.Equal(1, CountOf(bench, Gem));
    }

    [Fact]
    public void AnItemWithNoIngotOrGemResourceIsBurntForNothing()
    {
        // Every resource that is neither ingot nor gem says DEFMSG_MINING_CONSUMED
        // and the loop goes on; the item is consumed at the end (:1203/:1279).
        MiningRolls(true);
        var bench = Setup();
        var robe = Make(bench, Robe);

        SmeltAtForge(bench, robe);

        Assert.True(robe.IsDeleted);
        Assert.Equal(0, CountOf(bench, Cloth));
    }

    [Fact]
    public void AFailedRollOnASinglePieceLosesThePiece()
    {
        // rand(amount/2)+1 of one piece is the piece (:1247); one kind of resource
        // ends the smelt there.
        MiningRolls(false);
        var bench = Setup();
        var mace = Make(bench, Mace);

        SmeltAtForge(bench, mace);

        Assert.True(mace.IsDeleted);
        Assert.Equal(0, CountOf(bench, Ingot));
    }

    [Fact]
    public void TheSmeltTriggerSeesEveryResourceAndMayChangeThem()
    {
        var triggers = new TriggerDispatcher();
        int fired = 0;
        triggers.RegisterItemEvent("EVENTSITEM", "Smelt", (_, args) =>
        {
            fired++;
            Assert.Equal(1000, args.N1);                 // the smith's Mining
            Assert.Equal(2, args.N2);                    // two resources listed
            Assert.Null(args.O1);
            Assert.Equal(Ingot, args.Locals!.GetInt("resource.0.ID"));
            Assert.Equal(5, args.Locals.GetInt("resource.0.amount"));
            Assert.Equal(Gem, args.Locals.GetInt("resource.1.ID"));
            Assert.Equal(2, args.Locals.GetInt("resource.1.amount"));
            args.Locals.SetInt("resource.0.amount", 1);
            return TriggerResult.Default;
        });
        MiningRolls(true);
        var bench = Setup(triggers);
        var sword = Make(bench, Sword);

        SmeltAtForge(bench, sword);

        Assert.Equal(1, fired);
        Assert.True(sword.IsDeleted);
        Assert.Equal(1, CountOf(bench, Ingot));
        Assert.Equal(2, CountOf(bench, Gem));
    }

    [Fact]
    public void AScriptRefusalLeavesTheItemAlone()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterItemEvent("EVENTSITEM", "Smelt", (_, _) => TriggerResult.True);
        MiningRolls(true);
        var bench = Setup(triggers);
        var sword = Make(bench, Sword);

        SmeltAtForge(bench, sword);

        Assert.False(sword.IsDeleted);
        Assert.Equal(0, CountOf(bench, Ingot));
        Assert.Equal(0, CountOf(bench, Gem));
    }

    [Fact]
    public void AResourceBeforeTheIngotAbandonsTheSmeltAsTheReferenceDoes()
    {
        // Reproduced as-is: Source-X keeps the made ingots in a list it indexes by
        // the RESOURCE number (ingots.at(i), :1258). With the gem listed first the
        // ingot is resource 1 but item 0 of the list, at(1) throws, and the smelt
        // stops there: the gems are already out, the sword stays, no ingot is made.
        MiningRolls(true);
        var bench = Setup();
        var sword = Make(bench, GemFirstSword);

        SmeltAtForge(bench, sword);

        Assert.False(sword.IsDeleted);
        Assert.Equal(2, CountOf(bench, Gem));
        Assert.Equal(0, CountOf(bench, Ingot));
        Assert.DoesNotContain(bench.World.GetAllObjects().OfType<Item>(),
            i => !i.IsDeleted && i.BaseId == Ingot);
    }

    [Fact]
    public void ScriptVerbSmeltsEquipmentToo()
    {
        MiningRolls(true);
        var bench = Setup();
        var sword = Make(bench, Sword);

        Assert.True(bench.Client.TryExecuteScriptCommand(sword, "SMELT", "", null));

        Assert.True(sword.IsDeleted);
        Assert.Equal(5, CountOf(bench, Ingot));
    }

    // --- E22: magic / blessed / blessed2 are refused ---------------------

    [Theory]
    [InlineData(ObjAttributes.Magic)]
    [InlineData(ObjAttributes.Blessed)]
    [InlineData(ObjAttributes.Blessed2)]
    public void AnAttributedOreIsRefusedAndNothingChanges(ObjAttributes attr)
    {
        var triggers = new TriggerDispatcher();
        int fired = 0;
        triggers.RegisterItemEvent("EVENTSITEM", "Smelt", (_, _) => { fired++; return TriggerResult.Default; });
        int rolls = 0;
        Character.OnSkillUseQuick = (_, _, _, _) => { rolls++; return 1; };
        var bench = Setup(triggers);
        var ore = Make(bench, OreTile, amount: 6);
        ore.SetAttr(attr);
        var before = bench.Pack.Contents.Select(i => (i.Uid, i.BaseId, i.Amount)).ToList();

        bench.Client.HandleDoubleClick(ore.Uid.Value);

        Assert.False(ore.IsDeleted);
        Assert.Equal(6, ore.Amount);
        Assert.Equal(0, fired);       // the resource list never reaches @Smelt
        Assert.Equal(0, rolls);
        Assert.Equal(before, bench.Pack.Contents.Select(i => (i.Uid, i.BaseId, i.Amount)).ToList());
    }

    [Theory]
    [InlineData(ObjAttributes.Magic)]
    [InlineData(ObjAttributes.Blessed)]
    [InlineData(ObjAttributes.Blessed2)]
    public void AnAttributedWeaponIsRefusedAndNothingChanges(ObjAttributes attr)
    {
        MiningRolls(true);
        var bench = Setup();
        var sword = Make(bench, Sword);
        sword.SetAttr(attr);
        var before = bench.Pack.Contents.Select(i => (i.Uid, i.BaseId, i.Amount)).ToList();

        SmeltAtForge(bench, sword);

        Assert.False(sword.IsDeleted);
        Assert.Equal(before, bench.Pack.Contents.Select(i => (i.Uid, i.BaseId, i.Amount)).ToList());
    }

    [Fact]
    public void AnUnattributedOreStillSmelts()
    {
        MiningRolls(true);
        var bench = Setup();
        var ore = Make(bench, OreTile, amount: 6);

        bench.Client.HandleDoubleClick(ore.Uid.Value);

        Assert.True(ore.IsDeleted);
        Assert.Equal(6, CountOf(bench, Ingot));
    }

    // --- the rest of the path --------------------------------------------

    [Fact]
    public void IngotsAreNotSmelted()
    {
        // DEFMSG_MINING_INGOTS (:1109-1113).
        MiningRolls(true);
        var bench = Setup();
        var ingots = Make(bench, Ingot, amount: 5);

        SmeltAtForge(bench, ingots);

        Assert.False(ingots.IsDeleted);
        Assert.Equal(5, ingots.Amount);
    }

    [Fact]
    public void WithoutAForgeInRangeNothingIsSpent()
    {
        MiningRolls(true);
        var bench = Setup();
        bench.World.RemoveItem(bench.Forge);
        var ore = Make(bench, OreTile, amount: 3);

        bench.Client.HandleDoubleClick(ore.Uid.Value);

        Assert.False(ore.IsDeleted);
        Assert.Equal(3, ore.Amount);
        Assert.Equal(0, CountOf(bench, Ingot));
    }

    [Fact]
    public void OreSmeltedOnAPileOfTheSameOreJoinsIt()
    {
        // pItemTarg of IT_ORE with the same id: the piles combine (:1076-1086).
        MiningRolls(true);
        var bench = Setup();
        var a = Make(bench, OreTile, amount: 3);
        var b = Make(bench, OreTile, amount: 4);

        Assert.True(bench.Client.TryExecuteScriptCommand(a, "SMELT", $"0{b.Uid.Value:X}", null));

        Assert.True(a.IsDeleted);
        Assert.False(b.IsDeleted);
        Assert.Equal(7, b.Amount);
        Assert.Equal(0, CountOf(bench, Ingot));
    }
}
