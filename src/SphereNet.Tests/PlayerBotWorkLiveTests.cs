using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Crafting;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Working life against the Scripts-X pack the bots are written for: the townsfolk
/// archetypes come out with one whole trade, and a bot mines, smelts, smiths and
/// sells through the pack's own skills, item definitions, crafting events and vendor
/// templates. Skips when the pack is not in the repository.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotWorkLiveTests(ITestOutputHelper outp)
{
    private static string? Pack() => TestRepo.Optional("oldSphere/Scripts-X-main");

    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Forge", "X": 100, "Y": 104, "Z": 0, "Type": "Forge", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 100, "Y": 102, "Z": 0 } ] },
          { "Name": "Town Smith", "X": 104, "Y": 104, "Z": 0, "Type": "VendorSmith", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 104, "Y": 102, "Z": 0 } ] },
          { "Name": "Town Mine", "X": 111, "Y": 100, "Z": 0, "Type": "MiningSpot", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 111, "Y": 101, "Z": 0 } ] },
          { "Name": "Town Grove", "X": 111, "Y": 120, "Z": 0, "Type": "LumberSpot", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 111, "Y": 121, "Z": 0 } ] },
          { "Name": "Town Dock", "X": 111, "Y": 80, "Z": 0, "Type": "Dock", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 111, "Y": 81, "Z": 0 } ] },
          { "Name": "Town Carpenter", "X": 90, "Y": 120, "Z": 0, "Type": "VendorCarpenter", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 90, "Y": 121, "Z": 0 } ] },
          { "Name": "Town Tailor", "X": 90, "Y": 80, "Z": 0, "Type": "VendorTailor", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 90, "Y": 81, "Z": 0 } ] }
        ] }
        """;

    private sealed class Live
    {
        public required GameWorld World { get; init; }
        public required TriggerDispatcher Dispatcher { get; init; }
        public required ResourceHolder Resources { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public long Now { get; set; }

        /// <summary>Tick until <paramref name="done"/> or <paramref name="ms"/> of the
        /// wall clock have passed. The bots' own clock runs twenty times as fast (their
        /// walks and pauses need no waiting for); a skill's DELAY and a craft's stroke
        /// are the engine's and stay on the wall clock.</summary>
        public bool Run(int ms, Func<bool> done)
        {
            long until = Environment.TickCount64 + ms;
            while (!done() && Environment.TickCount64 < until)
            {
                Now += 100;
                Engine.Tick(Now);
                Thread.Sleep(5);
            }
            return done();
        }
    }

    private static Live Load(string pack)
    {
        var lf = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = pack };
        foreach (string file in Directory.EnumerateFiles(pack, "*.scp", SearchOption.AllDirectories))
            resources.LoadResourceFile(file);
        foreach (string file in Directory.EnumerateFiles(TestRepo.Tracked("playerbots/scripts"), "*.scp"))
            resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        var interpreter = new ScriptInterpreter(new ExpressionParser(), lf.CreateLogger<ScriptInterpreter>());
        var runner = new TriggerRunner(interpreter, resources, lf.CreateLogger<TriggerRunner>());
        var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };
        dispatcher.BuildUsedTriggerCache();

        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        // The client files say which hand a tool is held in (tiledata's layer): what a
        // double click on a pick, a hammer or a dagger equips it by.
        foreach (var (tool, hand) in new (ushort, Layer)[]
                 {
                     (0x0E85, Layer.OneHanded), (0x13E3, Layer.OneHanded), (0x0F51, Layer.OneHanded), // pick, hammer, dagger
                     (0x0F43, Layer.TwoHanded), (0x0DBF, Layer.TwoHanded),                             // hatchet, fishing pole
                 })
            map.SetSyntheticItemTile(tool, new SphereNet.MapData.Tiles.ItemTileData
            {
                Flags = SphereNet.MapData.Tiles.TileFlag.Wearable | SphereNet.MapData.Tiles.TileFlag.Weapon,
                Quality = (byte)hand,
                Weight = 5,
                Name = "tool",
            });
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        Item.ResolveDefName = defname =>
        {
            var rid = resources.ResolveDefName(defname);
            if (!rid.IsValid || rid.Type != ResType.ItemDef) return 0;
            var d = DefinitionLoader.GetItemDef(rid.Index);
            return d != null && d.DispIndex > 0 ? d.DispIndex : (ushort)rid.Index;
        };
        // An item's own @Create runs when it is made, as the server wires it: that is
        // where a pick gets its charges.
        Item.CreateTriggerHook = item =>
            dispatcher.FireItemTrigger(item, ItemTrigger.Create, new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item });
        Item.CreateTriggerSourceHook = (item, src) =>
            dispatcher.FireItemTrigger(item, ItemTrigger.Create, new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item, CharSrc = src });
        VendorEngine.World = world;
        // The land is the pack's ordinary land: its default rock, trees and water.
        var land = new SphereNet.Game.World.Regions.Region { Name = "test land", MapIndex = 0 };
        land.AddRect(0, 0, 511, 511);
        foreach (string name in new[] { "r_default_rock", "r_default_tree", "r_default_water" })
        {
            var type = resources.ResolveDefName(name);
            Assert.True(type.IsValid && type.Type == ResType.RegionType, $"{name} is no REGIONTYPE of the pack");
            land.AddRegionType(type);
        }
        world.AddRegion(land);
        // The sell list's own pace is a wall-clock rule; the bots' clock runs fast here.
        GameClient.AllowBuySellAgent = true;

        var crafting = new CraftingEngine(world);
        crafting.LoadRecipesFromDefs(resources);
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources, nav,
            BotChatBook.FromLines("en", new Dictionary<string, string[]>()), NullLogger.Instance, seed: 9)
        {
            TargetCount = 1000,
            Gravity = 0,
            WorkLookAroundPercent = 0,
            RunCreateScript = c => dispatcher.FireNpcLoadScript(c, restock: true),
        };
        engine.LoadWork(TestRepo.Tracked("playerbots"));
        engine.LoadShopping(TestRepo.Tracked("playerbots"));
        engine.CreateClient = ch =>
        {
            var c = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(lf), NullLogger.Instance);
            c.SetEngines(skillHandlers: new SkillHandlers(world, new GatheringEngine(world, dispatcher)),
                craftingEngine: crafting, triggerDispatcher: dispatcher);
            c.AttachServerDriven(ch);
            return c;
        };
        // What a script tells a character goes to its client, as the server wires it.
        ObjBase.ResolveClientConsole = c => engine.MindOf(c)?.Client;
        return new Live { World = world, Dispatcher = dispatcher, Resources = resources, Engine = engine, Now = Environment.TickCount64 };
    }

    private static void Unload()
    {
        ObjBase.ResolveWorld = null;
        Item.ResolveWorld = null;
        Item.ResolveDefName = null;
        ObjBase.ResolveClientConsole = null;
    }

    private static List<Item> Things(Live live, Character ch) =>
        Enum.GetValues<Layer>().Select(ch.GetEquippedItem).OfType<Item>()
            .Concat(live.World.GetContainerContents(ch.Backpack?.Uid ?? Serial.Invalid))
            .Where(i => !i.IsDeleted).ToList();

    private static Character Made(Live live, string archetype)
    {
        var rid = live.Resources.ResolveDefName(archetype);
        var ch = live.World.CreateCharacter();
        ch.CharDefIndex = rid.Index;
        ch.BaseId = 0x190; ch.BodyId = 0x190;
        ch.Str = 80; ch.Dex = 60; ch.Int = 40;
        live.World.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        live.Dispatcher.FireNpcLoadScript(ch, restock: true);
        return ch;
    }

    /// <summary>The trade a townsfolk kit amounts to, or null when it is none or more than one.</summary>
    private static string? TradeOf(Live live, Character ch)
    {
        var things = Things(live, ch);
        bool Has(ItemType t) => things.Any(i => i.ItemType == t);
        var trades = new List<string>();
        if (ch.GetSkill(SkillType.Mining) >= 300 && ch.GetSkill(SkillType.Blacksmithing) >= 300 &&
            Has(ItemType.WeaponMacePick) && Has(ItemType.WeaponMaceSmith)) trades.Add("miner");
        if (ch.GetSkill(SkillType.Lumberjacking) >= 300 && ch.GetSkill(SkillType.Carpentry) >= 300 &&
            Has(ItemType.WeaponAxe) && Has(ItemType.Carpentry)) trades.Add("lumberjack");
        if (ch.GetSkill(SkillType.Fishing) >= 300 && ch.GetSkill(SkillType.Cooking) >= 300 &&
            Has(ItemType.FishPole) && Has(ItemType.CookingTool) && Has(ItemType.WeaponFence)) trades.Add("fisher");
        if (ch.GetSkill(SkillType.Tailoring) >= 300 && Has(ItemType.SewingKit) && Has(ItemType.Cloth)) trades.Add("tailor");
        int skilled = new[] { SkillType.Mining, SkillType.Lumberjacking, SkillType.Fishing, SkillType.Tailoring }
            .Count(s => ch.GetSkill(s) >= 300);
        return trades.Count == 1 && skilled == 1 ? trades[0] : null;
    }

    /// <summary>Every townsfolk bot has one whole trade: its skills at working level
    /// and its tools, and nothing of another trade. All four trades turn up.</summary>
    [Theory]
    [InlineData("c_playerbot_townsfolk_m")]
    [InlineData("c_playerbot_townsfolk_f")]
    public void TownsfolkComeWithOneTradeAndItsTools(string archetype)
    {
        string? pack = Pack();
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var live = Load(pack!);
        try
        {
            var seen = new Dictionary<string, int>();
            for (int i = 0; i < 60; i++)
            {
                var ch = Made(live, archetype);
                string? trade = TradeOf(live, ch);
                Assert.True(trade != null, $"{archetype} #{i} has no single trade: " +
                    string.Join(", ", Things(live, ch).Select(t => t.ItemType)) +
                    $" mining {ch.GetSkill(SkillType.Mining)} lumber {ch.GetSkill(SkillType.Lumberjacking)} " +
                    $"fishing {ch.GetSkill(SkillType.Fishing)} tailoring {ch.GetSkill(SkillType.Tailoring)}");
                seen[trade!] = seen.GetValueOrDefault(trade!) + 1;
            }
            outp.WriteLine(string.Join(", ", seen.Select(kv => $"{kv.Key} {kv.Value}")));
            Assert.Equal(4, seen.Count);
        }
        finally { Unload(); }
    }

    private static Character Tradesman(Live live, string trade)
    {
        for (int i = 0; i < 200; i++)
        {
            var ch = Made(live, "c_playerbot_townsfolk_m");
            if (TradeOf(live, ch) == trade) return ch;
            live.World.DeleteObject(ch);
        }
        throw new InvalidOperationException($"no {trade} in 200 townsfolk");
    }

    private static Character Adopt(Live live, Character ch, Point3D at)
    {
        ch.IsPlayer = true;
        ch.NpcBrain = NpcBrainType.None;
        ch.MaxHits = ch.Str; ch.Hits = ch.Str; ch.MaxStam = ch.Dex; ch.Stam = ch.Dex;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.RiderTag, "0");
        ch.SetTag(PlayerBotEngine.RoleTag, "-");
        live.World.MoveCharacter(ch, at);
        live.Engine.AdoptSaved();
        return ch;
    }

    private static Item Rock(Live live, int x, int y)
    {
        var rock = live.World.CreateItem();
        rock.BaseId = 0x1363;
        rock.ItemType = ItemType.Rock;
        rock.SetAttr(ObjAttributes.Move_Never);
        live.World.PlaceItem(rock, new Point3D((short)x, (short)y, 0, 0));
        return rock;
    }

    private static int Count(Live live, Character ch, ItemType type) =>
        live.World.GetContainerContents(ch.Backpack!.Uid).Where(i => !i.IsDeleted && i.ItemType == type).Sum(i => (int)i.Amount);

    private static string Trace(Live live, Character ch) =>
        string.Join(" | ", live.Engine.MindOf(ch)!.Trace().Select(t => t.Text));

    /// <summary>A miner made by the archetype digs ore out of the pack's own rock
    /// with the pack's Mining skill (its @PreStart asks for a tool with charges in
    /// the hand, its DELAY and strokes time the swing), smelts it at a forge and
    /// hammers the ingots into something at the same forge.</summary>
    [Fact]
    public void AMinerDigsSmeltsAndSmithsWithThePacksOwnSkills()
    {
        string? pack = Pack();
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var live = Load(pack!);
        try
        {
            // Quick swings: the pack's 1.6 s a stroke would make this a minute's test.
            DefinitionLoader.GetSkillDef((int)SkillType.Mining)!.LoadFromKey("DELAY", "0.1");
            DefinitionLoader.GetSkillDef((int)SkillType.Blacksmithing)!.LoadFromKey("DELAY", "0.1");
            // A rock face: the pack's default rock is iron for the most part, nothing now and then.
            for (int y = 96; y <= 106; y++)
            {
                Rock(live, 113, y);
                Rock(live, 114, y);
            }
            var forge = live.World.CreateItem();
            forge.BaseId = 0x0FB1;
            forge.ItemType = ItemType.Forge;
            forge.SetAttr(ObjAttributes.Move_Never);
            live.World.PlaceItem(forge, new Point3D(100, 105, 0, 0));

            var bot = Adopt(live, Tradesman(live, "miner"), new Point3D(111, 101, 0, 0));
            var mind = live.Engine.MindOf(bot)!;
            outp.WriteLine("kit: " + string.Join(", ", Things(live, bot).Select(t => $"{t.GetName()}[{t.ItemType}]")));
            Assert.True(live.Engine.CanWork(mind));
            live.Engine.WorkMinMs = live.Engine.WorkMaxMs = 3_600_000;
            Assert.True(live.Engine.StartWork(mind, live.Now));
            Assert.Equal(BotJob.Gather, mind.Work!.Job);

            Assert.True(live.Run(30_000, () => Count(live, bot, ItemType.Ore) >= 3), "no ore: " + Trace(live, bot));
            outp.WriteLine($"ore {Count(live, bot, ItemType.Ore)} after digging");

            // With the ingots of a few earlier shifts in its pack there is enough for a
            // piece afterwards (the three ores of this one are no dagger).
            var more = live.World.CreateItem();
            Assert.True(ItemDefHelper.ApplyInstanceMetadata(more, DefinitionLoader.ResolveItemDefIndexByName("i_ingot_iron")));
            more.Amount = 30;
            Assert.True(bot.Backpack!.TryAddItem(more));

            // The shift over, the ore goes to the forge and is smelted; then the craft.
            // (Ore above its Mining stays ore: the default rock holds a little of that too.)
            mind.Work!.UntilMs = 0;
            Assert.True(live.Run(60_000, () => live.Engine.WindowSmelts > 0), "ore not smelted: " + Trace(live, bot));
            Assert.True(live.Run(60_000, () => live.Engine.WindowCrafts > 0), "nothing made: " + Trace(live, bot));
            var made = live.World.GetContainerContents(bot.Backpack!.Uid).Where(i => i.Crafter == bot.Uid).ToList();
            outp.WriteLine($"ingots {Count(live, bot, ItemType.Ingot)}, made {string.Join(", ", made.Select(i => $"{i.GetName()}[{i.ItemType}]"))}");
            Assert.NotEmpty(made);
            // What it made is a piece of the pack's own blacksmith menu - arms and
            // armour - not any definition that carries a SKILLMAKE line.
            Assert.Equal(ResType.SkillMenu, live.Resources.ResolveDefName("sm_blacksmith").Type);
            Assert.All(made, i => Assert.True(
                i.ItemType == ItemType.Shield || i.ItemType.ToString().StartsWith("Weapon") || i.ItemType.ToString().StartsWith("Armor"),
                $"made a {i.ItemType}"));

            // And the town's smith - the pack's own vendor, with its own buy list - pays
            // for its work (a dagger is on every smith's list, whatever else it made).
            var dagger = live.World.CreateItem();
            Assert.True(ItemDefHelper.ApplyInstanceMetadata(dagger, DefinitionLoader.ResolveItemDefIndexByName("i_dagger")));
            dagger.Crafter = bot.Uid;
            Assert.True(bot.Backpack!.TryAddItem(dagger));
            var smith = Made(live, "c_blacksmith");
            live.World.MoveCharacter(smith, new Point3D(104, 105, 0, 0));
            VendorEngine.SetVendorGold(smith, 5000);
            live.Engine.SaleWorthMin = 1;
            long before = VendorEngine.CountGold(bot);
            Assert.True(live.Run(90_000, () => live.Engine.WindowSales > 0), "nothing sold: " + Trace(live, bot));
            Assert.True(VendorEngine.CountGold(bot) > before);
            Assert.NotSame(bot, dagger.IsDeleted ? null : dagger.GetTopLevelObj());
            outp.WriteLine($"sold for {live.Engine.WindowSaleGold}: " + Trace(live, bot));
        }
        finally { Unload(); }
    }

    private static Item Thing(Live live, ItemType type, ushort graphic, int x, int y)
    {
        var item = live.World.CreateItem();
        item.BaseId = graphic;
        item.ItemType = type;
        item.SetAttr(ObjAttributes.Move_Never);
        live.World.PlaceItem(item, new Point3D((short)x, (short)y, 0, 0));
        return item;
    }

    private static void QuickSkills(params SkillType[] skills)
    {
        foreach (var skill in skills)
            DefinitionLoader.GetSkillDef((int)skill)!.LoadFromKey("DELAY", "0.1");
    }

    /// <summary>A lumberjack chops the pack's trees with its hatchet and makes something
    /// of the wood with its saw at the carpenter's.</summary>
    [Fact]
    public void ALumberjackChopsAndTurnsTheWoodIntoSomething()
    {
        string? pack = Pack();
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var live = Load(pack!);
        try
        {
            QuickSkills(SkillType.Lumberjacking, SkillType.Carpentry);
            for (int y = 116; y <= 126; y += 2)
            {
                Thing(live, ItemType.Tree, 0x0CD0, 113, y);
                Thing(live, ItemType.Tree, 0x0CD3, 115, y);
            }
            var bot = Adopt(live, Tradesman(live, "lumberjack"), new Point3D(111, 121, 0, 0));
            var mind = live.Engine.MindOf(bot)!;
            live.Engine.WorkMinMs = live.Engine.WorkMaxMs = 3_600_000;
            Assert.True(live.Engine.StartWork(mind, live.Now));
            Assert.Equal(SkillType.Lumberjacking, mind.Work!.Skill);
            // The kit names a dagger and then the two-handed hatchet: the hatchet is what
            // the hands hold (the pack's Lumberjacking asks WEAPON for its charges).
            Assert.Null(bot.GetEquippedItem(Layer.OneHanded));
            Assert.Equal(ItemType.WeaponAxe, bot.GetEquippedItem(Layer.TwoHanded)?.ItemType);
            Assert.True(live.Run(40_000, () => Count(live, bot, ItemType.Log) >= 6), "few logs: " + Trace(live, bot));
            outp.WriteLine($"logs {Count(live, bot, ItemType.Log)}");

            mind.Work!.UntilMs = 0;
            Assert.True(live.Run(90_000, () => live.Engine.WindowCrafts > 0), "nothing made of the wood: " + Trace(live, bot));
            outp.WriteLine("made: " + string.Join(", ", live.World.GetContainerContents(bot.Backpack!.Uid)
                .Where(i => i.Crafter == bot.Uid).Select(i => $"{i.GetName()}[{i.ItemType}]x{i.Amount}")) + " :: " + Trace(live, bot));
        }
        finally { Unload(); }
    }

    /// <summary>A fisher casts its pole at the water, and at a fire cuts the catch with
    /// its dagger and cooks the steaks in its pan.</summary>
    [Fact]
    public void AFisherCatchesCutsAndCooksItsFish()
    {
        string? pack = Pack();
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var live = Load(pack!);
        try
        {
            QuickSkills(SkillType.Fishing, SkillType.Cooking);
            for (int y = 76; y <= 86; y++)
                for (int x = 113; x <= 116; x++)
                    Thing(live, ItemType.Water, 0x1797, x, y);
            Thing(live, ItemType.Forge, 0x0FB1, 100, 105);
            var bot = Adopt(live, Tradesman(live, "fisher"), new Point3D(111, 81, 0, 0));
            var mind = live.Engine.MindOf(bot)!;
            live.Engine.WorkMinMs = live.Engine.WorkMaxMs = 3_600_000;
            Assert.True(live.Engine.StartWork(mind, live.Now));
            Assert.Equal(SkillType.Fishing, mind.Work!.Skill);
            Assert.True(live.Run(60_000, () => Count(live, bot, ItemType.Fish) >= 2), "no fish: " + Trace(live, bot));
            outp.WriteLine($"fish {Count(live, bot, ItemType.Fish)}");

            mind.Work!.UntilMs = 0;
            Assert.True(live.Run(60_000, () => live.Engine.WindowCrafts > 0), "nothing cooked: " + Trace(live, bot));
            var things = live.World.GetContainerContents(bot.Backpack!.Uid).ToList();
            outp.WriteLine("carries: " + string.Join(", ", things.Select(i => $"{i.GetName()}[{i.ItemType}]x{i.Amount}")) +
                           " :: " + Trace(live, bot));
            // The cut fish is the pack's raw fish steak by definition, which is what the
            // cooked steak's RESOURCES line asks for; the cooked one is the cook's make.
            int cooked = DefinitionLoader.ResolveItemDefIndexByName("i_fish_cut_cooked");
            Assert.Contains(things, i => i.Crafter == bot.Uid && ResourceMatch.ItemDefIndexOf(i) == cooked);
            Assert.DoesNotContain(things, i => i.BaseId == 0x097A &&
                ResourceMatch.ItemDefIndexOf(i) != DefinitionLoader.ResolveItemDefIndexByName("i_fish_cut_raw"));
        }
        finally { Unload(); }
    }

    /// <summary>A tailor sews its cloth into clothes with its kit at the tailor's shop
    /// and the pack's own tailor buys them.</summary>
    [Fact]
    public void ATailorSewsItsClothAndSellsTheClothes()
    {
        string? pack = Pack();
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var live = Load(pack!);
        try
        {
            QuickSkills(SkillType.Tailoring);
            var shopkeeper = Made(live, "c_tailor");
            live.World.MoveCharacter(shopkeeper, new Point3D(90, 84, 0, 0));
            VendorEngine.SetVendorGold(shopkeeper, 5000);
            var bot = Adopt(live, Tradesman(live, "tailor"), new Point3D(90, 81, 0, 0));
            var mind = live.Engine.MindOf(bot)!;
            int cloth = Count(live, bot, ItemType.Cloth);
            Assert.True(cloth >= 20);
            // A plain shirt of its own make from an earlier day: every tailor's shop buys
            // those, whatever its cloth becomes this time.
            var shirt = live.World.CreateItem();
            Assert.True(ItemDefHelper.ApplyInstanceMetadata(shirt, DefinitionLoader.ResolveItemDefIndexByName("i_shirt_plain")));
            shirt.Crafter = bot.Uid;
            Assert.True(bot.Backpack!.TryAddItem(shirt));
            live.Engine.SaleWorthMin = 1;
            Assert.True(live.Engine.StartWork(mind, live.Now));
            Assert.Equal(SkillType.Tailoring, mind.Work!.Skill);
            // (Out of cloth it buys more from the same shop: the least it held is what tells.)
            int least = cloth;
            Assert.True(live.Run(90_000, () =>
                {
                    least = Math.Min(least, Count(live, bot, ItemType.Cloth));
                    return live.Engine.WindowCrafts > 0 && live.Engine.WindowSales > 0;
                }),
                $"crafts {live.Engine.WindowCrafts} sales {live.Engine.WindowSales}: " + Trace(live, bot));
            Assert.True(least < cloth, $"cloth never went down from {cloth}");
            outp.WriteLine($"made {live.Engine.WindowCrafts}, sold for {live.Engine.WindowSaleGold}: " + Trace(live, bot));
            // Its kit stays with it: the shop buys sewing kits and scissors too.
            Assert.Contains(Things(live, bot), i => i.ItemType == ItemType.SewingKit);
            Assert.Contains(Things(live, bot), i => i.ItemType == ItemType.Scissors);
        }
        finally { Unload(); }
    }
}
