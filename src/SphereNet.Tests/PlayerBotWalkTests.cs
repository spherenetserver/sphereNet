using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Player bots that would stand and wait: somebody in a gap, a wall the edge
/// walk cannot get round, a portal tile taken or never stepped on, a foe behind a fence,
/// a bot off the roads or on a map with none, a body or a horse out of reach.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotWalkTests
{
    private const ushort WallTile = 0x0080;

    private static GameWorld World(bool secondMap = false)
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        if (secondMap) map.AddSyntheticMap(1, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        if (secondMap) world.InitMap(1, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        map.SetSyntheticItemTile(WallTile, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Impassable | SphereNet.MapData.Tiles.TileFlag.Wall,
            Height = 20,
        });
        TestHarness.SeedItemDefs(WallTile);
        return world;
    }

    private static void Wall(GameWorld world, int x0, int y0, int x1, int y1)
    {
        for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
            for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
                world.MapData!.AddSyntheticStatic(0, x, y, WallTile, 0);
    }

    /// <summary>A closed pen of wall round (cx, cy), radius r.</summary>
    private static void Pen(GameWorld world, int cx, int cy, int r)
    {
        Wall(world, cx - r, cy - r, cx + r, cy - r);
        Wall(world, cx - r, cy + r, cx + r, cy + r);
        Wall(world, cx - r, cy - r, cx - r, cy + r);
        Wall(world, cx + r, cy - r, cx + r, cy + r);
    }

    private const string RoadJson = """
        { "Waypoints": [
          { "Name": "W", "X": 100, "Y": 100, "Z": 0, "Connects": [ "E" ] },
          { "Name": "E", "X": 110, "Y": 100, "Z": 0 }
        ] }
        """;

    private const string FarInnJson = """
        { "Destinations": [
          { "Name": "Far Inn", "X": 111, "Y": 100, "Z": 0, "Type": "Inn", "City": "Town", "NearestWaypoint": "E" },
          { "Name": "Town Bank", "X": 100, "Y": 102, "Z": 0, "Type": "Bank", "City": "Town", "NearestWaypoint": "W",
            "Arrivals": [ { "X": 100, "Y": 103, "Z": 0, "Waypoints": [ "W" ] } ] }
        ] }
        """;

    private static BotNavGraph Nav(GameWorld world, string waypoints = RoadJson, string destinations = FarInnJson)
    {
        var nav = new BotNavGraph();
        nav.LoadWaypoints(waypoints, world.MapData);
        nav.LoadDestinations(destinations, world.MapData);
        return nav;
    }

    private static PlayerBotEngine Engine(GameWorld world, BotNavGraph nav, bool clients = false)
    {
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLines("en", new Dictionary<string, string[]>()),
            NullLogger.Instance, seed: 7)
        {
            TargetCount = 50,
            // The searches here are judged by the way they find, not by the clock: a cold
            // JIT can take a first search past the live 5 ms budget and turn a way into
            // "no path".
            SearchMs = 1000,
            SearchBatchMs = 1000,
        };
        if (clients)
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

    private static Character Bot(GameWorld world, Point3D at, string? goal, string? arch = null)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Wanderer";
        ch.Str = 60; ch.Dex = 60; ch.Int = 40;
        ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 60; ch.Hits = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        if (arch != null) ch.SetTag(PlayerBotEngine.ArchetypeTag, arch);
        if (goal != null) ch.SetTag(PlayerBotEngine.GoalTag, goal);
        world.PlaceCharacter(ch, at);
        return ch;
    }

    private static Character Npc(GameWorld world, Point3D at, string name = "orc")
    {
        var m = world.CreateCharacter();
        m.Name = name;
        m.BodyId = 0x11;
        m.NpcBrain = NpcBrainType.Monster;
        m.Str = 40; m.Dex = 40; m.MaxHits = 40; m.Hits = 40;
        m.SetStatFlag(StatFlag.Freeze);
        world.PlaceCharacter(m, at);
        return m;
    }

    private static string Trace(PlayerBotEngine engine, Character bot) =>
        string.Join(Environment.NewLine, engine.TraceOf(bot.Uid.Value.ToString()));

    /// <summary>Tick, running each tick's searches the way the host does (in parallel,
    /// between ticks), until <paramref name="done"/> or the tick count runs out.</summary>
    private static long Run(PlayerBotEngine engine, long now, int ticks, Func<bool>? done = null)
    {
        var po = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        for (int i = 0; i < ticks && (done == null || !done()); i++)
        {
            engine.RunSearches(po);
            engine.Tick(now);
            now += 100;
        }
        return now;
    }

    [Fact]
    public void SomebodyInTheGapIsWaitedForThenWalkedRoundNotTakenForAWall()
    {
        var world = World();
        // A wall across the road with a one-tile gap at y=100, somebody standing in it.
        Wall(world, 105, 90, 105, 99);
        Wall(world, 105, 101, 105, 110);
        Npc(world, new Point3D(105, 100, 0, 0), "a guard");
        var bot = Bot(world, new Point3D(100, 100, 0, 0), goal: "Far Inn");
        bot.Stam = 50; // short of full: it cannot push past
        var engine = Engine(world, Nav(world));
        engine.AdoptSaved();

        Run(engine, 1_000_000, 4000, () => bot.X == 111 && bot.Y == 100);

        Assert.True(bot.X == 111 && bot.Y == 100, $"bot at {bot.Position}{Environment.NewLine}{Trace(engine, bot)}");
        Assert.True(engine.StuckRescues == 0, $"rescued{Environment.NewLine}{Trace(engine, bot)}");
        Assert.True(engine.WindowMobileWaits > 0, "never waited for the one in the gap");
    }

    [Fact]
    public void AWallTheEdgeWalkCannotRoundIsCrossedByASearchRunOffTheAct()
    {
        var world = World();
        // A cup open to the west: walking east from inside it, the edge walk circles.
        Wall(world, 106, 92, 106, 108);
        Wall(world, 98, 92, 106, 92);
        Wall(world, 98, 108, 106, 108);
        var nav = Nav(world, """
            { "Waypoints": [
              { "Name": "W", "X": 102, "Y": 100, "Z": 0, "Connects": [ "E" ] },
              { "Name": "E", "X": 112, "Y": 100, "Z": 0 }
            ] }
            """, """
            { "Destinations": [
              { "Name": "Far Inn", "X": 113, "Y": 100, "Z": 0, "Type": "Inn", "City": "Town", "NearestWaypoint": "E" }
            ] }
            """);
        var bot = Bot(world, new Point3D(102, 100, 0, 0), goal: "Far Inn");
        var engine = Engine(world, nav);
        engine.AdoptSaved();

        Run(engine, 1_000_000, 3000, () => bot.X == 113 && bot.Y == 100);

        Assert.True(bot.X == 113 && bot.Y == 100, $"bot at {bot.Position}{Environment.NewLine}{Trace(engine, bot)}");
        Assert.True(engine.StuckRescues == 0, $"rescued{Environment.NewLine}{Trace(engine, bot)}");
        Assert.True(engine.WindowSearches > 0);
    }

    [Fact]
    public void ASecondBotOnTheSameRoadLinkFollowsTheWayTheFirstFound()
    {
        var world = World();
        Wall(world, 106, 92, 106, 108);
        Wall(world, 98, 92, 106, 92);
        Wall(world, 98, 108, 106, 108);
        var nav = Nav(world, """
            { "Waypoints": [
              { "Name": "W", "X": 102, "Y": 100, "Z": 0, "Connects": [ "E" ] },
              { "Name": "E", "X": 112, "Y": 100, "Z": 0 }
            ] }
            """, """
            { "Destinations": [
              { "Name": "Far Inn", "X": 113, "Y": 100, "Z": 0, "Type": "Inn", "City": "Town", "NearestWaypoint": "E" }
            ] }
            """);
        var first = Bot(world, new Point3D(102, 100, 0, 0), goal: "Far Inn");
        var engine = Engine(world, nav);
        engine.AdoptSaved();
        long now = Run(engine, 1_000_000, 3000, () => first.X == 113);
        Assert.Equal(113, first.X);
        Assert.True(engine.CorridorCount > 0);
        long searchesBefore = engine.WindowSearches;

        var second = Bot(world, new Point3D(102, 101, 0, 0), goal: "Far Inn");
        second.Name = "Follower";
        engine.AdoptSaved();
        Run(engine, now, 3000, () => second.X == 113);

        Assert.True(second.X == 113, $"second at {second.Position}{Environment.NewLine}{Trace(engine, second)}");
        Assert.True(engine.WindowCorridorUses > 0, Trace(engine, second));
        Assert.Equal(searchesBefore, engine.WindowSearches);
    }

    // A cave mouth: three teleporter tiles at (120..122, 100) carry a walker to
    // (300..302, 300); the way back out is (300..302, 302) -> (120..122, 101).
    private const string CaveRoads = """
        { "Waypoints": [
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 110, "Y": 100, "Z": 0, "Connects": [ "Mouth" ] },
          { "Name": "Mouth", "X": 118, "Y": 100, "Z": 0 },
          { "Name": "Inside", "X": 301, "Y": 296, "Z": 0, "Connects": [ "Back" ] },
          { "Name": "Back", "X": 301, "Y": 301, "Z": 0 }
        ] }
        """;

    private const string CavePlaces = """
        { "Destinations": [
          { "Name": "Cave Entrance", "X": 121, "Y": 100, "Z": 0, "Type": "DungeonEntrance", "City": "",
            "NearestWaypoint": "Mouth", "Arrivals": [ { "X": 121, "Y": 100, "Z": 0, "Waypoints": [ "Mouth" ] } ],
            "TargetX": 301, "TargetY": 300, "TargetZ": 0 },
          { "Name": "Cave Way Out", "X": 301, "Y": 302, "Z": 0, "Type": "DungeonAscend", "City": "",
            "NearestWaypoint": "Back", "Arrivals": [ { "X": 301, "Y": 302, "Z": 0, "Waypoints": [ "Back" ] } ],
            "TargetX": 121, "TargetY": 101, "TargetZ": 0 },
          { "Name": "Ghost Stair", "X": 200, "Y": 200, "Z": 0, "Type": "DungeonDescend", "City": "",
            "NearestWaypoint": "B" },
          { "Name": "Town Bank", "X": 100, "Y": 102, "Z": 0, "Type": "Bank", "City": "Town", "NearestWaypoint": "A" }
        ] }
        """;

    private static void CaveTeleporters(GameWorld world)
    {
        var list = new List<GameWorld.MapTeleport>();
        for (int i = 0; i < 3; i++)
        {
            list.Add(new GameWorld.MapTeleport(new Point3D((short)(120 + i), 100, 0, 0), new Point3D((short)(300 + i), 300, 0, 0), false));
            list.Add(new GameWorld.MapTeleport(new Point3D((short)(300 + i), 302, 0, 0), new Point3D((short)(120 + i), 101, 0, 0), false));
        }
        world.SetMapTeleports(list);
    }

    [Fact]
    public void ABotGoingIntoADungeonStepsOntoItsPortalAndStaysInside()
    {
        var world = World();
        CaveTeleporters(world);
        var nav = Nav(world, CaveRoads, CavePlaces);
        var bot = Bot(world, new Point3D(100, 100, 0, 0), goal: "Cave Entrance");
        var engine = Engine(world, nav);
        engine.AdoptSaved();

        long now = Run(engine, 1_000_000, 2000, () => bot.Y >= 300);

        Assert.True(bot.Y >= 300, $"bot at {bot.Position}{Environment.NewLine}{Trace(engine, bot)}");
        Assert.Equal(1, engine.WindowPortalCrossings);
        Assert.Equal(0, engine.StuckRescues);
        var mind = engine.MindOf(bot)!;
        Assert.Equal(BotMode.Linger, mind.Mode);
        Assert.Equal("Cave Way Out", mind.At?.Name);
        // It does not walk straight back out over the way-out tiles next to it.
        Run(engine, now, 15);
        Assert.True(bot.Y >= 300, $"bounced back out to {bot.Position}");
        Assert.True(mind.AvoidsTile(301, 302, 0, Environment.TickCount64 * 0 + now));
    }

    [Fact]
    public void ATakenPortalTileIsSwappedForAFreeOneOfTheSamePortal()
    {
        var world = World();
        CaveTeleporters(world);
        var nav = Nav(world, CaveRoads, CavePlaces);
        Npc(world, new Point3D(121, 100, 0, 0), "a loiterer"); // on the middle tile
        var bot = Bot(world, new Point3D(100, 100, 0, 0), goal: "Cave Entrance");
        bot.Stam = 50;
        var engine = Engine(world, nav);
        engine.AdoptSaved();

        Run(engine, 1_000_000, 2000, () => bot.Y >= 300);

        Assert.True(bot.Y >= 300, $"bot at {bot.Position}{Environment.NewLine}{Trace(engine, bot)}");
        Assert.True(bot.X is 300 or 302, $"landed at {bot.Position}");
        Assert.Equal(0, engine.StuckRescues);
    }

    [Fact]
    public void AStairTheWorldDoesNotHaveIsNeverAGoal()
    {
        var world = World();
        CaveTeleporters(world);
        var nav = Nav(world, CaveRoads, CavePlaces);
        var engine = Engine(world, nav);
        engine.ResolvePortals();

        Assert.True(nav.FindDestination("Ghost Stair")!.Portalless);
        Assert.Equal(3, nav.FindDestination("Cave Entrance")!.PortalTiles!.Count);
        Assert.Equal(3, nav.FindDestination("Cave Way Out")!.PortalTiles!.Count);
        Assert.Contains(engine.NavCheck(links: false, learn: false), l => l.Contains("Ghost Stair") && l.Contains("never chosen"));
    }

    [Fact]
    public void AMonsterBehindAFenceIsNoPreyAndOneStrikingFromThereIsLeftAlone()
    {
        var world = World();
        Pen(world, 108, 100, 2);
        var orc = Npc(world, new Point3D(108, 100, 0, 0));
        var bot = Bot(world, new Point3D(102, 100, 0, 0), goal: null, arch: "warrior");
        var engine = Engine(world, Nav(world), clients: true);
        engine.AdoptSaved();
        var mind = engine.MindOf(bot)!;

        long now = Run(engine, 1_000_000, 60);
        Assert.NotEqual(BotMode.Fight, mind.Mode);

        // Now it strikes at the bot from inside the pen: fought back (at its next act,
        // a lingering bot acts every few seconds), then given up.
        orc.FightTarget = bot.Uid;
        now = Run(engine, now, 200, () => mind.Mode == BotMode.Fight);
        Assert.Equal(BotMode.Fight, mind.Mode);
        now = Run(engine, now, (engine.FoeGiveUpMs + 5000) / 100);
        Assert.True(mind.Avoids(orc, now), Trace(engine, bot));
        Assert.NotEqual(BotMode.Fight, mind.Mode);
        Assert.True(engine.WindowUnreachableFoes >= 1);
        // And it is not taken up again while left alone.
        Run(engine, now, 100);
        Assert.NotEqual(BotMode.Fight, mind.Mode);
    }

    [Fact]
    public void ABotOffTheRoadsWalksBackToThemFirst()
    {
        var world = World();
        var nav = Nav(world);
        // 80 tiles east of the road's end: further than a trip looks for its first node.
        var bot = Bot(world, new Point3D(190, 100, 0, 0), goal: "Far Inn");
        var engine = Engine(world, nav);
        engine.AdoptSaved();

        Run(engine, 1_000_000, 3000, () => bot.X == 111 && bot.Y == 100);

        Assert.True(bot.Position.GetDistanceTo(new Point3D(111, 100, 0, 0)) <= 1,
            $"bot at {bot.Position}{Environment.NewLine}{Trace(engine, bot)}");
        Assert.Equal(0, engine.StrandedRescues);
        Assert.Equal(0, engine.StuckRescues);
    }

    [Fact]
    public void ABotWithNoRoadAnywhereNearIsLiftedHome()
    {
        var world = World();
        var nav = Nav(world);
        var bot = Bot(world, new Point3D(400, 400, 0, 0), goal: "Far Inn");
        var engine = Engine(world, nav);
        engine.AdoptSaved();

        Run(engine, 1_000_000, 200, () => engine.StrandedRescues > 0);

        Assert.Equal(1, engine.StrandedRescues);
        Assert.True(bot.Position.GetDistanceTo(new Point3D(100, 103, 0, 0)) <= 3, $"bot at {bot.Position}");
    }

    [Fact]
    public void ABotOnAMapWithNoRoadsIsTakenHome()
    {
        var world = World(secondMap: true);
        var nav = Nav(world);
        var bot = Bot(world, new Point3D(50, 50, 0, 1), goal: null);
        var engine = Engine(world, nav);
        engine.AdoptSaved();

        Run(engine, 1_000_000, 50);

        Assert.Equal(0, bot.MapIndex);
        Assert.True(engine.WindowMapRescues >= 1);
        Assert.True(bot.Position.GetDistanceTo(new Point3D(100, 103, 0, 0)) <= 3, $"bot at {bot.Position}");
    }

    [Fact]
    public void ABodyOutOfReachHasItsThingsHandedBack()
    {
        var world = World();
        Pen(world, 108, 100, 2);
        var bot = Bot(world, new Point3D(102, 100, 0, 0), goal: null);
        bot.Str = 100;
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        bot.Equip(pack, Layer.Pack);
        var engine = Engine(world, Nav(world), clients: true);
        engine.AdoptSaved();

        var corpse = world.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = ItemType.Corpse;
        corpse.SetTag("OWNER_UID", bot.Uid.Value.ToString());
        world.PlaceItem(corpse, new Point3D(108, 100, 0, 0));
        var gem = world.CreateItem();
        gem.BaseId = 0x0F0F;
        Assert.True(corpse.TryAddItem(gem));

        var mind = engine.MindOf(bot)!;
        long now = 1_000_000;
        mind.CorpseUid = corpse.Uid;
        mind.ReclaimUntilMs = now + 900_000;
        mind.Mode = BotMode.Reclaim;
        Run(engine, now, 1200, () => mind.Mode != BotMode.Reclaim);

        Assert.NotEqual(BotMode.Reclaim, mind.Mode);
        Assert.Equal(pack.Uid, gem.ContainedIn);
        Assert.True(corpse.IsDeleted);
        Assert.True(engine.WindowGiveUps >= 1, Trace(engine, bot));
    }

    [Fact]
    public void AHorseOutOfReachIsLeftAndAnotherSoughtLater()
    {
        var world = World();
        Pen(world, 108, 100, 2);
        var bot = Bot(world, new Point3D(102, 100, 0, 0), goal: null);
        bot.SetTag(PlayerBotEngine.RiderTag, "1");
        var horse = world.CreateCharacter();
        horse.Name = "a horse";
        horse.BodyId = 0xC8;
        horse.NpcBrain = NpcBrainType.Animal;
        horse.MaxHits = horse.Hits = 30;
        world.PlaceCharacter(horse, new Point3D(108, 100, 0, 0));
        Assert.True(horse.TryAssignOwnership(bot));
        bot.SetTag(PlayerBotEngine.MountTag, horse.Uid.Value.ToString());
        var engine = Engine(world, Nav(world), clients: true);
        engine.AddMount("c_horse_brown_dk");
        engine.AdoptSaved();

        Run(engine, 1_000_000, 2000, () => horse.IsDeleted);

        Assert.True(horse.IsDeleted, Trace(engine, bot));
        Assert.False(bot.TryGetTag(PlayerBotEngine.MountTag, out _));
        Assert.True(engine.WindowGiveUps >= 1);
    }

    [Fact]
    public void AStairWhoseFarSideIsOnTheSameRoadIsAPlaceNotADeadPortal()
    {
        var world = World();
        const string bank = """{ "Name": "Town Bank", """;
        const string rampJson = """
            { "Name": "Ramp Down", "X": 112, "Y": 100, "Z": 0, "Type": "DungeonDescend", "City": "",
              "NearestWaypoint": "B", "TargetX": 118, "TargetY": 100, "TargetZ": 0 },

            """;
        var nav = Nav(world, CaveRoads, CavePlaces.Replace(bank, rampJson + bank));
        var engine = Engine(world, nav);
        engine.ResolvePortals();

        var ramp = nav.FindDestination("Ramp Down")!;
        Assert.True(ramp.Portalless);
        Assert.True(ramp.WalkedThrough);
        Assert.False(nav.FindDestination("Ghost Stair")!.WalkedThrough);
        Assert.DoesNotContain(engine.NavCheck(links: false, learn: false), l => l.Contains("Ramp Down"));
    }

    [Fact]
    public void AShutDoorThatIsPartOfTheMapIsOpenedLikeAPlayersMacroOpensIt()
    {
        var world = World();
        const ushort DoorTile = 0x0675;
        world.MapData!.SetSyntheticItemTile(DoorTile, new SphereNet.MapData.Tiles.ItemTileData
        {
            Flags = SphereNet.MapData.Tiles.TileFlag.Impassable | SphereNet.MapData.Tiles.TileFlag.Door,
            Height = 20,
        });
        TestHarness.SeedItemDefs(DoorTile);
        // A long wall with a map door in it at y=100: the door is the way.
        Wall(world, 105, 40, 105, 99);
        Wall(world, 105, 101, 105, 160);
        world.MapData.AddSyntheticStatic(0, 105, 100, DoorTile, 0);
        var bot = Bot(world, new Point3D(100, 100, 0, 0), goal: "Far Inn");
        var engine = Engine(world, Nav(world), clients: true);
        engine.AdoptSaved();

        Run(engine, 1_000_000, 3000, () => bot.X == 111 && bot.Y == 100);

        Assert.True(bot.X == 111 && bot.Y == 100, $"bot at {bot.Position}{Environment.NewLine}{Trace(engine, bot)}");
        Assert.True(engine.WindowStaticDoors >= 1, Trace(engine, bot));
        Assert.Equal(0, engine.StuckRescues);
    }

    private const string BankPlaces = """
        { "Destinations": [
          { "Name": "Far Inn", "X": 111, "Y": 100, "Z": 0, "Type": "Inn", "City": "Town", "NearestWaypoint": "E" },
          { "Name": "Town Bank", "X": 100, "Y": 102, "Z": 0, "Type": "Bank", "City": "Town", "NearestWaypoint": "W",
            "Arrivals": [ { "X": 100, "Y": 103, "Z": 0, "Waypoints": [ "W" ] } ] }
        ] }
        """;

    private static Item Gold(GameWorld world, Item container, ushort amount)
    {
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED;
        gold.ItemType = ItemType.Gold;
        gold.Amount = amount;
        Assert.True(container.TryAddItem(gold));
        return gold;
    }

    private static Item Pack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    [Fact]
    public void ABotAtTheBankPutsItsSurplusGoldInThroughTheBankersMenu()
    {
        var world = World();
        var nav = Nav(world, RoadJson, BankPlaces);
        var bot = Bot(world, new Point3D(100, 103, 0, 0), goal: null);
        bot.Str = 200;
        var pack = Pack(world, bot);
        Gold(world, pack, 3000);
        var banker = Npc(world, new Point3D(101, 104, 0, 0), "the banker");
        banker.NpcBrain = NpcBrainType.Banker;
        var engine = Engine(world, nav, clients: true);
        engine.AdoptSaved();
        var mind = engine.MindOf(bot)!;
        long now = 1_000_000;
        mind.BeginLinger(nav.FindDestination("Town Bank"), now, new Random(1), short_: false);

        Run(engine, now, 200, () => engine.WindowDeposits > 0);

        var box = bot.GetEquippedItem(Layer.BankBox);
        Assert.True(box != null, Trace(engine, bot));
        long banked = box!.Contents.Where(i => i.ItemType == ItemType.Gold).Sum(i => (long)i.Amount);
        long carried = pack.Contents.Where(i => i.ItemType == ItemType.Gold).Sum(i => (long)i.Amount);
        Assert.True(banked >= 2400 && carried is >= 300 and <= 600, $"banked {banked}, carried {carried}{Environment.NewLine}{Trace(engine, bot)}");
        Assert.Equal(3000, banked + carried);
    }

    [Fact]
    public void ACasterWithARuneHomeRecallsWhenThereIsNoRoad()
    {
        var world = World();
        var nav = Nav(world, RoadJson, BankPlaces);
        var bot = Bot(world, new Point3D(400, 400, 0, 0), goal: "Far Inn", arch: "mage");
        bot.SetSkill(SkillType.Magery, 1000);
        bot.Int = 100; bot.MaxMana = 100; bot.Mana = 100;
        var pack = Pack(world, bot);
        var rune = world.CreateItem();
        rune.BaseId = 0x1F14;
        rune.ItemType = ItemType.Rune;
        rune.SetRuneMark(new Point3D(100, 103, 0, 0));
        rune.More1 = 10;
        Assert.True(pack.TryAddItem(rune));
        bot.SetTag(PlayerBotEngine.RuneTag, rune.Uid.Value.ToString());

        Character.SpellbookRequiredEnabled = false;
        var registry = new SphereNet.Game.Magic.SpellRegistry();
        registry.Register(new SphereNet.Game.Magic.SpellDef
        {
            Id = SpellType.Recall, Name = "Recall", Flags = SpellFlag.TargItem,
            CastTimeBase = 1, EffectBase = 0, EffectScale = 0,
        });
        var spells = new SphereNet.Game.Magic.SpellEngine(world, registry);
        var engine = Engine(world, nav);
        engine.CreateClient = ch =>
        {
            var c = new SphereNet.Game.Clients.GameClient(
                new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
            c.SetEngines(movement: new MovementEngine(world), spellEngine: spells);
            c.AttachServerDriven(ch);
            return c;
        };
        engine.AdoptSaved();

        // The cast timer runs on the real clock: let a little real time pass per tick.
        long now = 1_000_000;
        for (int i = 0; i < 300 && bot.X >= 200; i++)
        {
            now = Run(engine, now, 1);
            Thread.Sleep(10);
        }

        Assert.True(bot.Position.GetDistanceTo(new Point3D(100, 103, 0, 0)) <= 1, $"bot at {bot.Position}{Environment.NewLine}{Trace(engine, bot)}");
        Assert.True(engine.WindowRecalls >= 1);
        Assert.Equal(0, engine.StrandedRescues);
    }

    [Fact]
    public void ABotSettingOffTakesAnIdleBotAlongAndTheyArriveTogether()
    {
        var world = World();
        var nav = Nav(world, RoadJson, BankPlaces);
        var lead = Bot(world, new Point3D(100, 103, 0, 0), goal: null);
        var mate = Bot(world, new Point3D(101, 103, 0, 0), goal: null);
        mate.Name = "Mate";
        var engine = Engine(world, nav, clients: true);
        engine.CompanyPercent = 100;
        engine.AdoptSaved();
        long now = 1_000_000;
        var bank = nav.FindDestination("Town Bank");
        var lm = engine.MindOf(lead)!;
        var mm = engine.MindOf(mate)!;
        lm.BeginLinger(bank, now, new Random(1), short_: true);
        lm.LingerUntilMs = now;
        mm.BeginLinger(bank, now, new Random(1), short_: false);

        now = Run(engine, now, 30, () => engine.WindowCompanions > 0);
        Assert.Equal(1, engine.WindowCompanions);
        Assert.Same(lm, mm.Leader);
        Assert.Same(lm.Goal, mm.Goal);
        int widest = 0;
        Run(engine, now, 1500, () =>
        {
            widest = Math.Max(widest, lead.Position.GetDistanceTo(mate.Position));
            return lm.Mode == BotMode.Linger && mm.Mode == BotMode.Linger;
        });
        Assert.Equal(BotMode.Linger, mm.Mode);
        Assert.True(widest <= 8, $"they drifted {widest} tiles apart");
        Assert.True(lead.Position.GetDistanceTo(mate.Position) <= 3, $"{lead.Position} vs {mate.Position}");
    }

    [Fact]
    public void KeptWaysSurviveARestartThroughTheirFile()
    {
        var world = World();
        Wall(world, 106, 92, 106, 108);
        Wall(world, 98, 92, 106, 92);
        Wall(world, 98, 108, 106, 108);
        string roads = """
            { "Waypoints": [
              { "Name": "W", "X": 102, "Y": 100, "Z": 0, "Connects": [ "E" ] },
              { "Name": "E", "X": 112, "Y": 100, "Z": 0 }
            ] }
            """;
        string places = """
            { "Destinations": [
              { "Name": "Far Inn", "X": 113, "Y": 100, "Z": 0, "Type": "Inn", "City": "Town", "NearestWaypoint": "E" }
            ] }
            """;
        string file = Path.Combine(Path.GetTempPath(), $"sphnet_corridors_{Guid.NewGuid():N}.txt");
        try
        {
            var bot = Bot(world, new Point3D(102, 100, 0, 0), goal: "Far Inn");
            var engine = Engine(world, Nav(world, roads, places));
            engine.CorridorFile = file;
            engine.AdoptSaved();
            long now = Run(engine, 1_000_000, 3000, () => bot.X == 113);
            Assert.True(engine.CorridorCount > 0);
            engine.SaveCorridorsIfDue(now, force: true);

            var again = Engine(world, Nav(world, roads, places));
            again.CorridorFile = file;
            Assert.Equal(engine.CorridorCount, again.LoadCorridors());
            Assert.Equal(engine.CorridorCount, again.CorridorCount);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AStalledBotShowsInTheStallListWithItsLastRefusal()
    {
        var world = World();
        Pen(world, 102, 100, 1); // shut in
        var bot = Bot(world, new Point3D(102, 100, 0, 0), goal: "Far Inn");
        var engine = Engine(world, Nav(world));
        engine.AdoptSaved();

        Run(engine, 1_000_000, 150);

        var lines = engine.ListStalls(5);
        Assert.Contains(lines, l => l.Contains("Wanderer") && l.Contains("Travel") && l.Contains("refusal=terrain"));
        Assert.NotEqual("-", engine.RefusalReport());
    }
}

/// <summary>The shipped road data against the shipped script pack's teleporters: every
/// portal place the data names should have a portal in the world. The count of those
/// that do not is pinned, so new data that misses one fails here, and a fix lowers it.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotNavDataTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Measured 2026-10-21 against Scripts-X map0 teleporters (moongates excluded: they
    // are world items, placed by worldgen, not script data; stairs whose far side is on
    // the same road are walked through and do not count). The 14 left are passages
    // between dungeons the pack does not have.
    private const int MaxPortalless = 14;

    [Fact]
    public void ShippedPortalPlacesHaveTheirTeleportersInTheScriptPack()
    {
        string? maps = TestRepo.Optional("oldSphere/Scripts-X-main/maps/map0");
        if (Gate.MissingValue(output, "live script pack", maps)) return;
        string nav = TestRepo.Tracked("playerbots");

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 7168, 4096);
        var teleports = new List<GameWorld.MapTeleport>();
        foreach (string file in Directory.GetFiles(maps, "map0_teleports*.scp"))
            foreach (string raw in File.ReadLines(file))
            {
                var parts = raw.Split("//")[0].Trim().Split('=');
                if (parts.Length < 2 || Point(parts[0]) is not { } src || Point(parts[1]) is not { } dst) continue;
                teleports.Add(new GameWorld.MapTeleport(src, dst, false));
            }
        world.SetMapTeleports(teleports);

        var graph = BotNavGraph.Load(nav, map: null);
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, graph,
            BotChatBook.FromLines("en", new Dictionary<string, string[]>()), NullLogger.Instance, seed: 1);
        engine.ResolvePortals();

        var destard = graph.FindDestination("Destard L1 Entrance")!;
        Assert.Equal(3, destard.PortalTiles!.Count);
        Assert.All(destard.PortalTiles, t => Assert.Equal(2635, t.Y));
        var missing = graph.Destinations
            .Where(d => d.IsPortalType && d.Portalless && !d.WalkedThrough &&
                        !d.Type.Equals("Moongate", StringComparison.OrdinalIgnoreCase))
            .Select(d => d.Name).ToList();
        Assert.True(missing.Count <= MaxPortalless,
            $"{missing.Count} portal places without a teleporter (pinned {MaxPortalless}): {string.Join(", ", missing)}");
    }

    private static Point3D? Point(string text)
    {
        var p = text.Split(',');
        if (p.Length < 3 || !short.TryParse(p[0], out short x) || !short.TryParse(p[1], out short y) ||
            !sbyte.TryParse(p[2], out sbyte z))
            return null;
        byte map = p.Length > 3 && byte.TryParse(p[3], out byte m) ? m : (byte)0;
        return new Point3D(x, y, z, map);
    }
}
