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
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What a live run of the bots showed, one test each: a miner whose only "pick" is a
/// shovel buys and takes up a real pickaxe and digs (the pack's Mining reads the tool
/// from the hand); a criminal ghost waits its flag out before it goes among the
/// guards; a missing potion that the bot has another answer for does not keep a
/// fighter from hunting, a need no shop on its road sells is dropped, a choice that
/// would not start is not offered again at once; a heavy purse sends a bot to the bank
/// well below 1500 gold; a blank rune is bought where a vendor sells one, and a shop
/// found without one drops the need; a bot's nerve survives a restart; the way into a
/// stopping spot found at a rescue is kept for every bot.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotLiveFixTests(ITestOutputHelper outp)
{
    // ------------------------------------------------------------------
    //  A synthetic town: a road A (100,100) - B (110,100) - C (120,100) - D (130,100) -
    //  E (200,100), its bank, mage shop, alchemist, healer, inn and mine, a cave mouth at
    //  the end; and an island road F (400,400) with an alchemist of its own.
    // ------------------------------------------------------------------

    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "C" ] },
          { "Name": "C", "X": 120, "Y": 100, "Z": 0, "Connects": [ "D" ] },
          { "Name": "D", "X": 130, "Y": 100, "Z": 0, "Connects": [ "E" ] },
          { "Name": "E", "X": 200, "Y": 100, "Z": 0, "Connects": [ ] },
          { "Name": "F", "X": 400, "Y": 400, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private static string Destinations(bool alchemistOnTheRoad) => $$"""
        { "Destinations": [
          { "Name": "Town Bank", "X": 100, "Y": 96, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 100, "Y": 97, "Z": 0 } ] },
          { "Name": "Town Mage", "X": 110, "Y": 96, "Z": 0, "Type": "VendorMage", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 110, "Y": 97, "Z": 0 } ] },
          { "Name": "Town Alchemist", "X": {{(alchemistOnTheRoad ? 115 : 405)}}, "Y": {{(alchemistOnTheRoad ? 96 : 404)}}, "Z": 0,
            "Type": "VendorAlchemist", "City": "{{(alchemistOnTheRoad ? "Town" : "Isle")}}",
            "NearestWaypoint": "{{(alchemistOnTheRoad ? "B" : "F")}}" },
          { "Name": "Town Healer", "X": 120, "Y": 104, "Z": 0, "Type": "Healer", "City": "Town",
            "NearestWaypoint": "C", "Arrivals": [ { "X": 120, "Y": 103, "Z": 0 } ] },
          { "Name": "Town Inn", "X": 120, "Y": 96, "Z": 0, "Type": "Inn", "City": "Town",
            "NearestWaypoint": "C", "Arrivals": [ { "X": 120, "Y": 97, "Z": 0 } ] },
          { "Name": "Town Mine", "X": 131, "Y": 100, "Z": 0, "Type": "MiningSpot", "City": "Town",
            "NearestWaypoint": "D", "Arrivals": [ { "X": 131, "Y": 101, "Z": 0 } ] },
          { "Name": "Cave Mouth", "X": 201, "Y": 100, "Z": 0, "Type": "DungeonEntrance", "City": "",
            "NearestWaypoint": "E", "Arrivals": [ { "X": 201, "Y": 101, "Z": 0 } ] }
        ] }
        """;

    private const string Definitions = """
        [SKILL 45]
        DEFNAME=Skill_Mining
        KEY=Mining
        FLAGS=skf_gather
        RANGE=2

        [ITEMDEF 0f51]
        DEFNAME=i_dagger
        NAME=dagger
        TYPE=t_weapon_fence
        VALUE=21
        WEIGHT=1

        [ITEMDEF 0e85]
        DEFNAME=i_pickaxe
        NAME=pickaxe
        TYPE=t_weapon_mace_pick
        VALUE=24
        WEIGHT=5

        [ITEMDEF 0f7a]
        DEFNAME=i_reag_black_pearl
        NAME=black pearl
        TYPE=t_reagent
        VALUE=5
        WEIGHT=0.1

        [ITEMDEF 0e21]
        DEFNAME=i_bandage
        NAME=bandage
        TYPE=t_bandage
        VALUE=2
        WEIGHT=0.1

        [ITEMDEF 01f14]
        DEFNAME=i_rune_marker
        NAME=recall rune
        TYPE=t_rune
        VALUE=14
        WEIGHT=1
        """;

    private sealed class Rig
    {
        public required GameWorld World { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public long Now { get; set; } = 1_000_000;

        public bool Run(int ms, Func<bool>? done = null)
        {
            for (int t = 0; t < ms; t += 100)
            {
                if (done?.Invoke() == true) break;
                Engine.Tick(Now);
                Now += 100;
            }
            return done?.Invoke() ?? true;
        }
    }

    private static Rig MakeRig(bool alchemistOnTheRoad = true)
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_botlivefix_{Guid.NewGuid():N}.scp");
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
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;

        var crafting = new CraftingEngine(world);
        crafting.LoadRecipesFromDefs(resources);
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(Destinations(alchemistOnTheRoad), world.MapData);
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources, nav,
            BotChatBook.FromLines("en", new Dictionary<string, string[]>()), NullLogger.Instance, seed: 7)
        {
            TargetCount = 1000,
            Gravity = 0,
            // The murderers' town of this world: an open field east of the road.
            RedRespawnRegion = "300,300,0",
        };
        engine.AddGatherTrade(new BotGatherTrade(SkillType.Mining, ItemType.WeaponMacePick, ["MiningSpot"]));
        engine.AddNeed(new BotNeed("bandage", ItemType.Bandage, 10, 40, ["Healer"]));
        engine.AddNeed(new BotNeed("reagent", ItemType.Reagent, 15, 30, ["VendorMage"]));
        engine.AddNeed(new BotNeed("rune", ItemType.Rune, 1, 2, ["VendorMage"]));
        engine.AddNeed(new BotNeed("heal", ItemType.Potion, 1, 3, ["VendorAlchemist"], SpellType.GreaterHeal));
        engine.AddNeed(new BotNeed("cure", ItemType.Potion, 1, 2, ["VendorAlchemist"], SpellType.Cure));
        engine.AddNeed(new BotNeed("refresh", ItemType.Potion, 1, 2, ["VendorAlchemist"], SpellType.Refresh));
        engine.CreateClient = ch =>
        {
            var c = new GameClient(new SphereNet.Network.State.NetState(NullLogger.Instance),
                world, new SphereNet.Game.Accounts.AccountManager(lf), NullLogger.Instance);
            c.SetEngines(skillHandlers: new SkillHandlers(world, new GatheringEngine(world)), craftingEngine: crafting,
                triggerDispatcher: new TriggerDispatcher());
            c.AttachServerDriven(ch);
            return c;
        };
        return new Rig { World = world, Engine = engine };
    }

    private static Character Bot(Rig rig, Point3D at, Action<Character>? setup = null, string trait = "kind", string role = "-")
    {
        var ch = rig.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Bot" + ch.Uid.Value;
        ch.Str = 80; ch.Dex = 80; ch.Int = 60;
        ch.MaxStam = 80; ch.Stam = 80; ch.MaxHits = 80; ch.Hits = 80; ch.MaxMana = 60; ch.Mana = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.RiderTag, "0");
        ch.SetTag(PlayerBotEngine.RoleTag, role);
        ch.SetTag(PlayerBotEngine.TraitTag, trait);
        var pack = rig.World.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        setup?.Invoke(ch);
        rig.World.PlaceCharacter(ch, at);
        rig.Engine.AdoptSaved();
        var mind = rig.Engine.MindOf(ch)!;
        mind.LingerUntilMs = long.MaxValue;
        mind.Nerve = 1.2;
        return ch;
    }

    private static Character Warrior(Rig rig, Point3D at) => Bot(rig, at, ch =>
    {
        ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_warrior_m");
        ch.SetSkill(SkillType.Swordsmanship, 900);
        ch.SetSkill(SkillType.Healing, 800);
        ch.SetSkill(SkillType.Anatomy, 700);
    });

    private static Character Mage(Rig rig, Point3D at) => Bot(rig, at, ch =>
    {
        ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_mage_m");
        ch.SetSkill(SkillType.Magery, 800);
    });

    private static Item Give(Rig rig, Character ch, string defName, int amount = 1, Layer hand = Layer.None)
    {
        var item = rig.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, DefinitionLoader.ResolveItemDefIndexByName(defName)));
        item.Amount = (ushort)amount;
        if (hand != Layer.None) Assert.True(ch.Equip(item, hand));
        else Assert.True(ch.Backpack!.TryAddItem(item));
        return item;
    }

    private static void Gold(Rig rig, Item into, int amount)
    {
        var g = rig.World.CreateItem();
        g.BaseId = 0x0EED;
        g.ItemType = ItemType.Gold;
        g.Amount = (ushort)amount;
        Assert.True(into.TryAddItem(g));
    }

    private static string Why(BotMind mind) => string.Join(" | ", mind.Trace().Select(t => t.Text)) + " || " + mind.Plan.LastChoice;

    private static double Utility(List<PlayerBotEngine.BotOption> options, BotActivity a) =>
        options.FirstOrDefault(o => o.Activity == a)?.Utility ?? double.NaN;

    // ------------------------------------------------------------------
    //  1. A tool the trade can be plied with
    // ------------------------------------------------------------------

    private const string LiveWaypointsJson = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string LiveDestinationsJson = """
        { "Destinations": [
          { "Name": "Town Smith", "X": 104, "Y": 104, "Z": 0, "Type": "VendorSmith", "City": "Town",
            "NearestWaypoint": "A", "Arrivals": [ { "X": 104, "Y": 102, "Z": 0 } ] },
          { "Name": "Town Mine", "X": 111, "Y": 100, "Z": 0, "Type": "MiningSpot", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 111, "Y": 101, "Z": 0 } ] },
          { "Name": "North Mine", "X": 111, "Y": 92, "Z": 0, "Type": "MiningSpot", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 111, "Y": 93, "Z": 0 } ] }
        ] }
        """;

    private sealed class Live
    {
        public required GameWorld World { get; init; }
        public required TriggerDispatcher Dispatcher { get; init; }
        public required ResourceHolder Resources { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public long Now { get; set; }

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

    /// <summary>The Scripts-X pack with the bots' own scripts, a synthetic map whose
    /// tiledata says which hand a pick, a hammer and a dagger go in - and that a shovel
    /// (0x0F39) goes in none - and the pack's own rock, as PlayerBotWorkLiveTests loads it.</summary>
    private static Live LoadLive(string pack)
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
        foreach (var (tool, hand) in new (ushort, Layer)[]
                 {
                     (0x0E85, Layer.OneHanded), (0x0E86, Layer.OneHanded), (0x13E3, Layer.OneHanded), (0x0F51, Layer.OneHanded),
                 })
            map.SetSyntheticItemTile(tool, new SphereNet.MapData.Tiles.ItemTileData
            {
                Flags = SphereNet.MapData.Tiles.TileFlag.Wearable | SphereNet.MapData.Tiles.TileFlag.Weapon,
                Quality = (byte)hand,
                Weight = 5,
                Name = "tool",
            });
        // A shovel is no weapon and no wearable: no hand takes it.
        map.SetSyntheticItemTile(0x0F39, new SphereNet.MapData.Tiles.ItemTileData { Weight = 6, Name = "shovel" });
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
        Item.CreateTriggerHook = item =>
            dispatcher.FireItemTrigger(item, ItemTrigger.Create, new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item });
        Item.CreateTriggerSourceHook = (item, src) =>
            dispatcher.FireItemTrigger(item, ItemTrigger.Create, new SphereNet.Game.Scripting.TriggerArgs { ItemSrc = item, CharSrc = src });
        VendorEngine.World = world;
        var land = new SphereNet.Game.World.Regions.Region { Name = "test land", MapIndex = 0 };
        land.AddRect(0, 0, 511, 511);
        foreach (string name in new[] { "r_default_rock", "r_default_tree", "r_default_water" })
            land.AddRegionType(resources.ResolveDefName(name));
        world.AddRegion(land);

        var crafting = new CraftingEngine(world);
        crafting.LoadRecipesFromDefs(resources);
        var nav = new BotNavGraph();
        nav.LoadWaypoints(LiveWaypointsJson, world.MapData);
        nav.LoadDestinations(LiveDestinationsJson, world.MapData);
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
        ObjBase.ResolveClientConsole = c => engine.MindOf(c)?.Client;
        return new Live { World = world, Dispatcher = dispatcher, Resources = resources, Engine = engine, Now = Environment.TickCount64 };
    }

    private static void UnloadLive()
    {
        ObjBase.ResolveWorld = null;
        Item.ResolveWorld = null;
        Item.ResolveDefName = null;
        ObjBase.ResolveClientConsole = null;
    }

    private static Item Made(Live live, string defName)
    {
        var item = live.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(item, DefinitionLoader.ResolveItemDefIndexByName(defName)));
        // Its own @Create (charges: UsesMax), as a vendor's restock or a kit runs it.
        Item.CreateTriggerHook?.Invoke(item);
        return item;
    }

    private static List<Item> Carried(Live live, Character ch) =>
        Enum.GetValues<Layer>().Select(ch.GetEquippedItem).OfType<Item>()
            .Concat(live.World.GetContainerContents(ch.Backpack?.Uid ?? Serial.Invalid))
            .Where(i => !i.IsDeleted).ToList();

    /// <summary>A miner of the townsfolk archetype, its picks and blades taken away,
    /// something other than a pick in its hand and a shovel in its pack - the state a
    /// live miner was found in.</summary>
    private static Character MinerWithAShovel(Live live, out Item shovel)
    {
        for (int i = 0; i < 200; i++)
        {
            var ch = live.World.CreateCharacter();
            ch.CharDefIndex = live.Resources.ResolveDefName("c_playerbot_townsfolk_m").Index;
            ch.BaseId = 0x190; ch.BodyId = 0x190;
            ch.Str = 80; ch.Dex = 60; ch.Int = 40;
            live.World.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            live.Dispatcher.FireNpcLoadScript(ch, restock: true);
            if (ch.GetSkill(SkillType.Mining) < 300 || ch.GetSkill(SkillType.Lumberjacking) >= 300 ||
                ch.GetSkill(SkillType.Fishing) >= 300 || ch.GetSkill(SkillType.Tailoring) >= 300)
            {
                live.World.DeleteObject(ch);
                continue;
            }
            foreach (var pick in Carried(live, ch).Where(t => t.ItemType == ItemType.WeaponMacePick || t.ItemType == ItemType.WeaponFence))
                live.World.DeleteObject(pick);
            ch.IsPlayer = true;
            ch.NpcBrain = NpcBrainType.None;
            ch.MaxHits = ch.Str; ch.Hits = ch.Str; ch.MaxStam = ch.Dex; ch.Stam = ch.Dex;
            ch.SetTag(PlayerBotEngine.MarkerTag, "1");
            ch.SetTag(PlayerBotEngine.HomeTag, "Town");
            ch.SetTag(PlayerBotEngine.RiderTag, "0");
            ch.SetTag(PlayerBotEngine.RoleTag, "-");
            // A dagger in the hand when nothing else is (the hammer of its kit stays where it is).
            if (ch.GetEquippedItem(Layer.OneHanded) == null && ch.GetEquippedItem(Layer.TwoHanded) == null)
                Assert.True(ch.Equip(Made(live, "i_dagger"), Layer.OneHanded));
            shovel = Made(live, "i_shovel");
            Assert.True(ch.Backpack!.TryAddItem(shovel));
            live.World.MoveCharacter(ch, new Point3D(111, 101, 0, 0));
            live.Engine.AdoptSaved();
            return ch;
        }
        throw new InvalidOperationException("no miner in 200 townsfolk");
    }

    /// <summary>The pack's Mining checks <c>SRC.WEAPON.USESCUR</c> in @PreStart: a shovel
    /// (a <c>t_weapon_mace_pick</c> no hand holds) leaves the dagger in hand and mining
    /// never starts. A miner with only a shovel has no work and still needs a pickaxe; a
    /// vendor's shovel is not what it buys; with a real pickaxe in its pack it takes it
    /// into the hand and digs. A tool it cannot take up (too weak for it) fails twice and
    /// the trade is left alone. And the engine: a double click that cannot equip offers
    /// no target cursor, and the cursor's prompt names the item.</summary>
    [Fact]
    public void AMinerWithOnlyAShovelBuysAPickaxeTakesItUpAndDigs()
    {
        string? pack = TestRepo.Optional("oldSphere/Scripts-X-main");
        if (Gate.Missing(outp, "external script pack", pack == null)) return;
        var live = LoadLive(pack!);
        try
        {
            DefinitionLoader.GetSkillDef((int)SkillType.Mining)!.LoadFromKey("DELAY", "0.1");
            var bot = MinerWithAShovel(live, out var shovel);
            var mind = live.Engine.MindOf(bot)!;
            Assert.Equal(ItemType.WeaponMacePick, shovel.ItemType);

            // The shovel is no tool for the pack's Mining: no work, and a pickaxe is the need.
            Assert.False(live.Engine.CanWork(mind));
            Assert.Equal("pickaxe", live.Engine.FirstNeed(mind)?.Name);
            Assert.Equal(0, live.Engine.NeedHave(mind, live.Engine.FirstNeed(mind)!));

            // A vendor with shovels only is no seller of what it needs; one with pickaxes is.
            var smith = live.World.CreateCharacter();
            smith.NpcBrain = NpcBrainType.Vendor;
            live.World.PlaceCharacter(smith, new Point3D(104, 104, 0, 0));
            var stock = live.World.CreateItem();
            stock.ItemType = ItemType.Container;
            smith.Equip(stock, Layer.VendorStock);
            var shovels = Made(live, "i_shovel");
            shovels.Amount = 5;
            stock.AddItem(shovels);
            var need = live.Engine.FirstNeed(mind)!;
            mind.ShoppingFor = need;
            mind.BeginLinger(live.Engine.Nav.FindDestination("Town Smith"), live.Now, new Random(1), short_: true);
            Assert.False(live.Engine.StartShopping(mind, live.Now));
            var picks = Made(live, "i_pickaxe");
            picks.Amount = 5;
            stock.AddItem(picks);
            mind.ShoppingFor = need;
            Assert.True(live.Engine.StartShopping(mind, live.Now));
            Assert.Same(smith, mind.ShopVendor);
            mind.ShopVendor = null;
            mind.ShoppingFor = null;
            mind.Mode = BotMode.Linger;

            // The engine: the shovel's cursor names it (pItem->GetName(), not an empty NAME).
            mind.Client!.HandleDoubleClick(shovel.Uid.Value);
            Assert.True(mind.Client.HasPendingTarget);
            Assert.Contains(mind.Trace(), t => t.Text.Contains("use the shovel", StringComparison.OrdinalIgnoreCase));
            mind.Client.HandleTargetResponse(0, mind.Client.ActiveTargetCursorId, 0, -1, -1, 0, 0);

            // A real pickaxe in the pack (as bought): taken into the hand, and it digs the
            // pack's own rock.
            for (int y = 96; y <= 106; y++)
                foreach (int x in new[] { 113, 114 })
                {
                    var rock = live.World.CreateItem();
                    rock.BaseId = 0x1363;
                    rock.ItemType = ItemType.Rock;
                    rock.SetAttr(ObjAttributes.Move_Never);
                    live.World.PlaceItem(rock, new Point3D((short)x, (short)y, 0, 0));
                }
            var pick = Made(live, "i_pickaxe");
            Assert.True(bot.Backpack!.TryAddItem(pick));
            Assert.Null(live.Engine.FirstNeed(mind));
            Assert.True(live.Engine.CanWork(mind));
            live.Engine.WorkMinMs = live.Engine.WorkMaxMs = 3_600_000;
            Assert.True(live.Engine.StartWork(mind, live.Now));
            Assert.True(live.Run(30_000, () => live.World.GetContainerContents(bot.Backpack.Uid).Any(i => i.ItemType == ItemType.Ore)),
                "no ore: " + string.Join(" | ", mind.Trace().Select(t => t.Text)));
            Assert.Same(pick, bot.GetEquippedItem(Layer.OneHanded));
            Assert.DoesNotContain(mind.Trace(), t => t.Text.Contains("out of charges"));
            outp.WriteLine(string.Join(" | ", mind.Trace().Select(t => t.Text)));

            // Too weak for it (the pickaxe's ReqStr): the double click equips nothing and
            // offers no cursor (Cmd_Use_Item returns, CClientUse.cpp:131) ...
            mind.Work!.UntilMs = 0;
            live.Run(5_000, () => mind.Work == null);
            Assert.True(bot.Backpack.TryAddItem(pick));
            bot.Str = 10;
            live.Engine.WorkLoadPercent = 10_000; // a weak back is not what is tried here
            mind.Client.HandleDoubleClick(pick.Uid.Value);
            Assert.False(pick.IsEquipped);
            Assert.False(mind.Client.HasPendingTarget);
            // ... and a trade whose tool will not be taken up twice in a row (here, then at
            // the other mine) is left alone: no more work offered for it.
            for (int i = 0; i < 2; i++)
            {
                Assert.True(live.Engine.StartWork(mind, live.Now), $"try {i}: " + string.Join(" | ", mind.Trace().Select(t => t.Text)));
                Assert.True(live.Run(20_000, () => mind.Work == null));
            }
            Assert.Contains(mind.Trace(), t => t.Text.Contains("will not start; leaves it for a while"));
            // (Its ore would still be smelted: that is another job.)
            foreach (var ore in live.World.GetContainerContents(bot.Backpack.Uid).Where(i => i.ItemType == ItemType.Ore).ToList())
                live.World.DeleteObject(ore);
            Assert.False(live.Engine.CanWork(mind));
        }
        finally { UnloadLive(); }
    }

    // ------------------------------------------------------------------
    //  2. A criminal ghost
    // ------------------------------------------------------------------

    /// <summary>A criminal ghost does not walk into the guards' town to be raised: it
    /// waits about its body until the flag runs out, then goes to the healer's. One whose
    /// flag never runs out is raised in the murderers' town, not at a guarded healer.</summary>
    [Fact]
    public void ACriminalGhostWaitsOutItsFlagBeforeItIsRaisedAmongTheGuards()
    {
        var rig = MakeRig();
        var bot = Warrior(rig, new Point3D(140, 100, 0, 0));
        var mind = rig.Engine.MindOf(bot)!;
        mind.LingerUntilMs = 0;
        bot.MakeCriminal();
        Assert.True(bot.IsFlaggedAsCriminal);
        bot.Hits = 0;
        bot.SetStatFlag(StatFlag.Dead);

        // Two minutes on: still a ghost, about its body, not at the healer.
        rig.Run(120_000);
        Assert.True(bot.IsDead, Why(mind));
        Assert.True(bot.Position.GetDistanceTo(new Point3D(140, 100, 0, 0)) <= 8, $"wandered to {bot.Position}");
        Assert.Contains(mind.Trace(), t => t.Text.Contains("criminal ghost"));

        // The flag runs out: now it goes to be raised at the healer's.
        Assert.True(bot.TrySetProperty("CRIMINAL", "0"));
        Assert.False(bot.IsFlaggedAsCriminal);
        Assert.True(rig.Run(400_000, () => !bot.IsDead), Why(mind));
        Assert.True(bot.Position.GetDistanceTo(new Point3D(120, 103, 0, 0)) <= 3, $"raised at {bot.Position}");

        // A flag that never runs out: after the longest wait, the murderers' town.
        var other = Warrior(rig, new Point3D(150, 100, 0, 0));
        var om = rig.Engine.MindOf(other)!;
        om.LingerUntilMs = 0;
        other.MakeCriminal();
        other.Hits = 0;
        other.SetStatFlag(StatFlag.Dead);
        Assert.True(rig.Run(900_000, () => !other.IsDead), Why(om));
        Assert.True(other.Position.GetDistanceTo(new Point3D(300, 300, 0, 0)) <= 3, $"raised at {other.Position}");
    }

    // ------------------------------------------------------------------
    //  3. What a missing thing is worth to the plan
    // ------------------------------------------------------------------

    /// <summary>A warrior with bandages and Healing that has no potions is short of a
    /// heal potion, but that hardly matters to it: it goes hunting rather than shopping.
    /// A need no shop on its road sells is no need at all. A choice that would not start
    /// is not offered again at once. Company that presses does not throw a plan over.</summary>
    [Fact]
    public void AMissingPotionDoesNotKeepAWellKittedWarriorFromHunting()
    {
        var rig = MakeRig();
        var bot = Warrior(rig, new Point3D(100, 100, 0, 0));
        Give(rig, bot, "i_dagger", hand: Layer.OneHanded);
        Give(rig, bot, "i_bandage", 30);
        Gold(rig, bot.Backpack!, 300);
        var mind = rig.Engine.MindOf(bot)!;
        var plan = rig.Engine.PlanOf(mind, rig.Now);
        plan.Town = "Town";
        plan.TownSinceMs = rig.Now - 2_400_000; // long enough in town to want out

        var s = rig.Engine.Situation(mind, rig.Now);
        Assert.Equal("heal", s.Need?.Name);
        Assert.True(s.Drives[(int)BotDrive.Supplies] <= 0.35, $"supplies {s.Drives[(int)BotDrive.Supplies]}");
        var options = rig.Engine.Options(mind, s, rig.Now);
        Assert.True(Utility(options, BotActivity.Hunt) > Utility(options, BotActivity.Shop), Why(mind));
        var goal = rig.Engine.PlanNext(mind, rig.Now, out _);
        Assert.Equal(BotActivity.Hunt, mind.Plan.Steps[0].Activity);
        Assert.Equal("Cave Mouth", goal?.Name);

        // Without bandages a heal potion matters, and buying it comes first.
        var lone = Bot(rig, new Point3D(100, 100, 0, 0), ch =>
        {
            ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_warrior_m");
            ch.SetSkill(SkillType.Swordsmanship, 900);
        });
        Gold(rig, lone.Backpack!, 300);
        var lm = rig.Engine.MindOf(lone)!;
        Assert.True(rig.Engine.Situation(lm, rig.Now).Drives[(int)BotDrive.Supplies] >= 0.7);

        // No alchemist on its road: no need of potions, nothing held back.
        var island = MakeRig(alchemistOnTheRoad: false);
        var far = Warrior(island, new Point3D(100, 100, 0, 0));
        Give(island, far, "i_bandage", 30);
        Gold(island, far.Backpack!, 300);
        var fm = island.Engine.MindOf(far)!;
        Assert.Equal("heal", island.Engine.FirstNeed(fm)?.Name);
        var fs = island.Engine.Situation(fm, island.Now);
        Assert.Null(fs.Need);
        Assert.Equal(0, fs.Drives[(int)BotDrive.Supplies]);
        Assert.DoesNotContain(island.Engine.Options(fm, fs, island.Now), o => o.Activity == BotActivity.Shop);

        // A miner whose mine is out of its reach: work is its best choice and will not
        // start; it is not offered again at the next stay.
        var miner = Bot(island, new Point3D(100, 100, 0, 0), ch => ch.SetSkill(SkillType.Mining, 800));
        Give(island, miner, "i_pickaxe");
        island.Engine.WorkTripRange = 5;
        var mm = island.Engine.MindOf(miner)!;
        Assert.Contains(island.Engine.Options(mm, island.Engine.Situation(mm, island.Now), island.Now), o => o.Activity == BotActivity.Work);
        island.Engine.PlanNext(mm, island.Now, out _);
        Assert.True(island.Engine.WindowPlanFails >= 1, Why(mm));
        Assert.DoesNotContain(island.Engine.Options(mm, island.Engine.Situation(mm, island.Now), island.Now),
            o => o.Activity == BotActivity.Work);
        long fails = island.Engine.WindowPlanFails;
        island.Engine.PlanNext(mm, island.Now + 1000, out _);
        Assert.Equal(fails, island.Engine.WindowPlanFails);

        // A chatty bot long alone: company presses, but the plan it made is not thrown
        // over for it (only a need, wounds, a full pack or a heavy purse do that).
        var chatty = Bot(rig, new Point3D(100, 100, 0, 0), trait: "chatty");
        var cm = rig.Engine.MindOf(chatty)!;
        var cp = rig.Engine.PlanOf(cm, rig.Now);
        cp.LastSocialMs = rig.Now - 2_000_000;
        cp.Clear();
        cp.Steps.Add(new BotPlanStep(BotActivity.Wander));
        cp.Steps.Add(new BotPlanStep(BotActivity.Rest));
        cp.Step = 0;
        cp.MadeMinute = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
        long urgent = rig.Engine.WindowPlansUrgent;
        rig.Engine.PlanNext(cm, rig.Now, out _);
        Assert.Equal(urgent, rig.Engine.WindowPlansUrgent);
    }

    // ------------------------------------------------------------------
    //  4. The purse
    // ------------------------------------------------------------------

    /// <summary>A thief carrying 1498 gold feels it: the bank is its first errand. A
    /// bot carrying what a withdrawal leaves it does not.</summary>
    [Fact]
    public void AHeavyPurseSendsABotToTheBankWellBelow1500()
    {
        var rig = MakeRig();
        var thief = Bot(rig, new Point3D(110, 100, 0, 0), ch =>
        {
            ch.SetTag(PlayerBotEngine.ArchetypeTag, "c_playerbot_archer_m");
            ch.SetSkill(SkillType.Stealing, 700);
        }, trait: "stingy", role: "thief");
        Gold(rig, thief.Backpack!, 1498);
        var mind = rig.Engine.MindOf(thief)!;
        var s = rig.Engine.Situation(mind, rig.Now);
        Assert.True(s.Drives[(int)BotDrive.Purse] >= 0.95, $"purse {s.Drives[(int)BotDrive.Purse]}");
        var goal = rig.Engine.PlanNext(mind, rig.Now, out _);
        Assert.Equal(BotActivity.Bank, mind.Plan.Steps[0].Activity);
        Assert.Equal("Town Bank", goal?.Name);
        Assert.Equal(BotDrive.Purse, mind.Plan.Why);
        // The errand puts the gold in once there (what it carries is over its mark).
        Assert.True(1498 > rig.Engine.BankDepositAbove);

        // A thousand already weighs on it.
        var mid = Bot(rig, new Point3D(110, 100, 0, 0));
        Gold(rig, mid.Backpack!, 1000);
        Assert.True(rig.Engine.Situation(rig.Engine.MindOf(mid)!, rig.Now).Drives[(int)BotDrive.Purse] >= 0.5);

        // What a withdrawal brings the purse up to is not felt (no back and forth).
        var light = Bot(rig, new Point3D(110, 100, 0, 0));
        Gold(rig, light.Backpack!, rig.Engine.BankWithdrawTo);
        Assert.Equal(0, rig.Engine.Situation(rig.Engine.MindOf(light)!, rig.Now).Drives[(int)BotDrive.Purse]);
    }

    // ------------------------------------------------------------------
    //  5. Runes
    // ------------------------------------------------------------------

    /// <summary>A caster short of a blank rune buys one where a vendor sells it; a shop
    /// found with nobody selling one is not walked to again, and the need goes with it
    /// instead of standing for good. A blank rune never holds a hunt back.</summary>
    [Fact]
    public void ARuneIsBoughtWhereSoldAndTheNeedIsDroppedWhereNot()
    {
        Character.SpellbookRequiredEnabled = false; // Mark and Recall "in its book" (reset by ResetEngineStatics)
        GameClient.AllowBuySellAgent = true;         // the buy list's pace is a wall-clock rule; this clock runs fast
        var rig = MakeRig();
        var bot = Mage(rig, new Point3D(110, 97, 0, 0));
        Give(rig, bot, "i_reag_black_pearl", 30);
        Gold(rig, bot.Backpack!, 300);
        var mind = rig.Engine.MindOf(bot)!;
        var shop = rig.Engine.Nav.FindDestination("Town Mage")!;

        var s = rig.Engine.Situation(mind, rig.Now);
        Assert.Equal("rune", s.Need?.Name);
        Assert.False(s.FightNeed);
        var options = rig.Engine.Options(mind, s, rig.Now);
        Assert.Contains(options, o => o.Activity == BotActivity.Shop && o.Target == shop);

        // A vendor with blank runes in stock: one is bought.
        var vendor = rig.World.CreateCharacter();
        vendor.Name = "mage";
        vendor.NpcBrain = NpcBrainType.Vendor;
        rig.World.PlaceCharacter(vendor, new Point3D(111, 96, 0, 0));
        VendorEngine.SetVendorGold(vendor, 5000);
        var stock = rig.World.CreateItem();
        stock.ItemType = ItemType.Container;
        vendor.Equip(stock, Layer.VendorStock);
        var runes = rig.World.CreateItem();
        Assert.True(ItemDefHelper.ApplyInstanceMetadata(runes, DefinitionLoader.ResolveItemDefIndexByName("i_rune_marker")));
        runes.Amount = 4;
        stock.AddItem(runes);
        mind.ShoppingFor = s.Need;
        mind.BeginLinger(shop, rig.Now, new Random(1), short_: true);
        Assert.True(rig.Engine.StartShopping(mind, rig.Now));
        var need = s.Need!;
        Assert.True(rig.Run(30_000, () => rig.Engine.NeedHave(mind, need) > 0), Why(mind));
        Assert.Contains(mind.Trace(), t => t.Text.Contains("bought rune"));

        // Another caster at the same shop once the vendor is gone: nobody sells one.
        rig.World.DeleteObject(vendor);
        var other = Mage(rig, new Point3D(110, 97, 0, 0));
        Give(rig, other, "i_reag_black_pearl", 30);
        Gold(rig, other.Backpack!, 300);
        var om = rig.Engine.MindOf(other)!;
        var os = rig.Engine.Situation(om, rig.Now);
        Assert.Equal("rune", os.Need?.Name);
        double huntWithNeed = Utility(rig.Engine.Options(om, os, rig.Now), BotActivity.Hunt);
        om.ShoppingFor = os.Need;
        om.BeginLinger(shop, rig.Now, new Random(1), short_: true);
        Assert.False(rig.Engine.StartShopping(om, rig.Now));
        var after = rig.Engine.Situation(om, rig.Now);
        Assert.Null(after.Need);
        Assert.Equal(0, after.Drives[(int)BotDrive.Supplies]);
        var later = rig.Engine.Options(om, after, rig.Now);
        Assert.DoesNotContain(later, o => o.Activity == BotActivity.Shop);
        // The rune need held the hunt back no more before it was dropped (only the gold
        // it would cost counted, toward the money a hunt brings).
        Assert.True(huntWithNeed >= Utility(later, BotActivity.Hunt), $"{huntWithNeed} < {Utility(later, BotActivity.Hunt)}");
    }

    // ------------------------------------------------------------------
    //  6. Nerve
    // ------------------------------------------------------------------

    /// <summary>A bot's nerve and the hit points it runs at are rolled once and kept
    /// (PLAYERBOT.NERVE): the same after a restart. A bot from an older save without the
    /// tag rolls once and writes it down.</summary>
    [Fact]
    public void ABotsNerveSurvivesARestart()
    {
        var rig = MakeRig();
        var bot = Bot(rig, new Point3D(100, 100, 0, 0));
        // Bot() puts a fixed nerve on the mind for the planner tests; the first adopt's roll is in the tag.
        Assert.True(bot.TryGetTag(PlayerBotEngine.NerveTag, out string? tag));
        var p = tag!.Split('|');
        double nerve = double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture);
        int fleeAt = int.Parse(p[1]);
        Assert.InRange(nerve, 0.8, 2.0);
        Assert.InRange(fleeAt, 20, 39);

        for (int restart = 0; restart < 3; restart++)
        {
            rig.Engine.Release(bot);
            Assert.Equal(1, rig.Engine.AdoptSaved());
            var again = rig.Engine.MindOf(bot)!;
            Assert.Equal(nerve, again.Nerve);
            Assert.Equal(fleeAt, again.FleeAt);
        }

        // An old save: no tag. Rolled once, written, and kept from then on.
        rig.Engine.Release(bot);
        bot.RemoveTag(PlayerBotEngine.NerveTag);
        rig.Engine.AdoptSaved();
        var rolled = rig.Engine.MindOf(bot)!;
        Assert.True(bot.TryGetTag(PlayerBotEngine.NerveTag, out string? written));
        rig.Engine.Release(bot);
        rig.Engine.AdoptSaved();
        Assert.Equal(rolled.Nerve, rig.Engine.MindOf(bot)!.Nerve);
        Assert.Equal(rolled.FleeAt, rig.Engine.MindOf(bot)!.FleeAt);
        Assert.Equal(written, bot.TryGetTag(PlayerBotEngine.NerveTag, out string? still) ? still : null);
    }

    // ------------------------------------------------------------------
    //  7. The way into a stopping spot
    // ------------------------------------------------------------------

    private const ushort WallTile = 0x0080;

    /// <summary>A shop walled about with its door round the far side, its stopping spot
    /// inside. The way in that a rescue's search finds is kept, and bots whose own
    /// searches give out follow it from the road node instead of stalling there; it is
    /// written with the kept ways and read back.</summary>
    [Fact]
    public void TheWayIntoAStoppingSpotFoundAtARescueIsKeptForEveryBot()
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        map.SetSyntheticItemTile(WallTile, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Impassable | SphereNet.MapData.Tiles.TileFlag.Wall,
            Height = 20,
        });
        TestHarness.SeedItemDefs(WallTile);
        // Walls x 111..125, y 104..116, the doorway at the east end of the bottom wall.
        void Wall(int x0, int y0, int x1, int y1)
        {
            for (int x = x0; x <= x1; x++)
                for (int y = y0; y <= y1; y++)
                    map.AddSyntheticStatic(0, x, y, WallTile, 0);
        }
        Wall(111, 104, 125, 104);
        Wall(111, 116, 123, 116);
        Wall(111, 104, 111, 116);
        Wall(125, 104, 125, 116);
        var nav = new BotNavGraph();
        nav.LoadWaypoints("""
            { "Waypoints": [
              { "Name": "W", "X": 100, "Y": 100, "Z": 0, "Connects": [ "E" ] },
              { "Name": "E", "X": 112, "Y": 100, "Z": 0 }
            ] }
            """, world.MapData);
        nav.LoadDestinations("""
            { "Destinations": [
              { "Name": "Walled Shop", "X": 116, "Y": 110, "Z": 0, "Type": "VendorSmith", "City": "Town", "NearestWaypoint": "E",
                "Arrivals": [ { "X": 116, "Y": 110, "Z": 0, "Waypoints": [ "E" ] } ] }
            ] }
            """, world.MapData);
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLines("en", new Dictionary<string, string[]>()), NullLogger.Instance, seed: 7)
        {
            TargetCount = 50,
            // The bots' own searches give out at once: what gets them in is the kept way.
            SearchMs = 0,
        };
        Character Walker(Point3D at, string? goal)
        {
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Name = "Walker";
            ch.Str = 60; ch.Dex = 60; ch.Int = 40;
            ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 60; ch.Hits = 60;
            ch.SetTag(PlayerBotEngine.MarkerTag, "1");
            ch.SetTag(PlayerBotEngine.HomeTag, "Town");
            if (goal != null) ch.SetTag(PlayerBotEngine.GoalTag, goal);
            world.PlaceCharacter(ch, at);
            return ch;
        }

        var shop = nav.Destinations[0];
        var first = Walker(new Point3D(112, 101, 0, 0), null);
        engine.AdoptSaved();
        int e = nav.FindWaypoint("E");
        // A rescue on the last leg into the spot: the search of its own finds the way round.
        Assert.True(engine.LearnArrivalWay(shop, 0, [nav.FindWaypoint("W"), e], shop.Arrivals[0], first));
        Assert.Equal(1, engine.WindowArrivalWays);
        Assert.Equal(1, engine.CorridorCount);
        // Not looked for twice.
        Assert.False(engine.LearnArrivalWay(shop, 0, [e], shop.Arrivals[0], first));

        // Another bot sent there follows it from the node and gets in without a lift.
        var bot = Walker(new Point3D(100, 100, 0, 0), "Walled Shop");
        engine.AdoptSaved();
        var mind = engine.MindOf(bot)!;
        long now = 1_000_000;
        for (int i = 0; i < 1200 && !(mind.At == shop && mind.Mode == BotMode.Linger); i++)
        {
            engine.RunSearches(null);
            engine.Tick(now);
            now += 100;
        }
        string trace = string.Join(" | ", mind.Trace().Select(t => t.Text));
        Assert.True(mind.At == shop, trace);
        Assert.True(bot.Position.GetDistanceTo(shop.Arrivals[0]) <= 3, $"at {bot.Position}: {trace}");
        Assert.Contains("follows the kept way E -> Walled Shop spot 0", trace);
        Assert.Equal(0, engine.StuckRescues);

        // Written with the kept ways, and read back.
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_corridors_{Guid.NewGuid():N}.txt");
        try
        {
            engine.CorridorFile = file;
            engine.SaveCorridorsIfDue(now, force: true);
            Assert.Contains("spot|E|Walled Shop|0|", File.ReadAllText(file));
            var again = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
                BotChatBook.FromLines("en", new Dictionary<string, string[]>()), NullLogger.Instance, seed: 7)
            { CorridorFile = file };
            Assert.Equal(1, again.LoadCorridors());
        }
        finally { File.Delete(file); }
    }
}
