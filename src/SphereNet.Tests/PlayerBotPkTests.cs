using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.PlayerBots;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Player killer bots: kept to a haunt out of the guards' reach, never across
/// the guard line, set on the blue they see, raised at the haunt after death.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PlayerBotPkTests
{
    // A haunt (a dungeon mouth) at the west end of a road that runs east into a
    // guarded town (x 110..140).
    private const string WaypointsJson = """
        { "Waypoints": [
          { "Name": "H", "X": 60, "Y": 100, "Z": 0, "Connects": [ "M" ] },
          { "Name": "M", "X": 80, "Y": 100, "Z": 0, "Connects": [ "A" ] },
          { "Name": "A", "X": 100, "Y": 100, "Z": 0, "Connects": [ "B" ] },
          { "Name": "B", "X": 130, "Y": 100, "Z": 0, "Connects": [ ] }
        ] }
        """;

    private const string DestinationsJson = """
        { "Destinations": [
          { "Name": "Haunt Mouth", "X": 60, "Y": 101, "Z": 0, "Type": "DungeonEntrance", "City": "",
            "NearestWaypoint": "H" },
          { "Name": "Town Bank", "X": 131, "Y": 100, "Z": 0, "Type": "Bank", "City": "Town", "NearestWaypoint": "B" }
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
        var town = new Region { Name = "Town", Flags = RegionFlag.Guarded, MapIndex = 0 };
        town.AddRect(110, 80, 140, 120);
        world.AddRegion(town);
        return world;
    }

    private static PlayerBotEngine Engine(GameWorld world)
    {
        var nav = new BotNavGraph();
        nav.LoadWaypoints(WaypointsJson, world.MapData);
        nav.LoadDestinations(DestinationsJson, world.MapData);
        return new PlayerBotEngine(world, new MovementEngine(world), resources: null, nav,
            BotChatBook.FromLines("en", new Dictionary<string, string[]>()), NullLogger.Instance, seed: 5)
        {
            TargetCount = 50,
            SearchMs = 1000,
            SearchBatchMs = 1000,
            PkRespawnSec = 0,
            PkSpareSkill = 0,
            CreateClient = ch =>
            {
                var c = new SphereNet.Game.Clients.GameClient(
                    new SphereNet.Network.State.NetState(NullLogger.Instance), world,
                    new SphereNet.Game.Accounts.AccountManager(NullLoggerFactory.Instance), NullLogger.Instance);
                c.AttachServerDriven(ch);
                return c;
            },
        };
    }

    private static Character Pk(GameWorld world, Point3D at)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Name = "Slayer";
        ch.Str = 80; ch.Dex = 60; ch.Int = 40;
        ch.MaxStam = 60; ch.Stam = 60; ch.MaxHits = 80; ch.Hits = 80;
        ch.SetSkill(SkillType.Swordsmanship, 800);
        ch.SetTag(PlayerBotEngine.MarkerTag, "1");
        ch.SetTag(PlayerBotEngine.HomeTag, "Town");
        ch.SetTag(PlayerBotEngine.RoleTag, "pk");
        world.PlaceCharacter(ch, at);
        return ch;
    }

    private static Character Player(GameWorld world, Point3D at)
    {
        var p = world.CreateCharacter();
        p.IsPlayer = true;
        p.IsOnline = true;
        p.Name = "Hero";
        p.MaxHits = 60; p.Hits = 60;
        world.PlaceCharacter(p, at);
        world.AddOnlinePlayer(p);
        return p;
    }

    private static bool Guarded(GameWorld world, Point3D p) => world.FindRegion(p)?.IsGuarded == true;

    private static long Run(PlayerBotEngine engine, long now, int ticks, Func<bool>? done = null, Action? each = null)
    {
        var po = new ParallelOptions { MaxDegreeOfParallelism = 4 };
        for (int i = 0; i < ticks && (done == null || !done()); i++)
        {
            engine.RunSearches(po);
            engine.Tick(now);
            each?.Invoke();
            now += 100;
        }
        return now;
    }

    private static string Trace(PlayerBotEngine engine, Character bot) =>
        string.Join(Environment.NewLine, engine.TraceOf(bot.Uid.Value.ToString()));

    [Fact]
    public void APkInTownIsSetDownAtItsHauntAndNeverStepsBackIn()
    {
        var world = World();
        var pk = Pk(world, new Point3D(130, 100, 0, 0));
        var engine = Engine(world);
        engine.AdoptSaved();

        long now = Run(engine, 1_000_000, 20);
        Assert.False(Guarded(world, pk.Position), Trace(engine, pk));
        Assert.Equal("Haunt Mouth", pk.TryGetTag(PlayerBotEngine.HauntTag, out string? haunt) ? haunt : null);

        // Ten minutes of roaming: never inside the guard line, never far from the haunt.
        var at = new Point3D(60, 101, 0, 0);
        Run(engine, now, 6000, each: () =>
        {
            Assert.False(Guarded(world, pk.Position), $"in town at {pk.Position}{Environment.NewLine}{Trace(engine, pk)}");
            Assert.True(pk.Position.GetDistanceTo(at) <= engine.PkRoam + 12, $"strayed to {pk.Position}");
        });
        Assert.Equal(0, engine.StuckRescues);
    }

    [Fact]
    public void APkLiftWaitsWhileAPlayerWatchesThenGoesAhead()
    {
        var world = World();
        var pk = Pk(world, new Point3D(130, 100, 0, 0));
        // A player beside it in town: the lift out waits.
        Player(world, new Point3D(132, 100, 0, 0));
        var engine = Engine(world);
        engine.UnseenLiftWaitMs = 30_000;
        engine.AdoptSaved();

        long now = Run(engine, 1_000_000, 100);
        Assert.True(Guarded(world, pk.Position), Trace(engine, pk));
        Assert.Equal(1, engine.WindowLiftWaits);

        // Past the wait it goes anyway.
        Run(engine, now, 400, () => !Guarded(world, pk.Position));
        Assert.False(Guarded(world, pk.Position), Trace(engine, pk));
    }

    [Fact]
    public void ASurplusBotSomeoneWatchesDoesNotLogOut()
    {
        var world = World();
        var seen = Pk(world, new Point3D(130, 100, 0, 0));
        var unseen = Pk(world, new Point3D(60, 101, 0, 0));
        Player(world, new Point3D(131, 100, 0, 0));
        var engine = Engine(world);
        // Held in town by the watching player rather than lifted out of sight.
        engine.UnseenLiftWaitMs = 60_000;
        engine.AdoptSaved();
        engine.TargetCount = 0;
        Run(engine, 1_000_000, 20);
        Assert.NotNull(engine.MindOf(seen));
        Assert.Null(engine.MindOf(unseen));
    }

    [Fact]
    public void APkSetsOnABlueItSeesButNotOneInsideTheGuardLine()
    {
        var world = World();
        var pk = Pk(world, new Point3D(100, 100, 0, 0));
        var engine = Engine(world);
        engine.AdoptSaved();
        var mind = engine.MindOf(pk)!;

        // Blue, but standing in town.
        var safe = Player(world, new Point3D(112, 100, 0, 0));
        long now = Run(engine, 1_000_000, 50);
        Assert.NotEqual(safe, mind.Foe);
        Assert.False(Guarded(world, pk.Position));

        // Blue, out of town and in sight.
        var victim = Player(world, new Point3D((short)(pk.X - 6), pk.Y, 0, 0));
        Run(engine, now, 50, done: () => mind.Foe == victim);
        Assert.True(mind.Foe == victim, Trace(engine, pk));
        Assert.True(engine.WindowPkAttacks > 0);
    }

    [Fact]
    public void ANewPlayerAndOneAPkJustKilledAreLeftAlone()
    {
        var world = World();
        var pk = Pk(world, new Point3D(70, 100, 0, 0));
        var engine = Engine(world);
        engine.PkSpareSkill = 300;
        engine.AdoptSaved();
        var mind = engine.MindOf(pk)!;

        var fresh = Player(world, new Point3D(74, 100, 0, 0));
        long now = Run(engine, 1_000_000, 50);
        Assert.NotEqual(fresh, mind.Foe);

        // Killed by a player killer a moment ago (and raised since): left alone.
        foreach (var s in new[] { SkillType.Swordsmanship, SkillType.Tactics, SkillType.Anatomy, SkillType.Healing })
            fresh.SetSkill(s, 900);
        engine.OnDeath(fresh, pk);
        now = Run(engine, now, 50);
        Assert.NotEqual(fresh, mind.Foe);

        // A seasoned player nobody killed lately is fair game.
        var seasoned = Player(world, new Point3D((short)(pk.X - 4), pk.Y, 0, 0));
        foreach (var s in new[] { SkillType.Swordsmanship, SkillType.Tactics, SkillType.Anatomy, SkillType.Healing })
            seasoned.SetSkill(s, 900);
        Run(engine, now, 50, done: () => mind.Foe == seasoned);
        Assert.True(mind.Foe == seasoned, Trace(engine, pk));
    }

    [Fact]
    public void AKilledPkWaitsUnseenThenComesBackScattered()
    {
        var world = World();
        var engine = Engine(world);
        engine.PkRespawnSec = 60;
        var pks = Enumerable.Range(0, 4).Select(i => Pk(world, new Point3D((short)(70 + i), 100, 0, 0))).ToList();
        engine.AdoptSaved();
        long now = Run(engine, 1_000_000, 10);
        foreach (var pk in pks)
        {
            pk.Hits = 0;
            pk.SetStatFlag(StatFlag.Dead);
        }
        // Past the ghost time but short of the wait: still dead, a ghost no one sees.
        now = Run(engine, now, 300);
        Assert.All(pks, pk => Assert.True(pk.IsDead));
        Assert.All(pks, pk => Assert.False(pk.IsInWarMode));
        Run(engine, now, 1500, done: () => pks.All(p => !p.IsDead));
        Assert.All(pks, pk => Assert.False(pk.IsDead, Trace(engine, pk)));
        Assert.All(pks, pk => Assert.False(Guarded(world, pk.Position)));
        // Not all on one tile.
        Assert.True(pks.Select(p => (p.X, p.Y)).Distinct().Count() > 1,
            string.Join(" ", pks.Select(p => p.Position.ToString())));
    }

    [Fact]
    public void AMurdererIsNotWhatAPkHunts()
    {
        var world = World();
        var pk = Pk(world, new Point3D(70, 100, 0, 0));
        var engine = Engine(world);
        engine.AdoptSaved();
        var mind = engine.MindOf(pk)!;
        var red = Player(world, new Point3D(74, 100, 0, 0));
        red.Kills = 100;
        Run(engine, 1_000_000, 50);
        Assert.NotEqual(red, mind.Foe);
    }

    [Fact]
    public void ADeadPkIsRaisedAtItsHauntWithinSeconds()
    {
        var world = World();
        var pk = Pk(world, new Point3D(95, 100, 0, 0));
        var engine = Engine(world);
        engine.AdoptSaved();
        long now = Run(engine, 1_000_000, 10);
        pk.Hits = 0;
        pk.SetStatFlag(StatFlag.Dead);

        int ticks = 0;
        Run(engine, now, 400, done: () => !pk.IsDead, each: () => ticks++);

        Assert.False(pk.IsDead, Trace(engine, pk));
        Assert.True(ticks <= 160, $"raised after {ticks} ticks");
        Assert.True(pk.Position.GetDistanceTo(new Point3D(60, 101, 0, 0)) <= engine.PkRoam + 10, $"raised at {pk.Position}");
        Assert.False(Guarded(world, pk.Position));
        // No corpse run: what it died with stays on the body.
        Assert.NotEqual(BotMode.Reclaim, engine.MindOf(pk)!.Mode);
        Assert.Equal(1, engine.WindowPkRespawns);
    }
}
