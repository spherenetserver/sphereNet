using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The natural-resource bit and the gathering stages, end to end against Source-X:
/// CWorldMap::CheckNaturalResource (CWorldMap.cpp:26-172), CRandGroupDef::
/// GetRandMemberIndex (CRandGroupDef.cpp:218-289), Skill_NaturalResource_Create
/// (CCharSkill.cpp:992-1054) and the stage checks of Skill_Mining / Skill_Fishing /
/// Skill_Lumberjack (CCharSkill.cpp:1383-1692).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NaturalResourceSourceXFlowTests : IDisposable
{
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_nres_{Guid.NewGuid():N}.scp");

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private static readonly Point3D Here = new(100, 100, 0, 0);
    private static readonly Point3D Rock = new(101, 100, 0, 0);

    private sealed class Sink : IActiveSkillSink
    {
        public Character Self { get; }
        public Random Random { get; } = new(1);
        public GameWorld World { get; }
        public List<string> Log { get; } = [];
        public List<Item> Delivered { get; } = [];
        public Dictionary<ItemType, Item> Pack { get; } = [];

        public Sink(Character self, GameWorld world) { Self = self; World = world; }

        public void SysMessage(string text) => Log.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Log.Add(text);
        public void Emote(string text) => Log.Add(text);
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => Pack.TryGetValue(type, out var i) ? i : null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) => Delivered.Add(item);
    }

    private (GameWorld World, GatheringEngine Engine, Character Ch) Setup(string extra = "",
        int amount = 10, string regen = "60*60*10", bool alwaysSucceed = true)
    {
        var lf = LoggerFactory.Create(_ => { });
        File.WriteAllText(_defFile, $$"""
            [ITEMDEF 019b7]
            DEFNAME=i_ore_iron
            NAME=Iron Ore

            [ITEMDEF 09cc]
            DEFNAME=i_test_fish
            NAME=test fish

            [ITEMDEF 01bdd]
            DEFNAME=i_test_log
            NAME=test log

            [REGIONRESOURCE mr_iron]
            DEFNAME=mr_iron
            AMOUNT={{amount}}
            REAP=i_ore_iron
            REAPAMOUNT=1
            SKILL=0.0
            REGEN={{regen}}

            [REGIONRESOURCE mr_fish]
            DEFNAME=mr_fish
            AMOUNT={{amount}}
            REAP=i_test_fish
            REAPAMOUNT=1
            SKILL=0.0
            REGEN={{regen}}

            [REGIONRESOURCE mr_logs]
            DEFNAME=mr_logs
            AMOUNT={{amount}}
            REAP=i_test_log
            REAPAMOUNT=1
            SKILL=0.0
            REGEN={{regen}}

            [REGIONTYPE r_rock t_rock]
            DEFNAME=r_rock
            RESOURCES=100.0 mr_iron

            [REGIONTYPE r_water t_water]
            DEFNAME=r_water
            RESOURCES=100.0 mr_fish

            [REGIONTYPE r_tree t_tree]
            DEFNAME=r_tree
            RESOURCES=100.0 mr_logs
            {{extra}}
            """);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        TestHarness.AttachLoadedRegionTypes(world);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, Here);
        if (alwaysSucceed)
            Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        return (world, new GatheringEngine(world), ch);
    }

    private static Item Bit(GameWorld world, Point3D at) =>
        world.GetItemsInRange(at, 0).Single(i => i.BaseId == GatheringEngine.MarkerGraphic && !i.IsDeleted);

    private static Item Tool(GameWorld world, Character ch, ItemType type, Layer layer = Layer.OneHanded)
    {
        var tool = world.CreateItem();
        tool.BaseId = 0x0E86;
        tool.ItemType = type;
        ch.Equip(tool, layer);
        return tool;
    }

    // --- the bit ------------------------------------------------------------

    [Fact]
    public void TheBitIsAnInvisibleImmovableWorldGemOfTheResourceType()
    {
        var (world, engine, ch) = Setup(amount: 7);

        Assert.True(engine.ProbeResource(ch, SkillType.Mining, Rock).Success);
        var bit = Bit(world, Rock);

        // CItem::CreateScript(ITEMID_WorldGem, pCharSrc, iType), ATTR_INVIS|ATTR_MOVE_NEVER,
        // m_ridRes in MORE1 (CWorldMap.cpp:124-130).
        Assert.Equal(ItemType.Rock, bit.ItemType);
        Assert.True(bit.IsAttr(ObjAttributes.Invis));
        Assert.True(bit.IsAttr(ObjAttributes.Move_Never));
        Assert.Equal((uint)ResType.RegionResource, bit.More1 >> 24);
        var mrIron = DefinitionLoader.StaticResources!.ResolveDefName("mr_iron");
        Assert.Equal((uint)mrIron.Index, bit.More1 & 0x00FFFFFF);
    }

    [Fact]
    public void TheBitsAmountIsItsPoolForScripts()
    {
        var (world, engine, ch) = Setup(amount: 7);
        engine.ProbeResource(ch, SkillType.Mining, Rock);
        var bit = Bit(world, Rock);

        Assert.True(bit.TryGetProperty("AMOUNT", out string amount));
        Assert.Equal("7", amount);

        // ARGO.AMOUNT=0 empties the vein, as a RETURN 1 from @ResourceFound does.
        Assert.True(bit.TrySetProperty("AMOUNT", "0"));
        Assert.Equal(0, GatheringEngine.NaturalResourceAmount(bit));
        Assert.True(engine.ProbeResource(ch, SkillType.Mining, Rock).Depleted);

        Assert.True(bit.TrySetProperty("AMOUNT", "3"));
        Assert.True(bit.TryGetProperty("AMOUNT", out amount));
        Assert.Equal("3", amount);
    }

    [Fact]
    public void TheBitDecaysAfterRegenTenthsWithTheDecayAttribute()
    {
        var (world, engine, ch) = Setup(regen: "600");   // 60 s
        long before = Environment.TickCount64;
        engine.ProbeResource(ch, SkillType.Mining, Rock);
        var bit = Bit(world, Rock);

        // MoveToDecay(pt, REGEN * MSECS_PER_TENTH) -> SetDecayTime arms ATTR_DECAY
        // (CWorldMap.cpp:148, CItem.cpp:1499-1500).
        Assert.InRange(bit.DecayTime, before + 60_000 - 50, Environment.TickCount64 + 60_000 + 50);
        Assert.True(bit.IsAttr(ObjAttributes.Decay));
    }

    [Fact]
    public void AnExpiredBitIsDeletedByTheDecayQueueAndTheNextSearchMakesAFreshOne()
    {
        var (world, engine, ch) = Setup(amount: 4);
        engine.ProbeResource(ch, SkillType.Mining, Rock);
        var bit = Bit(world, Rock);
        GatheringEngine.ConsumeNaturalResource(bit, 4);
        Assert.True(engine.ProbeResource(ch, SkillType.Mining, Rock).Depleted);

        // Due now: the global decay queue hands it over whatever its sector is doing,
        // and the ordinary decay tick deletes it (invisible and MOVE_NEVER or not).
        bit.SetDecayAt(Environment.TickCount64 - 1);
        var due = new List<Item>();
        world.CollectDueDecay(Environment.TickCount64, 256, due);
        Assert.Contains(bit, due);
        Assert.False(bit.OnTick());
        Assert.True(bit.IsDeleted);
        world.DeleteObject(bit);

        var fresh = engine.ProbeResource(ch, SkillType.Mining, Rock);
        Assert.True(fresh.Success);
        var next = Bit(world, Rock);
        Assert.NotSame(bit, next);
        Assert.Equal(4, GatheringEngine.NaturalResourceAmount(next));
    }

    [Fact]
    public void AnotherKindOfItemOnTheTileRefusesANewBit()
    {
        // CWorldMap.cpp:76-85: any item there whose type is not the resource type.
        var (world, engine, ch) = Setup();
        var junk = world.CreateItem();
        junk.BaseId = 0x0EED;
        junk.ItemType = ItemType.Gold;
        world.PlaceItem(junk, Rock);

        var probe = engine.ProbeResource(ch, SkillType.Mining, Rock);
        Assert.False(probe.Handled);
        Assert.DoesNotContain(world.GetItemsInRange(Rock, 0), i => i.BaseId == GatheringEngine.MarkerGraphic);
    }

    [Fact]
    public void ABitSavedByAnOlderBuildIsFoundByItsTagAndGivenItsType()
    {
        var (world, engine, ch) = Setup();
        var old = world.CreateItem();
        old.BaseId = GatheringEngine.MarkerGraphic;
        old.SetAttr(ObjAttributes.Invis | ObjAttributes.Move_Never);
        old.SetTag("RESOURCE_MARKER", "1");
        old.SetTag("RES_SKILL", "Mining");
        old.SetTag("RES_POOL", "2");
        old.SetTag("RES_ID", DefinitionLoader.StaticResources!.ResolveDefName("mr_iron").Index.ToString());
        world.PlaceItem(old, Rock);

        var swing = engine.TryGatherForSink(ch, SkillType.Mining, Rock);
        Assert.True(swing.Success);
        Assert.Equal(ItemType.Rock, old.ItemType);
        Assert.Equal(1, GatheringEngine.NaturalResourceAmount(old));
    }

    // --- the draw -------------------------------------------------------------

    [Fact]
    public void ALowSkillCharacterIsNotDealtAVeinItCannotMake()
    {
        // GetRandMemberIndex(pCharSrc) keeps only members whose reap the character
        // could make (Skill_MakeItem SKTRIG_SELECT -> the reap ITEMDEF's SKILLMAKE,
        // CRandGroupDef.cpp:255): a gold vein never falls to a novice.
        var (world, engine, ch) = Setup("""

            [ITEMDEF i_ore_gold]
            ID=019b7
            NAME=Gold Ore
            SKILLMAKE=mining 85.0

            [REGIONRESOURCE mr_gold]
            DEFNAME=mr_gold
            AMOUNT=5
            REAP=i_ore_gold
            REAPAMOUNT=1
            SKILL=0.0
            REGEN=600

            [REGIONTYPE r_goldrock t_rock]
            DEFNAME=r_goldrock
            RESOURCES=100.0 mr_gold
            RESOURCES=100.0 mr_iron
            """);
        // Only the mixed group on this area.
        foreach (var r in world.FindAllRegions(Rock).Select(x => x.Region).ToList())
            world.RemoveRegion(r.Uid);
        var area = new Region { Name = "gold area", MapIndex = 0 };
        area.AddRect(0, 0, 6143, 4095);
        area.AddRegionType(DefinitionLoader.StaticResources!.ResolveDefName("r_goldrock"));
        world.AddRegion(area);

        ch.SetSkill(SkillType.Mining, 100);
        var goldIdx = DefinitionLoader.StaticResources!.ResolveDefName("mr_gold").Index;
        for (short x = 200; x < 240; x++)
        {
            var tile = new Point3D(x, 200, 0, 0);
            Assert.True(engine.ProbeResource(ch, SkillType.Mining, tile).Success);
            Assert.NotEqual((uint)goldIdx, Bit(world, tile).More1 & 0x00FFFFFF);
        }
    }

    [Fact]
    public void WhenNoMemberCanBeDrawnMrNothingStandsIn()
    {
        // CWorldMap.cpp:115-120: a bad index from the group draws mr_nothing - with no
        // AMOUNT, an empty bit that refuses the swing (DEFMSG_MINING_2).
        var (world, engine, ch) = Setup("""

            [REGIONRESOURCE mr_nothing]
            DEFNAME=mr_nothing
            REAP=0
            REGEN=600

            [ITEMDEF i_ore_gold]
            ID=019b7
            NAME=Gold Ore
            SKILLMAKE=mining 85.0

            [REGIONRESOURCE mr_gold]
            DEFNAME=mr_gold
            AMOUNT=5
            REAP=i_ore_gold
            SKILL=0.0
            REGEN=600

            [REGIONTYPE r_goldonly t_rock]
            DEFNAME=r_goldonly
            RESOURCES=100.0 mr_gold
            """);
        foreach (var r in world.FindAllRegions(Rock).Select(x => x.Region).ToList())
            world.RemoveRegion(r.Uid);
        var area = new Region { Name = "gold area", MapIndex = 0 };
        area.AddRect(0, 0, 6143, 4095);
        area.AddRegionType(DefinitionLoader.StaticResources!.ResolveDefName("r_goldonly"));
        world.AddRegion(area);

        var probe = engine.ProbeResource(ch, SkillType.Mining, Rock);
        Assert.True(probe.Handled);
        Assert.True(probe.Depleted);
        var nothing = DefinitionLoader.StaticResources!.ResolveDefName("mr_nothing");
        Assert.Equal((uint)nothing.Index, Bit(world, Rock).More1 & 0x00FFFFFF);
    }

    // --- the stages -----------------------------------------------------------

    [Fact]
    public void AFailedSkillCheckIsSilent()
    {
        // The FAIL stage returns 0 and prints nothing (CCharSkill.cpp:1403/1500/1604);
        // the pack's own @Fail says what it wants to.
        var (world, engine, ch) = Setup(alwaysSucceed: false);
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 0;
        Tool(world, ch, ItemType.WeaponMacePick);
        var sink = new Sink(ch, world);

        Assert.False(ActiveSkillEngine.Mining(sink, Rock, engine, world));
        Assert.Empty(sink.Log);
        Assert.Empty(sink.Delivered);
    }

    [Fact]
    public void MiningAndLumberjackingAnnounceNoCatchButFishingNamesIt()
    {
        var (world, engine, ch) = Setup();
        var pick = Tool(world, ch, ItemType.WeaponMacePick);
        var sink = new Sink(ch, world);
        Assert.True(ActiveSkillEngine.Mining(sink, Rock, engine, world));
        Assert.Empty(sink.Log);
        Assert.Single(sink.Delivered);

        ch.Unequip(Layer.OneHanded);
        var pole = Tool(world, ch, ItemType.FishPole);
        var fishSink = new Sink(ch, world);
        Assert.True(ActiveSkillEngine.Fishing(fishSink, new Point3D(103, 100, 0, 0), engine, world));
        Assert.Equal(ServerMessages.GetFormatted(Msg.FishingSuccess, fishSink.Delivered[0].GetName()),
            Assert.Single(fishSink.Log));
    }

    [Theory]
    [InlineData(100, 100, Msg.FishingClose)]   // under the feet (:1529-1533)
    [InlineData(105, 100, Msg.FishingReach)]   // RANGE defaults to 4 (:1522-1527)
    [InlineData(-1, 100, Msg.Fishing4)]        // the -1 point (:1501-1505)
    public void FishingRefusesAtTheReferenceDistances(short x, short y, string expected)
    {
        var (world, engine, ch) = Setup();
        Tool(world, ch, ItemType.FishPole);
        var sink = new Sink(ch, world);

        Assert.False(ActiveSkillEngine.Fishing(sink, new Point3D(x, y, 0, 0), engine, world));
        Assert.Equal(ServerMessages.Get(expected), Assert.Single(sink.Log));
    }

    [Fact]
    public void FishingReachesFourTilesByDefault()
    {
        var (world, engine, ch) = Setup();
        Tool(world, ch, ItemType.FishPole);
        var sink = new Sink(ch, world);
        Assert.True(ActiveSkillEngine.Fishing(sink, new Point3D(104, 100, 0, 0), engine, world));
    }

    [Fact]
    public void NobodyFishesFromInsideAHouseOrThroughAMultisFloor()
    {
        var (world, engine, ch) = Setup();
        Tool(world, ch, ItemType.FishPole);

        var house = new Region { Name = "house", MapIndex = 0, Flags = RegionFlag.House };
        house.AddRect(98, 98, 101, 101);
        world.AddRegion(house);
        var sink = new Sink(ch, world);
        Assert.False(ActiveSkillEngine.Fishing(sink, new Point3D(103, 100, 0, 0), engine, world));
        Assert.Equal(ServerMessages.Get(Msg.Fishing3), Assert.Single(sink.Log));

        world.RemoveRegion(house.Uid);
        var ship = new Region { Name = "ship", MapIndex = 0, Flags = RegionFlag.Ship };
        ship.AddRect(103, 98, 106, 102);
        world.AddRegion(ship);
        var sink2 = new Sink(ch, world);
        Assert.False(ActiveSkillEngine.Fishing(sink2, new Point3D(103, 100, 0, 0), engine, world));
        Assert.Equal(ServerMessages.Get(Msg.Fishing4), Assert.Single(sink2.Log));
    }

    [Fact]
    public void MiningAtYourOwnFeetIsTooClose()
    {
        var (world, engine, ch) = Setup();
        Tool(world, ch, ItemType.WeaponMacePick);
        var sink = new Sink(ch, world);
        Assert.False(ActiveSkillEngine.Mining(sink, Here, engine, world));
        Assert.Equal(ServerMessages.Get(Msg.MiningClose), Assert.Single(sink.Log));
    }

    [Fact]
    public void AnEmptyBitAnswersTheSecondMessage()
    {
        var (world, engine, ch) = Setup(amount: 1);
        Tool(world, ch, ItemType.WeaponMacePick);
        Assert.True(ActiveSkillEngine.Mining(new Sink(ch, world), Rock, engine, world));

        var sink = new Sink(ch, world);
        Assert.False(ActiveSkillEngine.Mining(sink, Rock, engine, world));
        Assert.Equal(ServerMessages.Get(Msg.Mining2), Assert.Single(sink.Log));
    }

    [Fact]
    public void ADaggerHacksKindlingOffATree()
    {
        // Skill_Lumberjack with an IT_WEAPON_FENCE tool: DEFMSG_LUMBERJACKING_5, one
        // ITEMID_KINDLING1, one unit off the bit (CCharSkill.cpp:1672-1678).
        var (world, engine, ch) = Setup(amount: 5);
        var dagger = Tool(world, ch, ItemType.WeaponFence);
        ch.ActPrv = dagger.Uid;
        var sink = new Sink(ch, world);

        Assert.True(ActiveSkillEngine.Lumberjacking(sink, Rock, engine, world));
        var kindling = Assert.Single(sink.Delivered);
        Assert.Equal(GatheringEngine.KindlingGraphic, kindling.BaseId);
        Assert.Equal(ServerMessages.Get(Msg.Lumberjacking5), Assert.Single(sink.Log));
        Assert.Equal(4, GatheringEngine.NaturalResourceAmount(Bit(world, Rock)));
    }

    [Fact]
    public void TheToolIsTheOneThatStartedTheSkill()
    {
        // m_Act_Prv_UID (CClientTarg.cpp:1809): an axe in hand counts as the tool for
        // lumberjacking, a pole in the pack does not make a miner.
        var (world, engine, ch) = Setup();
        var sink = new Sink(ch, world);
        sink.Pack[ItemType.FishPole] = world.CreateItem();
        Assert.False(ActiveSkillEngine.Mining(sink, Rock, engine, world));
        Assert.Equal(ServerMessages.Get(Msg.MiningTool), Assert.Single(sink.Log));

        var axe = Tool(world, ch, ItemType.WeaponAxe, Layer.TwoHanded);
        ch.ActPrv = axe.Uid;
        var sink2 = new Sink(ch, world);
        Assert.True(ActiveSkillEngine.Lumberjacking(sink2, Rock, engine, world));
    }

    // --- the client start -------------------------------------------------------

    private static SphereNet.Game.Clients.GameClient StartFishingClient(GameWorld world, Character ch,
        GatheringEngine engine, int id)
    {
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new SphereNet.Game.Accounts.AccountManager(lf), id);
        TestHarness.AttachCharacter(client, ch);
        client.SetEngines(
            skillHandlers: new SkillHandlers(world, engine),
            triggerDispatcher: new SphereNet.Game.Scripting.TriggerDispatcher());
        return client;
    }

    [Fact]
    public void AFishingStartDropsASplashAtTheSpot()
    {
        // Skill_Fishing SKTRIG_START: CreateBase(ITEMID_FX_SPLASH), TYPE t_water_wash,
        // MoveToDecay one second (CCharSkill.cpp:1560-1566) - before the first stroke.
        var (world, engine, ch) = Setup("""

            [SKILL 18]
            DEFNAME=Skill_Fishing
            KEY=Fishing
            DELAY=8.0
            FLAGS=skf_gather
            RANGE=4
            """);
        var pole = Tool(world, ch, ItemType.FishPole, Layer.TwoHanded);
        var client = StartFishingClient(world, ch, engine, 1611);
        var spot = new Point3D(103, 100, 0, 0);

        ((SphereNet.Game.Clients.IClientContext)client).StartSkillFromTool(SkillType.Fishing, SphereNet.Core.Types.Serial.Invalid, null, spot, pole);

        Assert.True(ch.HasActiveSkillPending());
        var splash = Assert.Single(world.GetItemsInRange(spot, 0), i => i.BaseId == 0x352D);
        Assert.Equal(ItemType.WaterWash, splash.ItemType);
        Assert.False(splash.IsAttr(ObjAttributes.Move_Never));
        Assert.InRange(splash.DecayTime - Environment.TickCount64, 500, 1100);
    }

    [Fact]
    public void AFishingStartWithNoWaterResourceIsRefusedBeforeAnyStroke()
    {
        // No bit (DEFMSG_FISHING_1): the START stage answers -SKTRIG_QTY and the skill
        // is cleaned up - no timer, no splash (CCharSkill.cpp:1548-1553).
        var (world, engine, ch) = Setup();
        foreach (var r in world.FindAllRegions(Here).Select(x => x.Region).ToList())
            world.RemoveRegion(r.Uid);
        var pole = Tool(world, ch, ItemType.FishPole, Layer.TwoHanded);
        var client = StartFishingClient(world, ch, engine, 1612);
        var spot = new Point3D(103, 100, 0, 0);

        ((SphereNet.Game.Clients.IClientContext)client).StartSkillFromTool(SkillType.Fishing, SphereNet.Core.Types.Serial.Invalid, null, spot, pole);

        Assert.False(ch.HasActiveSkillPending());
        Assert.DoesNotContain(world.GetItemsInRange(spot, 0), i => i.BaseId == 0x352D);
    }

    // --- the tile ---------------------------------------------------------------

    [Fact]
    public void ATreeIsFoundByItsType()
    {
        // IsItemTypeNear(pt, IT_TREE, 0) looks at the TYPE of what is there - a
        // dynamic item here - never at tile names (CWorldMap.cpp:676-697).
        var (world, _, _) = Setup();
        var tile = new Point3D(150, 150, 0, 0);
        Assert.False(NaturalResourceTiles.IsItemTypeAt(world, tile, ItemType.Tree));

        var tree = world.CreateItem();
        tree.BaseId = 0x0CCA;
        tree.ItemType = ItemType.Tree;
        world.PlaceItem(tree, tile);
        Assert.True(NaturalResourceTiles.IsItemTypeAt(world, tile, ItemType.Tree));
        Assert.True(NaturalResourceTiles.IsTypeNearTop(world, tile, ItemType.Tree));
        Assert.False(NaturalResourceTiles.IsItemTypeAt(world, tile, ItemType.Rock));
    }
}
