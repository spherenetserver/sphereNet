using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Server-driven player characters: the road graph, the chat book, a bot
/// walking a route to its destination, and a bot resuming after a save and load.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotEngineTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"sphnet_bots_{Guid.NewGuid():N}");

    public PlayerBotEngineTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    // A straight east-west road: A (100,100) - B (110,100) - C (120,100). The links
    // are written one way only, as the source data sometimes has them.
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "C" ] },
          { "Name": "C", "X": 120, "Y": 100, "Z": 0, "Connects": [ ], "ArrivalRange": 2 }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Bank", "X": 121, "Y": 100, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "C", "Arrivals": [ { "X": 122, "Y": 101, "Z": 0 } ] },
          { "Name": "Town Inn", "X": 100, "Y": 101, "Z": 0, "Type": "Inn", "City": "Town",
            "NearestWaypoint": "A" }
        ] }
        """;

    private static GameWorld World()
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static BotNavGraph Nav(GameWorld world)
    {
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        return nav;
    }

    private static PlayerBotEngine Engine(GameWorld world, BotNavGraph nav) =>
        new(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLines("en", new Dictionary<string, string[]>()),
            NullLogger.Instance, seed: 7)
        { TargetCount = 1 };

    private static Character SavedBot(GameWorld world, Point3D at, string? goal)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Wanderer";
        ch.Str = 60; ch.Dex = 60; ch.Int = 40;
        ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 60; ch.Hits = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        if (goal != null) ch.SetTag(PlayerBotEngine.GoalTag, goal);
        world.PlaceCharacter(ch, at);
        return ch;
    }

    [Fact]
    public void RoadGraphLinksBothWaysAndRoutesEndToEnd()
    {
        var nav = Nav(World());
        Assert.Equal(3, nav.Waypoints.Count);
        int a = nav.FindWaypoint("A"), c = nav.FindWaypoint("C");
        Assert.Equal(["A", "B", "C"], nav.Route(a, c)!.Select(i => nav.Waypoints[i].Name));
        Assert.Equal(["C", "B", "A"], nav.Route(c, a)!.Select(i => nav.Waypoints[i].Name));
        Assert.Equal(nav.FindWaypoint("B"), nav.FindNearest(new Point3D(108, 103, 0, 0), 10));
        Assert.Equal(-1, nav.FindNearest(new Point3D(300, 300, 0, 0), 10));
        Assert.Equal(2, nav.Destinations.Count);
    }

    [Fact]
    public void ArrivalSpotsKeepTheNodesTheyAreWalkedFrom()
    {
        var world = World();
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations("""
            { "Destinations": [
              { "Name": "Shop", "X": 121, "Y": 100, "Z": 0, "Type": "VendorSmith", "City": "Town",
                "NearestWaypoint": "C", "Arrivals": [ { "X": 112, "Y": 101, "Z": 0, "Waypoints": [ "B", "C" ] } ] }
            ] }
            """, world.MapData);
        var shop = nav.Destinations[0];
        Assert.Single(shop.Arrivals);
        Assert.Equal([nav.FindWaypoint("B"), nav.FindWaypoint("C")], shop.ArrivalLinks[0]);
    }

    [Fact]
    public void ALearnedBadSpotIsNotChosenAgain()
    {
        var world = World();
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations("""
            { "Destinations": [
              { "Name": "Shop", "X": 121, "Y": 100, "Z": 0, "Type": "VendorSmith", "City": "Town", "NearestWaypoint": "C",
                "Arrivals": [ { "X": 112, "Y": 101, "Z": 0 }, { "X": 114, "Y": 101, "Z": 0 } ] }
            ] }
            """, world.MapData);
        var shop = nav.Destinations[0];
        shop.BadArrivals.Add(0);
        var rng = new Random(3);
        for (int i = 0; i < 50; i++) Assert.Equal(1, shop.PickArrival(rng));
        shop.BadArrivals.Add(1);
        Assert.Equal(-1, shop.PickArrival(rng));
        Assert.Equal(1, shop.ArrivalAt(new Point3D(115, 102, 0, 0)));
    }

    [Fact]
    public void LearnedBadSpotsComeBackFromTheirFile()
    {
        var world = World();
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        // A bank with two stopping spots: one learned bad stays bad. (A place the file
        // would leave with none gets its last one back - PlayerBotPlacesTests.)
        nav.LoadDestinations(DestinationsJson.Replace(
            "\"Arrivals\": [ { \"X\": 122, \"Y\": 101, \"Z\": 0 } ]",
            "\"Arrivals\": [ { \"X\": 122, \"Y\": 101, \"Z\": 0 }, { \"X\": 120, \"Y\": 101, \"Z\": 0 } ]"),
            world.MapData);
        Assert.Equal(2, nav.FindDestination("Town Bank")!.Arrivals.Count);
        string file = Path.Combine(_dir, "badspots.txt");
        File.WriteAllLines(file, ["Town Bank|0", "Nowhere|3", "Town Bank|9", "garbage"]);
        var engine = Engine(world, nav);
        engine.BadSpotFile = file;

        Assert.Equal(1, engine.LoadBadSpots());
        Assert.Contains(0, nav.FindDestination("Town Bank")!.BadArrivals);
        Assert.Equal(1, engine.BadSpots);
    }

    [Fact]
    public void ATeleporterThatCarriedTheBotOffIsSteppedRoundUnlessItIsTheWay()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        var mind = new BotMind(bot, new Random(1));
        mind.RememberPortal(101, 100, 0, now: 1000);

        Assert.True(mind.AvoidsTile(101, 100, 0, now: 2000));
        Assert.False(mind.AvoidsTile(102, 100, 0, now: 2000));
        Assert.False(mind.AvoidsTile(101, 100, 0, now: 1000 + 180_001)); // forgotten

        mind.RememberPortal(101, 100, 0, now: 5000);
        mind.Goal = new BotDestination { Name = "Way In", Type = "DungeonEntrance", City = "", Pos = new Point3D(102, 101, 0, 0) };
        Assert.False(mind.AvoidsTile(101, 100, 0, now: 6000));
    }

    [Fact]
    public void ASpotRecordedOnAWallIsSeatedBesideIt()
    {
        var world = World();
        world.MapData!.SetSyntheticItemTile(0x0080, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Impassable | SphereNet.MapData.Tiles.TileFlag.Wall,
            Height = 20,
        });
        world.MapData.AddSyntheticStatic(0, 121, 100, 0x0080, 0);
        TestHarness.SeedItemDefs(0x0080);
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson.Replace("\"X\": 122, \"Y\": 101", "\"X\": 121, \"Y\": 100"), world.MapData);

        var bank = nav.FindDestination("Town Bank")!;
        Assert.NotEqual((121, 100), (bank.Pos.X, bank.Pos.Y));
        Assert.Equal(1, bank.Pos.GetDistanceTo(new Point3D(121, 100, 0, 0)));
        Assert.Single(bank.Arrivals);
        Assert.Equal(1, bank.Arrivals[0].GetDistanceTo(new Point3D(121, 100, 0, 0)));
    }

    [Fact]
    public void NodeAtPlaceholderHeightIsSeatedOnTheGround()
    {
        var world = World();
        var nav = new BotNavGraph();
        nav.LoadWaypoints("""{ "Waypoints": [ { "Name": "Low", "X": 50, "Y": 50, "Z": -20 } ] }""", world.MapData);
        Assert.Equal(0, nav.Waypoints[0].Pos.Z);
        Assert.Equal(1, nav.ReseatedNodes);
    }

    [Fact]
    public void ChatLinesNeedEveryTokenFilled()
    {
        Assert.Equal("selling at Britain for 500",
            BotChatBook.Fill("selling at {place} for {price}",
                new Dictionary<string, string> { ["place"] = "Britain", ["price"] = "500" }));
        Assert.Null(BotChatBook.Fill("{actor} died", new Dictionary<string, string>()));
        Assert.Equal("hail", BotChatBook.Fill("hail", null));
    }

    [Fact]
    public void ChosenLanguageOverridesEnglishAndMissingCategoriesFallBack()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "chat", "en"));
        Directory.CreateDirectory(Path.Combine(_dir, "chat", "tr"));
        File.WriteAllLines(Path.Combine(_dir, "chat", "en", "small_talk.txt"), ["# comment", "hello there"]);
        File.WriteAllLines(Path.Combine(_dir, "chat", "en", "traveling.txt"), ["off I go"]);
        File.WriteAllLines(Path.Combine(_dir, "chat", "tr", "small_talk.txt"), ["selam millet"]);

        var book = BotChatBook.Load(_dir, "tr");
        var rng = new Random(1);
        Assert.Equal("selam millet", book.Pick("small_talk", rng));
        Assert.Equal("off I go", book.Pick("traveling", rng));
        Assert.Null(book.Pick("nonexistent", rng));
    }

    [Fact]
    public void AdoptedBotIsOnlineButNotAClient()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        var engine = Engine(world, Nav(world));

        Assert.Equal(1, engine.AdoptSaved());
        Assert.True(bot.IsOnline);
        Assert.True(bot.IsPlayerBot);
        Assert.False(bot.HasClient);
        Assert.False(bot.IsDisconnectedPlayer);
        Assert.Contains(bot, world.OnlinePlayers);
    }

    [Fact]
    public void BotWalksTheRoadToItsDestinationAndLingersThere()
    {
        var world = World();
        var nav = Nav(world);
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: "Town Bank");
        var engine = Engine(world, nav);
        engine.AdoptSaved();

        long now = 1_000_000;
        for (int i = 0; i < 3000 && bot.Position.GetDistanceTo(new Point3D(122, 101, 0, 0)) > 0; i++)
        {
            engine.Tick(now);
            now += 100;
        }

        Assert.Equal(new Point3D(122, 101, 0, 0), bot.Position);
        // One more act settles it into the visit; the trip's goal tag is cleared.
        for (int i = 0; i < 20; i++) { engine.Tick(now); now += 100; }
        Assert.False(bot.TryGetTag(PlayerBotEngine.GoalTag, out _));
        Assert.Equal(0, engine.StuckRescues);
    }

    [Fact]
    public void EachBotStepReachesTheViewersAsAMovePacket()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: "Town Bank");
        var engine = Engine(world, Nav(world));
        var moves = new List<byte[]>();
        engine.CreateClient = ch =>
        {
            var c = new SphereNet.Game.Clients.GameClient(
                new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.AttachServerDriven(ch);
            c.BroadcastMoveNearby = (_, _, packet, exclude, mover) =>
            {
                Assert.Equal(ch.Uid.Value, exclude);
                Assert.Same(ch, mover);
                moves.Add(packet.Build().Span.ToArray());
            };
            return c;
        };
        engine.AdoptSaved();

        long now = 1_000_000;
        var steps = new List<Point3D>();
        for (int i = 0; i < 3000 && bot.Position.GetDistanceTo(new Point3D(122, 101, 0, 0)) > 0; i++)
        {
            var before = bot.Position;
            engine.Tick(now);
            if (bot.Position != before) steps.Add(bot.Position);
            now += 100;
        }

        Assert.Equal(new Point3D(122, 101, 0, 0), bot.Position);
        Assert.NotEmpty(steps);
        // One 0x77 per step, carrying where the step ended.
        Assert.Equal(steps.Count, moves.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            var p = moves[i];
            Assert.Equal(0x77, p[0]);
            Assert.Equal(bot.Uid.Value, (uint)(p[1] << 24 | p[2] << 16 | p[3] << 8 | p[4]));
            Assert.Equal(steps[i].X, (short)(p[7] << 8 | p[8]));
            Assert.Equal(steps[i].Y, (short)(p[9] << 8 | p[10]));
        }
    }

    private static PlayerBotEngine EngineWithClients(GameWorld world, BotNavGraph nav)
    {
        var engine = Engine(world, nav);
        engine.CreateClient = ch =>
        {
            var c = new SphereNet.Game.Clients.GameClient(
                new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.AttachServerDriven(ch);
            return c;
        };
        return engine;
    }

    private static Character Monster(GameWorld world, Point3D at, Character target)
    {
        var m = world.CreateCharacter();
        m.Name = "orc";
        m.BodyId = 0x11;
        m.NpcBrain = SphereNet.Core.Enums.NpcBrainType.Monster;
        m.Str = 40; m.Dex = 40; m.MaxHits = 40; m.Hits = 40;
        world.PlaceCharacter(m, at);
        m.FightTarget = target.Uid;
        return m;
    }

    [Fact]
    public void AttackedBotFightsBackThroughItsClient()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        var engine = EngineWithClients(world, Nav(world));
        engine.AdoptSaved();
        var orc = Monster(world, new Point3D(101, 100, 0, 0), bot);

        long now = 1_000_000;
        for (int i = 0; i < 40; i++) { engine.Tick(now); now += 100; }

        var mind = engine.MindOf(bot)!;
        Assert.NotNull(mind.Client);
        Assert.Equal(BotMode.Fight, mind.Mode);
        Assert.Equal(orc.Uid, bot.FightTarget);
        Assert.True(bot.IsInWarMode);

        // The orc dies: the fight ends, war mode drops, life goes on.
        orc.Hits = 0;
        orc.SetStatFlag(SphereNet.Core.Enums.StatFlag.Dead);
        for (int i = 0; i < 40; i++) { engine.Tick(now); now += 100; }
        Assert.NotEqual(BotMode.Fight, mind.Mode);
        Assert.False(bot.IsInWarMode);
    }

    [Fact]
    public void RaisedBotWalksBackAndTakesItsGearOffItsCorpse()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        bot.Str = 100;
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = SphereNet.Core.Enums.ItemType.Container;
        bot.Equip(pack, SphereNet.Core.Enums.Layer.Pack);
        var engine = EngineWithClients(world, Nav(world));
        engine.AdoptSaved();

        var corpse = world.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = SphereNet.Core.Enums.ItemType.Corpse;
        corpse.SetTag("OWNER_UID", bot.Uid.Value.ToString());
        world.PlaceItem(corpse, new Point3D(105, 100, 0, 0));
        // A one-handed blade by its tiledata, the way every client file has it.
        world.MapData!.SetSyntheticItemTile(0x13B9, new SphereNet.MapData.Tiles.ItemTileData
        { Flags = SphereNet.MapData.Tiles.TileFlag.Wearable, Quality = (byte)SphereNet.Core.Enums.Layer.OneHanded });
        var sword = world.CreateItem();
        sword.BaseId = 0x13B9;
        sword.SetTag("EQUIPLAYER", ((byte)SphereNet.Core.Enums.Layer.OneHanded).ToString());
        Assert.True(corpse.TryAddItem(sword));
        var gem = world.CreateItem();
        gem.BaseId = 0x0F0F;
        Assert.True(corpse.TryAddItem(gem));

        var mind = engine.MindOf(bot)!;
        long now = 1_000_000;
        mind.CorpseUid = corpse.Uid;
        mind.ReclaimUntilMs = now + 240_000;
        mind.Mode = BotMode.Reclaim;
        for (int i = 0; i < 200 && mind.Mode == BotMode.Reclaim; i++) { engine.Tick(now); now += 100; }

        Assert.NotEqual(BotMode.Reclaim, mind.Mode);
        Assert.True(sword == bot.GetEquippedItem(SphereNet.Core.Enums.Layer.OneHanded),
            $"sword in {sword.ContainedIn} equipped={sword.IsEquipped} layer={sword.EquipLayer} pos={sword.Position} deleted={sword.IsDeleted}; pack={pack.Uid} corpse={corpse.Uid} bot={bot.Uid}");
        Assert.Equal(pack.Uid, gem.ContainedIn);
    }

    [Fact]
    public void InvitedBotJoinsThePlayersPartyAndFollows()
    {
        var world = World();
        var parties = new SphereNet.Game.Party.PartyManager();
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        var engine = Engine(world, Nav(world));
        engine.Parties = parties;
        engine.CreateClient = ch =>
        {
            var c = new SphereNet.Game.Clients.GameClient(
                new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.SetEngines(movement: new MovementEngine(world), partyManager: parties);
            c.AttachServerDriven(ch);
            return c;
        };
        engine.AdoptSaved();

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true; // a connected player
        player.Name = "Hero";
        player.Dex = 60; player.MaxStam = 60; player.Stam = 60;
        world.PlaceCharacter(player, new Point3D(102, 100, 0, 0));
        Assert.True(parties.Invite(player, bot, SphereNet.Game.Party.PartyIo.ForWorld(world, null)));

        long now = 1_000_000;
        for (int i = 0; i < 80; i++) { engine.Tick(now); now += 100; }
        var party = parties.FindParty(bot.Uid);
        Assert.NotNull(party);
        Assert.Equal(player.Uid, party!.Master);

        world.MoveCharacter(player, new Point3D(112, 100, 0, 0));
        for (int i = 0; i < 100; i++) { engine.Tick(now); now += 100; }
        Assert.True(bot.Position.GetDistanceTo(player.Position) <= 3, $"bot at {bot.Position}");
    }

    [Fact]
    public void GhostWalksToTheHealerAndIsRaisedThere()
    {
        var world = World();
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations("""
            { "Destinations": [
              { "Name": "Town Healer", "X": 121, "Y": 100, "Z": 0, "Type": "Healer", "City": "Town", "NearestWaypoint": "C",
                "Arrivals": [ { "X": 123, "Y": 100, "Z": 0 } ] }
            ] }
            """, world.MapData);
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        var engine = Engine(world, nav);
        engine.AdoptSaved();
        bot.Hits = 0;
        bot.SetStatFlag(SphereNet.Core.Enums.StatFlag.Dead);

        long now = 1_000_000;
        for (int i = 0; i < 2000 && bot.IsDead; i++) { engine.Tick(now); now += 100; }

        Assert.False(bot.IsDead);
        Assert.Equal(new Point3D(123, 100, 0, 0), bot.Position);
        Assert.Equal(0, engine.StuckRescues);
    }

    private static Item CorpseOf(GameWorld world, Character bot, Point3D at, out Item sword, out Item gem)
    {
        var corpse = world.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = SphereNet.Core.Enums.ItemType.Corpse;
        corpse.SetTag("OWNER_UID", bot.Uid.Value.ToString());
        world.PlaceItem(corpse, at);
        world.MapData!.SetSyntheticItemTile(0x13B9, new SphereNet.MapData.Tiles.ItemTileData
        { Flags = SphereNet.MapData.Tiles.TileFlag.Wearable, Quality = (byte)SphereNet.Core.Enums.Layer.OneHanded });
        sword = world.CreateItem();
        sword.BaseId = 0x13B9;
        sword.SetTag("EQUIPLAYER", ((byte)SphereNet.Core.Enums.Layer.OneHanded).ToString());
        Assert.True(corpse.TryAddItem(sword));
        gem = world.CreateItem();
        gem.BaseId = 0x0F0F;
        Assert.True(corpse.TryAddItem(gem));
        return corpse;
    }

    private static Item PackOf(GameWorld world, Character bot)
    {
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = SphereNet.Core.Enums.ItemType.Container;
        bot.Equip(pack, SphereNet.Core.Enums.Layer.Pack);
        return pack;
    }

    private static void Kill(Character bot)
    {
        bot.Hits = 0;
        bot.SetStatFlag(SphereNet.Core.Enums.StatFlag.Dead);
    }

    [Fact]
    public void DeadBotFarFromTheHealerIsCarriedThereAndGetsItsThingsBack()
    {
        var world = World();
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations("""
            { "Destinations": [
              { "Name": "Town Healer", "X": 121, "Y": 100, "Z": 0, "Type": "Healer", "City": "Town", "NearestWaypoint": "C",
                "Arrivals": [ { "X": 123, "Y": 100, "Z": 0 } ] }
            ] }
            """, world.MapData);
        var bot = SavedBot(world, new Point3D(300, 300, 0, 0), goal: null);
        bot.Str = 100;
        var pack = PackOf(world, bot);
        var engine = Engine(world, nav);
        engine.ReclaimRange = 40;
        engine.AdoptSaved();
        var corpse = CorpseOf(world, bot, new Point3D(300, 300, 0, 0), out var sword, out var gem);
        Kill(bot);

        long now = 1_000_000;
        for (int i = 0; i < 1000 && bot.IsDead; i++) { engine.Tick(now); now += 100; }

        Assert.False(bot.IsDead);
        Assert.Equal(new Point3D(123, 100, 0, 0), bot.Position);
        // 177 tiles from the body: what was on it comes back, the corpse goes.
        Assert.Same(sword, bot.GetEquippedItem(SphereNet.Core.Enums.Layer.OneHanded));
        Assert.Equal(pack.Uid, gem.ContainedIn);
        Assert.True(corpse.IsDeleted);
        Assert.False(bot.TryGetTag(PlayerBotEngine.CorpseTag, out _));
    }

    [Fact]
    public void ALootedItemIsNotHandedBack()
    {
        var world = World();
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations("""
            { "Destinations": [
              { "Name": "Town Healer", "X": 121, "Y": 100, "Z": 0, "Type": "Healer", "City": "Town", "NearestWaypoint": "C" }
            ] }
            """, world.MapData);
        var bot = SavedBot(world, new Point3D(300, 300, 0, 0), goal: null);
        var pack = PackOf(world, bot);
        var engine = Engine(world, nav);
        engine.AdoptSaved();
        var corpse = CorpseOf(world, bot, new Point3D(300, 300, 0, 0), out var sword, out var gem);
        Kill(bot);

        long now = 1_000_000;
        engine.Tick(now);           // it dies and lingers over the body a moment
        // Someone loots the sword meanwhile.
        corpse.RemoveItem(sword);
        world.PlaceItem(sword, new Point3D(301, 300, 0, 0));
        for (int i = 0; i < 1000 && bot.IsDead; i++) { now += 100; engine.Tick(now); }

        Assert.False(bot.IsDead);
        Assert.Null(bot.GetEquippedItem(SphereNet.Core.Enums.Layer.OneHanded));
        Assert.Equal(pack.Uid, gem.ContainedIn);
        Assert.False(sword.ContainedIn.IsValid);
    }

    [Fact]
    public void AGhostWalksAboutItsBodyInWarModeWhereTheLivingSeeIt()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(200, 200, 0, 0), goal: null);
        var engine = EngineWithClients(world, Nav(world));
        engine.AdoptSaved();
        Kill(bot);
        bot.SetStatFlag(SphereNet.Core.Enums.StatFlag.Insubstantial); // as death leaves a player ghost

        long now = 1_000_000;
        int farthest = 0;
        var moved = new HashSet<Point3D>();
        for (int i = 0; i < 250; i++)
        {
            engine.Tick(now);
            now += 100;
            farthest = Math.Max(farthest, bot.Position.GetDistanceTo(new Point3D(200, 200, 0, 0)));
            moved.Add(bot.Position);
        }

        Assert.True(bot.IsDead);
        Assert.True(bot.IsInWarMode);
        Assert.False(bot.IsConcealed);   // a manifested ghost
        Assert.True(moved.Count > 1, "the ghost never moved");
        Assert.True(farthest <= 5, $"wandered {farthest} tiles off");
    }

    [Fact]
    public void ADeadMurdererIsRaisedInItsOwnTown()
    {
        var world = World();
        var redTown = new SphereNet.Game.World.Regions.Region { Name = "Delucia", MapIndex = 0 };
        redTown.AddRect(400, 400, 420, 420);
        world.AddRegion(redTown);
        var nav = Nav(world);
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        bot.Kills = (short)(Character.MurderMinCount + 1);
        Assert.True(bot.IsMurderer);
        var engine = Engine(world, nav);
        engine.RedRespawnRegion = "Delucia";
        engine.AdoptSaved();
        Kill(bot);

        long now = 1_000_000;
        for (int i = 0; i < 1000 && bot.IsDead; i++) { engine.Tick(now); now += 100; }

        Assert.False(bot.IsDead);
        Assert.Equal(new Point3D(410, 410, 0, 0), bot.Position);
    }

    [Fact]
    public void BadlyHurtBotRunsAway()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        var engine = EngineWithClients(world, Nav(world));
        engine.AdoptSaved();
        var orc = Monster(world, new Point3D(101, 100, 0, 0), bot);
        orc.SetStatFlag(SphereNet.Core.Enums.StatFlag.Freeze); // it stays put, the bot must leave

        long now = 1_000_000;
        for (int i = 0; i < 20; i++) { engine.Tick(now); now += 100; }
        Assert.Equal(BotMode.Fight, engine.MindOf(bot)!.Mode);

        bot.Hits = 5; // of 60
        for (int i = 0; i < 100; i++) { engine.Tick(now); now += 100; }
        Assert.True(bot.Position.GetDistanceTo(orc.Position) >= 4,
            $"bot stayed at {bot.Position}, orc at {orc.Position}");
    }

    [Fact]
    public void BotWalksAroundAWallWithoutBeingRescued()
    {
        var world = World();
        // A wall across the road at x=105, y 94..106; the way round is past its ends.
        world.MapData!.SetSyntheticItemTile(0x0080, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Impassable | SphereNet.MapData.Tiles.TileFlag.Wall,
            Height = 20,
        });
        for (int y = 94; y <= 106; y++)
            world.MapData.AddSyntheticStatic(0, 105, y, 0x0080, 0);
        // The walk check reads a static's height from its ITEMDEF.
        TestHarness.SeedItemDefs(0x0080);

        var nav = new BotNavGraph();
        nav.LoadWaypoints("""
            { "Waypoints": [
              { "Name": "W", "X": 100, "Y": 100, "Z": 0, "Connects": [ "E" ] },
              { "Name": "E", "X": 110, "Y": 100, "Z": 0 }
            ] }
            """, world.MapData);
        nav.LoadDestinations("""
            { "Destinations": [
              { "Name": "Far Inn", "X": 111, "Y": 100, "Z": 0, "Type": "Inn", "City": "Town", "NearestWaypoint": "E" }
            ] }
            """, world.MapData);
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: "Far Inn");
        var engine = Engine(world, nav);
        engine.AdoptSaved();

        long now = 1_000_000;
        int widest = 0;
        for (int i = 0; i < 3000 && bot.Position.GetDistanceTo(new Point3D(111, 100, 0, 0)) > 0; i++)
        {
            engine.Tick(now);
            now += 100;
            widest = Math.Max(widest, Math.Abs(bot.Y - 100));
        }
        string trace = string.Join(Environment.NewLine, engine.TraceOf(bot.Uid.Value.ToString()));
        Assert.True(new Point3D(111, 100, 0, 0) == bot.Position, $"bot at {bot.Position}{Environment.NewLine}{trace}");
        Assert.True(engine.StuckRescues == 0, $"rescued {engine.StuckRescues}{Environment.NewLine}{trace}");
        Assert.True(widest >= 7, $"never went round the wall's end (widest {widest})");
    }

    [Fact]
    public void CarriedAwayBotDropsItsOldRoute()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: "Town Bank");
        var engine = Engine(world, Nav(world));
        engine.AdoptSaved();
        long now = 1_000_000;
        for (int i = 0; i < 10; i++) { engine.Tick(now); now += 100; }
        var mind = engine.MindOf(bot)!;
        Assert.Equal(BotMode.Travel, mind.Mode);

        // A gate or a teleporter puts it somewhere else entirely.
        world.MoveCharacter(bot, new Point3D(300, 300, 0, 0));
        for (int i = 0; i < 10; i++) { engine.Tick(now); now += 100; }
        Assert.Equal(BotMode.Linger, mind.Mode);
        Assert.False(bot.TryGetTag(PlayerBotEngine.GoalTag, out _));
        Assert.Equal(0, engine.StuckRescues);
    }

    [Fact]
    public void TripsStayOnTheConnectedPieceOfRoad()
    {
        var world = World();
        var nav = new BotNavGraph();
        nav.LoadWaypoints("""
            { "Waypoints": [
              { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
              { "Name": "B", "X": 110, "Y": 100, "Z": 0 },
              { "Name": "Isle", "X": 400, "Y": 400, "Z": 0 }
            ] }
            """, world.MapData);
        nav.LoadDestinations("""
            { "Destinations": [
              { "Name": "Town Bank", "X": 110, "Y": 101, "Z": 0, "Type": "Bank", "City": "Town", "NearestWaypoint": "B" },
              { "Name": "Town Inn", "X": 100, "Y": 101, "Z": 0, "Type": "Inn", "City": "Town", "NearestWaypoint": "A" },
              { "Name": "Isle Inn", "X": 400, "Y": 401, "Z": 0, "Type": "Inn", "City": "Town", "NearestWaypoint": "Isle" }
            ] }
            """, world.MapData);
        Assert.Equal(2, nav.ComponentCount);

        var bot = SavedBot(world, new Point3D(100, 100, 0, 0), goal: null);
        var engine = Engine(world, nav);
        engine.AdoptSaved();
        var goals = new HashSet<string>();
        long now = 1_000_000;
        for (int i = 0; i < 20_000; i++)
        {
            engine.Tick(now);
            now += 1000;
            if (bot.TryGetTag(PlayerBotEngine.GoalTag, out string? g) && g != null) goals.Add(g);
        }
        Assert.NotEmpty(goals);
        Assert.DoesNotContain("Isle Inn", goals);
    }

    [Fact]
    public void SavedBotResumesItsTripAfterReload()
    {
        var world = World();
        var bot = SavedBot(world, new Point3D(110, 100, 0, 0), goal: "Town Bank");
        uint uid = bot.Uid.Value;
        // Online state is runtime only: the save must hold an ordinary player.
        var engine = Engine(world, Nav(world));
        engine.AdoptSaved();

        new SphereNet.Persistence.Save.WorldSaver(LoggerFactory.Create(_ => { })).Save(world, _dir);

        var reloaded = World();
        new SphereNet.Persistence.Load.WorldLoader(LoggerFactory.Create(_ => { })).Load(reloaded, _dir);
        var back = reloaded.FindChar(new SphereNet.Core.Types.Serial(uid));
        Assert.NotNull(back);
        Assert.True(back!.IsPlayer);
        Assert.False(back.IsOnline);
        Assert.Equal(new Point3D(110, 100, 0, 0), back.Position);
        Assert.True(back.TryGetTag(PlayerBotEngine.GoalTag, out string? goal));
        Assert.Equal("Town Bank", goal);

        var engine2 = Engine(reloaded, Nav(reloaded));
        Assert.Equal(1, engine2.AdoptSaved());
        long now = 1_000_000;
        for (int i = 0; i < 3000 && back.Position.GetDistanceTo(new Point3D(122, 101, 0, 0)) > 0; i++)
        {
            engine2.Tick(now);
            now += 100;
        }
        Assert.Equal(new Point3D(122, 101, 0, 0), back.Position);
    }
}
