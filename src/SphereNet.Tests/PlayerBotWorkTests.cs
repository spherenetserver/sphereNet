using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Bots working for a living: a gathering shift with the tool and its target
/// cursor, a tile found empty left for another, a worked-out place given up, ore
/// smelted at the forge, a smith making what its ingots allow, goods sold to the
/// vendor that buys them (tools and materials kept), a lost tool bought again, a job
/// taken up again after a restart, and trade talk naming goods really carried.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotWorkTests
{
    // A road A (100,100) - B (110,100) - C (120,100) - D (130,100): a forge by A, a
    // smith's shop by B, a grove off C (an outline, no stopping spot), a mine by D.
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "C" ] },
          { "Name": "C", "X": 120, "Y": 100, "Z": 0, "Connects": [ "D" ] },
          { "Name": "D", "X": 130, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Bank", "X": 100, "Y": 96, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 100, "Y": 97, "Z": 0 } ] },
          { "Name": "Town Forge", "X": 100, "Y": 104, "Z": 0, "Type": "Forge", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 100, "Y": 102, "Z": 0 } ] },
          { "Name": "Town Smith", "X": 110, "Y": 104, "Z": 0, "Type": "VendorSmith", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 110, "Y": 102, "Z": 0 } ] },
          { "Name": "Town Grove", "X": 121, "Y": 108, "Z": 0, "Type": "LumberSpot", "City": "Town",
            "NearestWaypoint": "C", "Polygon": [ [117, 104], [125, 104], [125, 112], [117, 112] ] },
          { "Name": "Town Mine", "X": 131, "Y": 100, "Z": 0, "Type": "MiningSpot", "City": "Town",
            "NearestWaypoint": "D", "Arrivals": [ { "X": 131, "Y": 101, "Z": 0 } ] }
        ] }
        """;

    // No DELAY on the skills: a swing and a craft resolve in the act that starts them,
    // so the tests need no wall clock.
    private const string Definitions = """
        [SKILL 45]
        DEFNAME=Skill_Mining
        KEY=Mining
        FLAGS=skf_gather
        RANGE=2

        [SKILL 44]
        DEFNAME=Skill_Lumberjack
        KEY=Lumberjacking
        FLAGS=skf_gather
        RANGE=2

        [SKILL 7]
        DEFNAME=Skill_Blacksmith
        KEY=Blacksmithing
        FLAGS=skf_craft

        [ITEMDEF 019b7]
        DEFNAME=i_ore_iron
        NAME=iron ore
        TYPE=t_ore
        TDATA1=i_ingot_iron
        VALUE=4
        WEIGHT=2

        [ITEMDEF 01bf2]
        DEFNAME=i_ingot_iron
        NAME=iron ingot
        TYPE=t_ingot
        VALUE=8
        WEIGHT=0.1

        [ITEMDEF 01bdd]
        DEFNAME=i_log
        NAME=log
        TYPE=t_log
        VALUE=1
        WEIGHT=2

        [ITEMDEF 0f51]
        DEFNAME=i_dagger
        NAME=dagger
        TYPE=t_weapon_fence
        VALUE=21
        WEIGHT=1
        RESOURCES=4 i_ingot_iron
        SKILLMAKE=Blacksmithing 0.0

        [ITEMDEF 0e85]
        DEFNAME=i_pickaxe
        NAME=pickaxe
        TYPE=t_weapon_mace_pick
        VALUE=24
        WEIGHT=5

        [ITEMDEF 0f43]
        DEFNAME=i_hatchet
        NAME=hatchet
        TYPE=t_weapon_axe
        VALUE=33
        WEIGHT=5

        [SKILL 13]
        DEFNAME=Skill_Cooking
        KEY=Cooking
        FLAGS=skf_craft

        [ITEMDEF 09cc]
        DEFNAME=i_fish_big
        NAME=fish
        TYPE=t_fish
        VALUE=2
        WEIGHT=1

        [ITEMDEF 097a]
        DEFNAME=i_fish_cut_raw
        NAME=raw fish steak
        TYPE=t_meat_raw
        VALUE=3
        WEIGHT=0.1

        [ITEMDEF 097b]
        DEFNAME=i_fish_cut_cooked
        NAME=fish steak
        TYPE=t_food
        VALUE=3
        WEIGHT=0.1
        RESOURCES=1 i_fish_cut_raw
        SKILLMAKE=Cooking 0.0, t_cooking

        [ITEMDEF 097f]
        DEFNAME=i_fry_pan
        NAME=frying pan
        TYPE=t_cooking
        VALUE=8
        WEIGHT=1

        [ITEMDEF 0f52]
        DEFNAME=i_test_knife
        NAME=knife
        TYPE=t_weapon_fence
        LAYER=1
        VALUE=30
        RESOURCES=2 i_ingot_iron
        SKILLMAKE=Blacksmithing 0.0, Armslore 90.0

        [ITEMDEF 0f47]
        DEFNAME=i_test_greataxe
        NAME=great axe
        TYPE=t_weapon_axe
        LAYER=2
        TWOHANDS=Y

        [CHARDEF 0190]
        DEFNAME=c_test_woodsman
        NAME=woodsman
        ON=@Create
           ITEM=i_test_knife
           ITEM=i_test_greataxe

        [ITEMDEF 013e3]
        DEFNAME=i_hammer_smith
        NAME=smith's hammer
        TYPE=t_weapon_mace_smith
        VALUE=50
        WEIGHT=7

        [ITEMDEF 0fb1]
        DEFNAME=i_forge
        TYPE=t_forge

        [ITEMDEF 01363]
        DEFNAME=i_rock_face
        TYPE=t_rock

        [ITEMDEF 0cd0]
        DEFNAME=i_tree_oak
        TYPE=t_tree

        [REGIONRESOURCE mr_iron]
        DEFNAME=mr_iron
        AMOUNT=3
        REAP=i_ore_iron
        REAPAMOUNT=1
        SKILL=0.0

        [REGIONRESOURCE mr_tree]
        DEFNAME=mr_tree
        AMOUNT=4
        REAP=i_log
        REAPAMOUNT=1
        SKILL=0.0

        [REGIONTYPE r_test_rock t_rock]
        RESOURCES=100.0 mr_iron

        [REGIONTYPE r_test_tree t_tree]
        RESOURCES=100.0 mr_tree
        """;

    private sealed class ListLogger : ILogger
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning) Warnings.Add(formatter(state, exception) + " " + exception);
        }
    }

    private sealed class Rig
    {
        public required GameWorld World { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public required ListLogger Log { get; init; }
        public required CraftingEngine Crafting { get; init; }
        public required ResourceHolder Resources { get; init; }
        public long Now { get; set; } = 1_000_000;

        /// <summary>Tick until <paramref name="done"/> or <paramref name="ms"/> have passed.</summary>
        public bool Run(int ms, Func<bool>? done = null)
        {
            for (int t = 0; t < ms; t += 100)
            {
                if (done?.Invoke() == true) break;
                Engine.Tick(Now);
                Now += 100;
            }
            Assert.Empty(Log.Warnings);
            return done?.Invoke() ?? true;
        }
    }

    private static Rig MakeRig(Dictionary<string, string[]>? chat = null, string[]? scenes = null)
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_botwork_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, Definitions);
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = Path.GetDirectoryName(file) ?? "" };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        File.Delete(file);

        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;
        TestHarness.AttachLoadedRegionTypes(world);
        // Every roll succeeds: the dice are not under test.
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        // The sale follows the list by engine time here; no wall clock passes.
        GameClient.AllowBuySellAgent = true;

        var crafting = new CraftingEngine(world);
        crafting.LoadRecipesFromDefs(resources);
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        var log = new ListLogger();
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources, nav,
            BotChatBook.FromLines("en", chat ?? new Dictionary<string, string[]>()), log, seed: 7)
        {
            TargetCount = 1000,
            Scenes = BotSceneBook.Parse(scenes ?? []),
            Gravity = 0,
            WorkMinMs = 60_000,
            WorkMaxMs = 60_000,
            WorkLookAroundPercent = 0,
        };
        engine.SetClock(1_000_000); // the rig's clock, not the machine's uptime
        engine.AddGatherTrade(new BotGatherTrade(SkillType.Mining, ItemType.WeaponMacePick, ["MiningSpot"]));
        engine.AddGatherTrade(new BotGatherTrade(SkillType.Lumberjacking, ItemType.WeaponAxe, ["LumberSpot"]));
        engine.AddCraftTrade(new BotCraftTrade(SkillType.Blacksmithing, ItemType.WeaponMaceSmith, InHand: true,
            [ItemType.Ingot], ["Forge"]));
        engine.AddSaleRule(new BotSaleRule(ItemType.WeaponFence, ["VendorSmith"]));
        engine.AddSaleRule(new BotSaleRule(ItemType.Ingot, ["VendorSmith"]));
        engine.AddSaleRule(new BotSaleRule(ItemType.Log, ["VendorSmith"]));
        engine.CreateClient = ch =>
        {
            var c = new GameClient(new SphereNet.Network.State.NetState(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance),
                world, new SphereNet.Game.Accounts.AccountManager(lf), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
            c.SetEngines(skillHandlers: new SkillHandlers(world, new GatheringEngine(world)), craftingEngine: crafting,
                triggerDispatcher: new TriggerDispatcher());
            c.AttachServerDriven(ch);
            return c;
        };
        engine.AddCraftTrade(new BotCraftTrade(SkillType.Cooking, ItemType.CookingTool, InHand: false,
            [ItemType.Fish, ItemType.MeatRaw], ["Forge"]));
        return new Rig { World = world, Engine = engine, Log = log, Crafting = crafting, Resources = resources };
    }

    private static Item Give(Rig rig, Character ch, string defName, int amount = 1, Layer hand = Layer.None)
    {
        int index = DefinitionLoader.ResolveItemDefIndexByName(defName);
        Assert.NotEqual(0, index);
        var item = rig.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, index));
        item.Amount = (ushort)amount;
        if (hand != Layer.None) Assert.True(ch.Equip(item, hand));
        else Assert.True(ch.Backpack!.TryAddItem(item));
        return item;
    }

    private static Item Ground(Rig rig, string defName, int x, int y)
    {
        int index = DefinitionLoader.ResolveItemDefIndexByName(defName);
        var item = rig.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, index));
        item.SetAttr(ObjAttributes.Move_Never);
        rig.World.PlaceItem(item, new Point3D((short)x, (short)y, 0, 0));
        return item;
    }

    private static Character Bot(Rig rig, Point3D at, Action<Character>? setup = null)
    {
        var ch = rig.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Bot" + ch.Uid.Value;
        ch.Str = 80; ch.Dex = 80; ch.Int = 40;
        ch.MaxStam = 80; ch.Stam = 80; ch.MaxHits = 80; ch.Hits = 80;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.RiderTag, "0");
        ch.SetTag(PlayerBotEngine.RoleTag, "-");
        var pack = rig.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        setup?.Invoke(ch);
        rig.World.PlaceCharacter(ch, at);
        rig.Engine.AdoptSaved();
        return ch;
    }

    private static int Count(Rig rig, Character ch, ItemType type) =>
        rig.World.GetContainerContents(ch.Backpack!.Uid).Where(i => !i.IsDeleted && i.ItemType == type).Sum(i => (int)i.Amount);

    private static Character Vendor(Rig rig, int x, int y, params (ushort BaseId, ItemType Type)[] buys)
    {
        var vendor = rig.World.CreateCharacter();
        vendor.Name = "smith";
        vendor.NpcBrain = NpcBrainType.Vendor;
        rig.World.PlaceCharacter(vendor, new Point3D((short)x, (short)y, 0, 0));
        foreach (var (baseId, type) in buys)
            TestHarness.GiveVendorBuySample(rig.World, vendor, baseId, type);
        VendorEngine.SetVendorGold(vendor, 5000);
        return vendor;
    }

    private static Character Miner(Rig rig, Point3D at) => Bot(rig, at, ch => ch.SetSkill(SkillType.Mining, 800));

    [Fact]
    public void AMinerWalksToTheMineAndDigsOreWithItsPick()
    {
        var rig = MakeRig();
        Ground(rig, "i_rock_face", 133, 101);
        Ground(rig, "i_rock_face", 133, 102);
        var bot = Miner(rig, new Point3D(100, 100, 0, 0));
        var pick = Give(rig, bot, "i_pickaxe");
        var mind = rig.Engine.MindOf(bot)!;
        Assert.True(rig.Engine.IsTradesman(mind));
        Assert.True(rig.Engine.CanWork(mind));

        // The stay ends: a tradesman goes to work.
        Assert.True(rig.Run(2_000, () => mind.Work != null));
        Assert.Equal(BotJob.Gather, mind.Work!.Job);
        Assert.Equal("Town Mine", mind.Work.Place.Name);
        Assert.Equal(BotMode.Travel, mind.Mode);
        Assert.StartsWith("Gather|Mining|Town Mine", bot.TryGetTag(PlayerBotEngine.WorkTag, out string? tag) ? tag : "");

        Assert.True(rig.Run(120_000, () => mind.Mode == BotMode.Work), $"never at work; at {bot.Position}");
        Assert.True(rig.Run(60_000, () => Count(rig, bot, ItemType.Ore) >= 2),
            "no ore dug: " + string.Join(" | ", mind.Trace().Select(t => t.Text)));
        Assert.True(rig.Engine.WindowGathers >= 2);
        // The ore came out of the engine's own vein under the rock, through the pick's cursor.
        Assert.Contains(rig.World.GetItemsInRange(new Point3D(133, 101, 0, 0), 1),
            i => i.BaseId == GatheringEngine.MarkerGraphic);
        Assert.False(pick.IsDeleted);
    }

    [Fact]
    public void ATileFoundEmptyIsLeftForAnother()
    {
        var rig = MakeRig();
        Ground(rig, "i_rock_face", 133, 101);
        Ground(rig, "i_rock_face", 134, 103);
        var bot = Miner(rig, new Point3D(131, 101, 0, 0));
        Give(rig, bot, "i_pickaxe");
        var mind = rig.Engine.MindOf(bot)!;
        Assert.True(rig.Engine.StartWork(mind, rig.Now));
        Assert.Equal(BotMode.Work, mind.Mode); // it stood at the mine already

        // Each vein holds three: the fourth piece of ore is the second tile's.
        Assert.True(rig.Run(60_000, () => Count(rig, bot, ItemType.Ore) >= 4),
            $"ore {Count(rig, bot, ItemType.Ore)}: " + string.Join(" | ", mind.Trace().Select(t => t.Text)));
        Assert.NotNull(mind.Work);
        Assert.NotEmpty(mind.Work!.Spent);
    }

    [Fact]
    public void AWorkedOutPlaceIsGivenUpAndLeftAlone()
    {
        var rig = MakeRig();
        Ground(rig, "i_rock_face", 133, 101);
        var bot = Miner(rig, new Point3D(131, 101, 0, 0));
        Give(rig, bot, "i_pickaxe");
        var mind = rig.Engine.MindOf(bot)!;
        Assert.True(rig.Engine.StartWork(mind, rig.Now));

        // One vein of three, then nothing to work: the job ends by itself.
        mind.LingerUntilMs = long.MaxValue;
        Assert.True(rig.Run(120_000, () => mind.Work == null),
            string.Join(" | ", mind.Trace().Select(t => t.Text)));
        Assert.Equal(3, Count(rig, bot, ItemType.Ore));
        Assert.Equal(BotMode.Linger, mind.Mode);
        Assert.False(bot.TryGetTag(PlayerBotEngine.WorkTag, out _));
        // Nobody is sent to that pit again for a while; the ore still wants a forge.
        var other = Miner(rig, new Point3D(130, 100, 0, 0));
        Give(rig, other, "i_pickaxe");
        Assert.False(rig.Engine.StartWork(rig.Engine.MindOf(other)!, rig.Now));
    }

    [Fact]
    public void OreIsCarriedToTheForgeAndSmelted()
    {
        var rig = MakeRig();
        Ground(rig, "i_forge", 100, 105);
        var bot = Miner(rig, new Point3D(120, 100, 0, 0));
        Give(rig, bot, "i_ore_iron", 6);
        var mind = rig.Engine.MindOf(bot)!;
        Assert.True(rig.Engine.CanWork(mind));

        // The stay ends with ore in the pack: the planner sends it to the forge.
        Assert.True(rig.Run(2_000, () => mind.Work != null));
        Assert.Equal(BotJob.Smelt, mind.Work!.Job);
        Assert.Equal("Town Forge", mind.Work.Place.Name);

        mind.LingerUntilMs = long.MaxValue;
        Assert.True(rig.Run(180_000, () => Count(rig, bot, ItemType.Ingot) > 0),
            $"no ingots; at {bot.Position}: " + string.Join(" | ", mind.Trace().Select(t => t.Text)));
        Assert.Equal(6, Count(rig, bot, ItemType.Ingot));
        Assert.Equal(0, Count(rig, bot, ItemType.Ore));
        Assert.True(bot.Position.GetDistanceTo(new Point3D(100, 105, 0, 0)) <= 2);
        Assert.Equal(1, rig.Engine.WindowSmelts);
    }

    [Fact]
    public void ASmithMakesWhatItsIngotsAllow()
    {
        var rig = MakeRig();
        Ground(rig, "i_forge", 100, 105);
        var bot = Bot(rig, new Point3D(100, 102, 0, 0), ch => ch.SetSkill(SkillType.Blacksmithing, 700));
        Give(rig, bot, "i_hammer_smith", hand: Layer.OneHanded);
        Give(rig, bot, "i_ingot_iron", 10);
        var mind = rig.Engine.MindOf(bot)!;

        rig.Engine.WorkMinMs = rig.Engine.WorkMaxMs = 3_600_000;
        Assert.True(rig.Engine.StartWork(mind, rig.Now));
        Assert.Equal(BotJob.Craft, mind.Work!.Job);
        mind.LingerUntilMs = long.MaxValue;
        // A craft's stroke is timed on the wall clock (a tenth of a second at least).
        for (int i = 0; i < 100 && mind.Work != null; i++)
        {
            rig.Run(3_000, () => mind.Work == null);
            Thread.Sleep(40);
        }
        Assert.True(mind.Work == null, string.Join(" | ", mind.Trace().Select(t => t.Text)));

        // Ten ingots, four a dagger: two daggers through the craft's own path, two ingots left.
        var daggers = rig.World.GetContainerContents(bot.Backpack!.Uid).Where(i => i.ItemType == ItemType.WeaponFence).ToList();
        Assert.Equal(2, daggers.Count);
        Assert.All(daggers, d => Assert.Equal(bot.Uid, d.Crafter));
        Assert.Equal(2, Count(rig, bot, ItemType.Ingot));
        Assert.Equal(2, rig.Engine.WindowCrafts);
    }

    [Fact]
    public void GoodsAreSoldToTheVendorThatBuysThemAndTheToolsKept()
    {
        var rig = MakeRig();
        var vendor = Vendor(rig, 110, 105, (0x0F51, ItemType.WeaponFence), (0x0E85, ItemType.WeaponMacePick),
            (0x1BF2, ItemType.Ingot));
        var bot = Bot(rig, new Point3D(100, 100, 0, 0), ch =>
        {
            ch.SetSkill(SkillType.Mining, 800);
            ch.SetSkill(SkillType.Blacksmithing, 700);
        });
        var pick = Give(rig, bot, "i_pickaxe");
        Give(rig, bot, "i_hammer_smith", hand: Layer.OneHanded);
        var ingots = Give(rig, bot, "i_ingot_iron", 2);
        var daggers = Enumerable.Range(0, 6).Select(_ =>
        {
            var d = Give(rig, bot, "i_dagger");
            d.Crafter = bot.Uid;
            return d;
        }).ToList();
        var mind = rig.Engine.MindOf(bot)!;

        Assert.True(rig.Engine.HasGoodsToSell(mind));
        Assert.Equal(6, rig.Engine.GoodsToSell(mind).Count);
        Assert.True(rig.Engine.StartSelling(mind, rig.Now));
        Assert.Equal("Town Smith", mind.Work!.Place.Name);
        mind.LingerUntilMs = long.MaxValue;
        long quote = daggers.Sum(d => VendorEngine.GetSellQuote(vendor, d).UnitPrice);
        Assert.True(quote > 0);
        Assert.True(rig.Run(180_000, () => mind.Work == null),
            string.Join(" | ", mind.Trace().Select(t => t.Text)));

        // Paid what the vendor's own list quoted, for the daggers and nothing else.
        Assert.All(daggers, d => Assert.True(d.IsDeleted || d.GetTopLevelObj() != bot));
        Assert.Equal(quote, VendorEngine.CountGold(bot));
        Assert.Equal(quote, rig.Engine.WindowSaleGold);
        Assert.Equal(1, rig.Engine.WindowSales);
        // The vendor buys picks and ingots too; the bot's own stay with it.
        Assert.Same(bot, pick.GetTopLevelObj());
        Assert.Same(bot, ingots.GetTopLevelObj());
        Assert.False(rig.Engine.HasGoodsToSell(mind));
    }

    [Fact]
    public void AShopWhereNobodyBuysIsNotWalkedToAgain()
    {
        var rig = MakeRig();
        Vendor(rig, 110, 105); // buys nothing
        var bot = Bot(rig, new Point3D(110, 102, 0, 0));
        Give(rig, bot, "i_log", 200);
        var mind = rig.Engine.MindOf(bot)!;
        Assert.True(rig.Engine.HasGoodsToSell(mind));
        Assert.True(rig.Engine.StartSelling(mind, rig.Now));
        mind.LingerUntilMs = long.MaxValue;
        Assert.True(rig.Run(30_000, () => mind.Work == null));
        Assert.Equal(200, Count(rig, bot, ItemType.Log));
        Assert.False(rig.Engine.StartSelling(mind, rig.Now));
    }

    [Fact]
    public void ALostToolIsBoughtAgain()
    {
        var rig = MakeRig();
        rig.Engine.AddNeed(new BotNeed("pickaxe", ItemType.WeaponMacePick, 1, 1, ["VendorSmith"]));
        var vendor = Vendor(rig, 110, 105);
        var stock = rig.World.CreateItem();
        stock.ItemType = ItemType.Container;
        vendor.Equip(stock, Layer.VendorStock);
        var forSale = rig.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(forSale, DefinitionLoader.ResolveItemDefIndexByName("i_pickaxe")));
        forSale.Amount = 5;
        stock.AddItem(forSale);

        var bot = Miner(rig, new Point3D(100, 100, 0, 0));
        var purse = rig.World.CreateItem();
        purse.BaseId = 0x0EED;
        purse.ItemType = ItemType.Gold;
        purse.Amount = 200;
        Assert.True(bot.Backpack!.TryAddItem(purse));
        var mind = rig.Engine.MindOf(bot)!;

        // A miner with no pick: no work, and the pick is what it is short of.
        Assert.True(rig.Engine.IsTradesman(mind));
        Assert.False(rig.Engine.CanWork(mind));
        Assert.Equal("pickaxe", rig.Engine.FirstNeed(mind)?.Name);

        Assert.True(rig.Run(240_000, () => Count(rig, bot, ItemType.WeaponMacePick) > 0),
            $"no pick bought; at {bot.Position}: " + string.Join(" | ", mind.Trace().Select(t => t.Text)));
        Assert.Null(rig.Engine.FirstNeed(mind));
        Assert.True(rig.Engine.CanWork(mind));
        Assert.True(VendorEngine.CountGold(bot) < 200);

        // In the hand it is still its pick: not a need, and not for sale.
        var pick = rig.World.GetContainerContents(bot.Backpack.Uid).First(i => i.ItemType == ItemType.WeaponMacePick);
        bot.Backpack.RemoveItem(pick);
        Assert.True(bot.Equip(pick, Layer.OneHanded));
        Assert.Null(rig.Engine.FirstNeed(mind));
    }

    [Fact]
    public void AJobIsTakenUpAgainAfterARestart()
    {
        var rig = MakeRig();
        Ground(rig, "i_rock_face", 133, 101);
        var bot = Miner(rig, new Point3D(100, 100, 0, 0));
        Give(rig, bot, "i_pickaxe");
        var mind = rig.Engine.MindOf(bot)!;
        Assert.True(rig.Engine.StartWork(mind, rig.Now));
        Assert.True(rig.Run(60_000, () => bot.X >= 112), $"did not set off; at {bot.Position}");
        Assert.Equal(BotMode.Travel, mind.Mode);

        // The server stops with the bot on the road: all that is left is the character and its tags.
        rig.Engine.Release(bot);
        Assert.True(bot.TryGetTag(PlayerBotEngine.WorkTag, out _));
        Assert.False(bot.TryGetTag(PlayerBotEngine.GoalTag, out _));
        Assert.Equal(1, rig.Engine.AdoptSaved());
        var again = rig.Engine.MindOf(bot)!;
        Assert.NotSame(mind, again);
        Assert.Null(again.Work);

        Assert.True(rig.Run(2_000, () => again.Work != null));
        Assert.Equal(BotJob.Gather, again.Work!.Job);
        Assert.Equal("Town Mine", again.Work.Place.Name);
        Assert.True(rig.Run(120_000, () => Count(rig, bot, ItemType.Ore) > 0),
            $"no ore after the restart; at {bot.Position}: " + string.Join(" | ", again.Trace().Select(t => t.Text)));
    }

    [Fact]
    public void ALumberjackGoesOnFromTheRoadsEndIntoTheGrove()
    {
        var rig = MakeRig();
        Ground(rig, "i_tree_oak", 121, 109);
        Ground(rig, "i_tree_oak", 123, 110);
        var bot = Bot(rig, new Point3D(100, 100, 0, 0), ch => ch.SetSkill(SkillType.Lumberjacking, 800));
        Give(rig, bot, "i_hatchet");
        var mind = rig.Engine.MindOf(bot)!;
        Assert.True(rig.Engine.StartWork(mind, rig.Now));
        var work = mind.Work!;
        Assert.Equal("Town Grove", work.Place.Name);
        // The grove has no stopping spot: the trip ends where its road does.
        Assert.NotSame(work.Place, work.TripGoal);
        Assert.Equal(new Point3D(120, 100, 0, 0), work.TripGoal.Pos);

        Assert.True(rig.Run(240_000, () => Count(rig, bot, ItemType.Log) >= 5),
            $"logs {Count(rig, bot, ItemType.Log)}; at {bot.Position}: " + string.Join(" | ", mind.Trace().Select(t => t.Text)));
    }

    [Fact]
    public void TheShiftEndsWhenThePackIsHeavy()
    {
        var rig = MakeRig();
        for (int y = 99; y <= 104; y++) Ground(rig, "i_rock_face", 133, y);
        var bot = Miner(rig, new Point3D(131, 101, 0, 0));
        bot.Str = 10; // carries 75 stones
        Give(rig, bot, "i_pickaxe");
        Give(rig, bot, "i_log", 22); // 44 stones of ballast: six pieces of ore to the limit
        var mind = rig.Engine.MindOf(bot)!;
        rig.Engine.WorkMinMs = rig.Engine.WorkMaxMs = 3_600_000;
        Assert.True(rig.Engine.StartWork(mind, rig.Now));
        mind.LingerUntilMs = long.MaxValue;
        Assert.True(rig.Run(240_000, () => mind.Work == null), string.Join(" | ", mind.Trace().Select(t => t.Text)));
        int ore = Count(rig, bot, ItemType.Ore);
        Assert.InRange(ore, 1, 12);
        Assert.True(bot.GetTotalWeightTenths() >= bot.MaxWeight * 10 * rig.Engine.WorkLoadPercent / 100,
            $"stopped at {bot.GetTotalWeightTenths()} of {bot.MaxWeight * 10}");
        Assert.Contains(mind.Trace(), t => t.Text.Contains("pack full"));
    }

    [Fact]
    public void AFighterWithNoTradeNeverWorksAndKeepsItsSpareBlade()
    {
        var rig = MakeRig();
        Ground(rig, "i_rock_face", 133, 101);
        var bot = Bot(rig, new Point3D(100, 100, 0, 0), ch =>
        {
            ch.SetSkill(SkillType.Swordsmanship, 900);
            ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_warrior_m");
        });
        Give(rig, bot, "i_pickaxe");
        var blade = Give(rig, bot, "i_dagger");
        var mind = rig.Engine.MindOf(bot)!;
        Assert.False(rig.Engine.IsTradesman(mind));
        Assert.False(rig.Engine.CanWork(mind));
        Assert.False(rig.Engine.StartWork(mind, rig.Now));
        // Unarmed, the dagger in its pack is its weapon, not merchandise.
        Assert.DoesNotContain(blade, rig.Engine.GoodsToSell(mind));
        rig.Run(5_000);
        Assert.Null(mind.Work);
    }

    [Fact]
    public void TradeTalkNamesTheGoodsReallyCarried()
    {
        var rig = MakeRig(
            chat: new Dictionary<string, string[]>
            {
                ["wts_offer"] = ["selling {item}"],
                ["token_item"] = ["gm katana"],
            },
            scenes: ["wts 1 : A:wts_offer"]);
        var said = new List<string>();
        rig.Engine.OnLine = (_, _, line) => said.Add(line);
        rig.Engine.ChatPaceSeconds = 1;

        var player = rig.World.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.Name = "Watcher";
        rig.World.PlaceCharacter(player, new Point3D(103, 100, 0, 0));
        rig.World.AddOnlinePlayer(player);

        // With nothing to sell the word comes from the list.
        var bot = Bot(rig, new Point3D(100, 100, 0, 0));
        var mind = rig.Engine.MindOf(bot)!;
        mind.LingerUntilMs = long.MaxValue;
        Assert.True(rig.Run(60_000, () => said.Count > 0));
        Assert.Equal("selling gm katana", said[0]);

        // Carrying a dagger of its own make, that is what it cries.
        said.Clear();
        Give(rig, bot, "i_dagger").Crafter = bot.Uid;
        Assert.True(rig.Run(60_000, () => said.Count > 0));
        Assert.Equal("selling dagger", said[0]);
    }

    [Fact]
    public void ACryForSomethingWantedDoesNotNameItsOwnGoods()
    {
        var rig = MakeRig(
            chat: new Dictionary<string, string[]>
            {
                ["wtb"] = ["buying {item}"],
                ["token_item"] = ["gm katana"],
            },
            scenes: ["wtb 1 : A:wtb"]);
        var said = new List<string>();
        rig.Engine.OnLine = (_, _, line) => said.Add(line);
        rig.Engine.ChatPaceSeconds = 1;
        var player = rig.World.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.Name = "Watcher";
        rig.World.PlaceCharacter(player, new Point3D(103, 100, 0, 0));
        rig.World.AddOnlinePlayer(player);

        var bot = Bot(rig, new Point3D(100, 100, 0, 0));
        Give(rig, bot, "i_dagger").Crafter = bot.Uid;
        rig.Engine.MindOf(bot)!.LingerUntilMs = long.MaxValue;
        Assert.True(rig.Run(60_000, () => said.Count > 0));
        Assert.Equal("buying gm katana", said[0]);
    }

    /// <summary>A cook cuts its catch with its blade (the blade's double click, its
    /// cursor on the fish) and cooks the steaks at a fire. The cut fish must BE the raw
    /// steak by definition, not only by its picture: the cooked steak's RESOURCES line
    /// names that definition (the engine's blade-on-fish left the fish's own
    /// definition on the item, and nobody could cook what they had cut).</summary>
    [Fact]
    public void ACookCutsItsCatchAndCooksTheSteaks()
    {
        var rig = MakeRig();
        Ground(rig, "i_forge", 100, 105);
        var bot = Bot(rig, new Point3D(100, 103, 0, 0), ch => ch.SetSkill(SkillType.Cooking, 600));
        Give(rig, bot, "i_fry_pan");
        Give(rig, bot, "i_dagger");
        var fish = Give(rig, bot, "i_fish_big", 2);
        var mind = rig.Engine.MindOf(bot)!;
        int raw = DefinitionLoader.ResolveItemDefIndexByName("i_fish_cut_raw");
        Assert.NotEqual(raw, ResourceMatch.ItemDefIndexOf(fish));

        rig.Engine.WorkMinMs = rig.Engine.WorkMaxMs = 3_600_000;
        Assert.True(rig.Engine.CanWork(mind));
        Assert.True(rig.Engine.StartWork(mind, rig.Now));
        Assert.Equal(BotJob.Craft, mind.Work!.Job);
        Assert.Equal(SkillType.Cooking, mind.Work.Skill);
        mind.LingerUntilMs = long.MaxValue;
        for (int i = 0; i < 200 && mind.Work != null; i++)
        {
            rig.Run(3_000, () => mind.Work == null);
            Thread.Sleep(30);
        }
        Assert.True(mind.Work == null, string.Join(" | ", mind.Trace().Select(t => t.Text)));

        // Two fish are eight steaks, and every one of them went through the pan.
        var cooked = rig.World.GetContainerContents(bot.Backpack!.Uid).Where(i => i.ItemType == ItemType.Food).ToList();
        Assert.Equal(8, cooked.Sum(i => (int)i.Amount));
        Assert.All(cooked, c => Assert.Equal(bot.Uid, c.Crafter));
        Assert.Equal(0, Count(rig, bot, ItemType.Fish));
        Assert.Equal(0, Count(rig, bot, ItemType.MeatRaw));
        // Plain work on a pile goes several at a time (the menu's make-amount): a few
        // rounds at the pan, not eight.
        Assert.InRange(rig.Engine.WindowCrafts, 1, 4);
    }

    /// <summary>The blade-on-fish use by itself, as a player does it: afterwards the item
    /// is the raw fish steak's definition (Source-X CItem::SetID re-bases it).</summary>
    [Fact]
    public void AFishCutWithABladeBecomesTheSteakByDefinition()
    {
        var rig = MakeRig();
        var bot = Bot(rig, new Point3D(100, 100, 0, 0));
        var knife = Give(rig, bot, "i_dagger");
        var fish = Give(rig, bot, "i_fish_big", 3);
        var client = rig.Engine.MindOf(bot)!.Client!;

        client.HandleDoubleClick(knife.Uid.Value);
        Assert.True(client.HasPendingTarget);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId, fish.Uid.Value, 0, 0, 0, fish.DispIdFull);

        Assert.Equal(0x097A, fish.BaseId);
        Assert.Equal(12, fish.Amount);
        Assert.Equal(ItemType.MeatRaw, fish.ItemType);
        Assert.Equal(DefinitionLoader.ResolveItemDefIndexByName("i_fish_cut_raw"), ResourceMatch.ItemDefIndexOf(fish));
        Assert.Equal("raw fish steak", fish.Name);
        var recipe = rig.Crafting.GetRecipe(DefinitionLoader.ResolveItemDefIndexByName("i_fish_cut_cooked"))!;
        Assert.Equal(12, rig.Crafting.TestReplication(bot, recipe, 100));
    }

    /// <summary>A piece a shop turned down is not made again while something else can
    /// be: the smith makes what sells, and keeps the other piece for when nothing else
    /// can be made.</summary>
    [Fact]
    public void APieceNobodyBoughtIsNotMadeAgainWhileSomethingElseCanBe()
    {
        var rig = MakeRig();
        Ground(rig, "i_forge", 100, 105);
        Vendor(rig, 110, 105, (0x0F51, ItemType.WeaponFence)); // buys daggers, not knives
        var bot = Bot(rig, new Point3D(100, 103, 0, 0), ch =>
        {
            ch.SetSkill(SkillType.Blacksmithing, 1000);
            ch.SetSkill(SkillType.ArmsLore, 1000); // the knife asks for it; the other smiths here have none
        });
        Give(rig, bot, "i_hammer_smith", hand: Layer.OneHanded);
        Give(rig, bot, "i_ingot_iron", 2);
        var mind = rig.Engine.MindOf(bot)!;
        rig.Engine.WorkMinMs = rig.Engine.WorkMaxMs = 3_600_000;
        mind.LingerUntilMs = long.MaxValue;

        void WorkItOut()
        {
            for (int i = 0; i < 200 && mind.Work != null; i++)
            {
                rig.Run(3_000, () => mind.Work == null);
                Thread.Sleep(30);
            }
            Assert.True(mind.Work == null, string.Join(" | ", mind.Trace().Select(t => t.Text)));
        }
        int Made(string defName) => rig.World.GetContainerContents(bot.Backpack!.Uid).Count(i =>
            i.Crafter == bot.Uid && ResourceMatch.ItemDefIndexOf(i) == DefinitionLoader.ResolveItemDefIndexByName(defName));

        // Two ingots are a knife, and nothing else.
        Assert.True(rig.Engine.StartWork(mind, rig.Now));
        WorkItOut();
        Assert.Equal(1, Made("i_test_knife"));

        // The smith's shop buys no knives.
        Assert.True(rig.Engine.StartSelling(mind, rig.Now));
        WorkItOut();
        Assert.Equal(1, Made("i_test_knife"));
        Assert.Equal(0, VendorEngine.CountGold(bot));
        Assert.False(rig.Engine.StartSelling(mind, rig.Now));

        // With ingots again it makes daggers, which do sell.
        Give(rig, bot, "i_ingot_iron", 8);
        Assert.True(rig.Engine.StartWork(mind, rig.Now));
        WorkItOut();
        Assert.Equal(2, Made("i_dagger"));
        Assert.Equal(1, Made("i_test_knife"));
        Assert.True(rig.Engine.StartSelling(mind, rig.Now));
        WorkItOut();
        Assert.Equal(0, Made("i_dagger"));
        Assert.True(VendorEngine.CountGold(bot) > 0);
    }

    /// <summary>A kit that names a one-handed weapon and then a two-handed one leaves
    /// the two-handed one in the hands and the other in the pack: ITEM= equips through
    /// the same hand rule a player's equip does (Source-X CChar::CanEquipLayer). Both
    /// stayed in hand, and WEAPON - first hand first - answered with the knife, so a
    /// pack's "is your tool charged" check never saw the axe.</summary>
    [Fact]
    public void AKitsTwoHandedWeaponTakesTheOneHandedOneOutOfTheHand()
    {
        var rig = MakeRig();
        var lf = LoggerFactory.Create(_ => { });
        var interpreter = new SphereNet.Scripting.Execution.ScriptInterpreter(
            new SphereNet.Scripting.Expressions.ExpressionParser(),
            lf.CreateLogger<SphereNet.Scripting.Execution.ScriptInterpreter>());
        var runner = new SphereNet.Scripting.Execution.TriggerRunner(interpreter, rig.Resources,
            lf.CreateLogger<SphereNet.Scripting.Execution.TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = rig.Resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var ch = rig.World.CreateCharacter();
        ch.CharDefIndex = rig.Resources.ResolveDefName("c_test_woodsman").Index;
        ch.BaseId = 0x190; ch.BodyId = 0x190;
        rig.World.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        dispatcher.FireNpcLoadScript(ch, restock: false);

        Assert.Equal(ItemType.WeaponAxe, ch.GetEquippedItem(Layer.TwoHanded)?.ItemType);
        Assert.Null(ch.GetEquippedItem(Layer.OneHanded));
        Assert.NotNull(ch.Backpack);
        Assert.Contains(rig.World.GetContainerContents(ch.Backpack!.Uid), i => i.ItemType == ItemType.WeaponFence);
        Assert.True(ch.TryGetProperty("WEAPON", out string weapon));
        Assert.Equal($"0{ch.GetEquippedItem(Layer.TwoHanded)!.Uid.Value:x}", weapon);
    }
}
