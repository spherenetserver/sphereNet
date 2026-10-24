using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Where bots stand at a place: a place's outline, a place's room (a full one
/// draws nobody), walking on into a place rather than stopping on its doorstep, and
/// the weighted tile search.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotPlacesTests
{
    // Road W (60,100) - A (100,100) - B (120,100) - Y (128,100); a bank by B, an inn by
    // W, a fenced yard east of Y entered by its west gate.
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "W", "X": 60, "Y": 100, "Z": 0, "Connects": [ "A" ] },
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 120, "Y": 100, "Z": 0, "Connects": [ "Y" ] },
          { "Name": "Y", "X": 128, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Town Bank", "X": 121, "Y": 104, "Z": 0, "Type": "Bank", "City": "Town",
            "NearestWaypoint": "B", "Arrivals": [ { "X": 122, "Y": 104, "Z": 0, "Waypoints": [ "B" ] },
                                                { "X": 119, "Y": 104, "Z": 0, "Waypoints": [ "B" ] } ] },
          { "Name": "Town Inn", "X": 60, "Y": 101, "Z": 0, "Type": "Inn", "City": "Town",
            "NearestWaypoint": "W" },
          { "Name": "Town Yard", "X": 140, "Y": 100, "Z": 0, "Type": "Graveyard", "City": "Town",
            "NearestWaypoint": "Y", "Polygon": [ [130, 90], [150, 90], [150, 110], [130, 110] ],
            "Arrivals": [ { "X": 131, "Y": 100, "Z": 0, "Waypoints": [ "Y" ] } ] }
        ] }
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
        public required ListLogger Log { get; init; }
        public required GameWorld World { get; init; }
        public required PlayerBotEngine Engine { get; init; }
        public long Now { get; set; } = 1_000_000;

        public void Run(int ms)
        {
            for (int t = 0; t < ms; t += 100)
            {
                Engine.Tick(Now);
                Now += 100;
            }
            Assert.Empty(Log.Warnings);
        }
    }

    private static Rig MakeRig()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, map);
        nav.LoadDestinations(DestinationsJson, map);
        var log = new ListLogger();
        var engine = new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLanguages("en", new Dictionary<string, IReadOnlyDictionary<string, string[]>> { ["en"] = new Dictionary<string, string[]>() }),
            log, seed: 5)
        {
            TargetCount = 1000,
            Gravity = 0,
        };
        return new Rig { World = world, Engine = engine, Log = log };
    }

    private static Character Bot(Rig rig, string name, Point3D at, string? stayAt = null)
    {
        var ch = rig.World.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = name;
        ch.Str = 60; ch.Dex = 60; ch.Int = 40;
        ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 60; ch.Hits = 60;
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        rig.World.PlaceCharacter(ch, at);
        rig.Engine.AdoptSaved();
        if (stayAt != null)
        {
            var mind = rig.Engine.MindOf(ch)!;
            mind.LingerUntilMs = long.MaxValue;
            mind.At = rig.Engine.Nav.FindDestination(stayAt);
        }
        return ch;
    }

    [Fact]
    public void APlaceOutlineIsReadAndEnclosesItsTiles()
    {
        var rig = MakeRig();
        var yard = rig.Engine.Nav.FindDestination("Town Yard")!;
        Assert.NotNull(yard.Outline);
        Assert.True(yard.Encloses(140, 100));
        Assert.True(yard.Encloses(131, 91));
        Assert.False(yard.Encloses(128, 100));
        Assert.False(yard.Encloses(140, 111));
        Assert.Null(rig.Engine.Nav.FindDestination("Town Bank")!.Outline);
    }

    [Fact]
    public void AFullPlaceDrawsNobody()
    {
        var rig = MakeRig();
        rig.Engine.SetPlaceWeight("Bank", 100);
        rig.Engine.SetPlaceRoom("Bank", 2);
        rig.Engine.SetPlaceWeight("Inn", 1);
        rig.Engine.SetPlaceWeight("Graveyard", 0);
        for (int i = 0; i < 3; i++)
            Bot(rig, $"Banker{i}", new Point3D((short)(120 + i), 104, 0, 0), stayAt: "Town Bank");
        var walkers = Enumerable.Range(0, 4)
            .Select(i => Bot(rig, $"Walker{i}", new Point3D((short)(100 + i), 101, 0, 0)))
            .ToList();
        rig.Run(1500);
        foreach (var w in walkers)
            Assert.Equal("Town Inn", rig.Engine.MindOf(w)!.Goal?.Name);
    }

    [Fact]
    public void PlacesTxtGivesWeightAndRoom()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pbplaces_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "places.txt"),
                ["# comment", "*  0  6", "Bank  100  2", "Inn  1", "Graveyard 0"]);
            var rig = MakeRig();
            rig.Engine.LoadDistribution(dir);
            for (int i = 0; i < 3; i++)
                Bot(rig, $"Banker{i}", new Point3D((short)(120 + i), 104, 0, 0), stayAt: "Town Bank");
            var walker = Bot(rig, "Walker", new Point3D(100, 101, 0, 0));
            rig.Run(1500);
            Assert.Equal("Town Inn", rig.Engine.MindOf(walker)!.Goal?.Name);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ABotWalksOnIntoAYardInsteadOfStoppingAtItsGate()
    {
        var rig = MakeRig();
        rig.Engine.SetPlaceWeight("Bank", 0);
        rig.Engine.SetPlaceWeight("Inn", 0);
        rig.Engine.SetPlaceWeight("Graveyard", 1);
        var yard = rig.Engine.Nav.FindDestination("Town Yard")!;
        var gate = yard.Arrivals[0];
        var bots = Enumerable.Range(0, 3)
            .Select(i => Bot(rig, $"Visitor{i}", new Point3D((short)(118 + i), 101, 0, 0)))
            .ToList();
        rig.Run(60_000);
        foreach (var b in bots)
        {
            var mind = rig.Engine.MindOf(b)!;
            Assert.Equal(BotMode.Linger, mind.Mode);
            Assert.Same(yard, mind.At);
            Assert.True(yard.Encloses(b.X, b.Y), $"{b.Name} at {b.Position}");
            Assert.Null(mind.SettleTo);
        }
        // Spread over the yard, not all on the gate.
        Assert.True(bots.Count(b => b.Position.GetDistanceTo(gate) > 2) >= 2,
            string.Join(", ", bots.Select(b => b.Position.ToString())));
        Assert.True(bots.Select(b => (b.X, b.Y)).Distinct().Count() == bots.Count);
        Assert.Equal(0, rig.Engine.WindowCrowdLeaves);
    }

    [Fact]
    public void ARestartLeavesNoPlaceWithoutAStoppingSpot()
    {
        var rig = MakeRig();
        var file = Path.Combine(Path.GetTempPath(), "pbbad_" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            // The bank lost both its spots, the yard its only one, the inn none.
            File.WriteAllLines(file, ["Town Bank|1", "Town Bank|0", "Town Yard|0"]);
            rig.Engine.BadSpotFile = file;
            rig.Engine.LoadBadSpots();
            var bank = rig.Engine.Nav.FindDestination("Town Bank")!;
            var yard = rig.Engine.Nav.FindDestination("Town Yard")!;
            // The last spot learned of each is tried again; the others stay bad.
            Assert.Equal([1], bank.BadArrivals.ToArray());
            Assert.Empty(yard.BadArrivals);

            File.WriteAllLines(file, ["Town Bank|1"]);
            var again = MakeRig();
            again.Engine.BadSpotFile = file;
            again.Engine.LoadBadSpots();
            Assert.Equal([1], again.Engine.Nav.FindDestination("Town Bank")!.BadArrivals.ToArray());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AHastySearchFindsAWayRoundAWallInFewerNodes()
    {
        const ushort wallTile = 0x0676;
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(wallTile, new ItemTileData { Flags = TileFlag.Impassable, Height = 20 });
        TestHarness.SeedItemDefs(wallTile);
        // A wall across the straight way, open at its south end.
        for (int y = 80; y <= 125; y++) map.AddSyntheticStatic(0, 110, y, wallTile, 0);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var walker = world.CreateCharacter();
        walker.IsPlayer = true;
        walker.MaxHits = walker.Hits = 50;
        var from = new Point3D(100, 100, 0, 0);
        var to = new Point3D(120, 100, 0, 0);
        world.PlaceCharacter(walker, from);
        var pf = new Pathfinder(world);

        var best = pf.FindPath(from, to, 0, self: walker, maxNodes: 4000, maxRadius: 40);
        var hasty = pf.FindPath(from, to, 0, self: walker, maxNodes: 4000, maxRadius: 40, options: PathOptions.Hasty);
        Assert.NotNull(best);
        Assert.NotNull(hasty);
        Assert.Equal(to.X, hasty![^1].X);
        Assert.Equal(to.Y, hasty[^1].Y);
        Assert.True(hasty.Count <= best!.Count * 2, $"hasty {hasty.Count} best {best.Count}");
        // The fewest nodes each needs: the weighted one opens fewer.
        int Needs(PathOptions o)
        {
            for (int budget = 25; budget <= 4000; budget += 25)
                if (pf.FindPath(from, to, 0, self: walker, maxNodes: budget, maxRadius: 40, options: o) != null)
                    return budget;
            return int.MaxValue;
        }
        int plain = Needs(PathOptions.None), weighted = Needs(PathOptions.Hasty);
        Assert.True(weighted < plain, $"hasty needs {weighted} nodes, plain {plain}");
    }
}
