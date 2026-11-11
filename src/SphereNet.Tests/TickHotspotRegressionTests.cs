using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The places a sampled profile of a 5,000-creature, 300-player world found the tick
/// spending its time, each pinned to the behaviour it had before it was made cheaper:
/// the range walks, the active-sector set, a spawn point resolving its MORE1, the
/// walk check's diagnostic, and the route search - which also had to start agreeing
/// with the step it plans for.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TickHotspotRegressionTests : IDisposable
{
    public void Dispose() => Sector.SleepDelayMs = 10L * 60 * 1000;

    private static GameWorld BareWorld(int size = 1024)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, size, size);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    // ------------------------------------------------------------------
    //  Range walks
    // ------------------------------------------------------------------

    /// <summary>What the sectors' own walk yields for a centre and range, sector by
    /// sector - the selection and order every range query has always had.</summary>
    private static List<ObjBase> SectorWalk(GameWorld world, Point3D center, int range)
    {
        var all = new List<ObjBase>();
        int minSx = Math.Max(0, (center.X - range) / Sector.SectorSize);
        int maxSx = (center.X + range) / Sector.SectorSize;
        int minSy = Math.Max(0, (center.Y - range) / Sector.SectorSize);
        int maxSy = (center.Y + range) / Sector.SectorSize;
        for (int sx = minSx; sx <= maxSx; sx++)
            for (int sy = minSy; sy <= maxSy; sy++)
                if (world.GetSector(center.Map, sx, sy) is { } sector)
                    all.AddRange(sector.GetObjectsInRange(center, range));
        return all;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(18)]
    [InlineData(70)]
    public void CharAndItemRangeQueries_ReturnWhatTheSectorWalkReturns(int range)
    {
        var world = BareWorld();
        var rng = new Random(20261111);
        // Around the corner where four sectors meet, so every range crosses a border.
        var center = new Point3D(128, 128, 0, 0);
        for (int i = 0; i < 160; i++)
        {
            var at = new Point3D((short)(128 + rng.Next(-80, 81)), (short)(128 + rng.Next(-80, 81)), 0, 0);
            if (i % 3 == 0)
            {
                var ch = world.CreateCharacter();
                world.PlaceCharacter(ch, at);
            }
            else
            {
                var item = world.CreateItem();
                world.PlaceItem(item, at);
            }
        }
        // Several things on the centre tile itself, for the range-0 walk.
        for (int i = 0; i < 3; i++)
            world.PlaceItem(world.CreateItem(), center);
        world.PlaceCharacter(world.CreateCharacter(), center);

        var reference = SectorWalk(world, center, range);
        Assert.Equal(reference, world.GetObjectsInRange(center, range).ToList());
        Assert.Equal(reference.OfType<Character>().ToList(), world.GetCharsInRange(center, range).ToList());
        Assert.Equal(reference.OfType<Item>().ToList(), world.GetItemsInRange(center, range).ToList());
        if (range > 0)
            Assert.NotEmpty(reference.OfType<Item>());
    }

    /// <summary>"What lies on this tile" is answered from the sector's per-tile index,
    /// so it has to follow an item that is moved, deleted or added.</summary>
    [Fact]
    public void ItemsOnOneTile_FollowAMoveADeleteAndAnAddition()
    {
        var world = BareWorld();
        var tile = new Point3D(300, 300, 0, 0);
        var a = world.CreateItem(); world.PlaceItem(a, tile);
        var b = world.CreateItem(); world.PlaceItem(b, tile);
        var elsewhere = world.CreateItem(); world.PlaceItem(elsewhere, new Point3D(301, 300, 0, 0));

        Assert.Equal(SectorWalk(world, tile, 0).OfType<Item>(), world.GetItemsInRange(tile, 0).ToList());
        Assert.Equal(2, world.GetItemsInRange(tile, 0).Count());

        b.Position = new Point3D(310, 305, 0, 0);
        Assert.Equal([a], world.GetItemsInRange(tile, 0).ToList());
        Assert.Equal([b], world.GetItemsInRange(b.Position, 0).ToList());

        var c = world.CreateItem(); world.PlaceItem(c, tile);
        Assert.Equal(SectorWalk(world, tile, 0).OfType<Item>(), world.GetItemsInRange(tile, 0).ToList());
        Assert.Equal(2, world.GetItemsInRange(tile, 0).Count());

        world.DeleteObject(a);
        Assert.Equal([c], world.GetItemsInRange(tile, 0).ToList());

        // An item deleted while the tile is being walked is not handed out.
        var d = world.CreateItem(); world.PlaceItem(d, tile);
        var seen = new List<Item>();
        foreach (var item in world.GetItemsInRange(tile, 0))
        {
            seen.Add(item);
            foreach (var other in new[] { c, d })
                if (other != item && !other.IsDeleted)
                    world.DeleteObject(other);
        }
        Assert.Single(seen);
    }

    // ------------------------------------------------------------------
    //  The active-sector set
    // ------------------------------------------------------------------

    private static Character OnlinePlayer(GameWorld world, Point3D at)
    {
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.IsOnline = true;
        player.MaxHits = 100; player.Hits = 100;
        world.PlaceCharacter(player, at);
        world.AddOnlinePlayer(player);
        return player;
    }

    [Fact]
    public void NoSectorIsActiveBeforeTheWorldHasTicked()
    {
        var world = BareWorld();
        OnlinePlayer(world, new Point3D(500, 500, 0, 0));

        Assert.False(world.IsInActiveArea(0, 500, 500));
        Assert.Empty(world.ActiveSectorsForProbe);
    }

    [Fact]
    public void TheWindowIsReportedNewOnce_AndPlayersOfOneSectorShareIt()
    {
        var world = BareWorld();
        // Two players in one sector and a third one sector over: 5x5 and 5x5 shifted
        // by one column is 30 sectors, each of them once.
        OnlinePlayer(world, new Point3D(500, 500, 0, 0));
        OnlinePlayer(world, new Point3D(510, 505, 0, 0));
        OnlinePlayer(world, new Point3D(500 + Sector.SectorSize, 500, 0, 0));

        world.OnTick();

        Assert.Equal(30, world.ActiveSectorsForProbe.Count);
        Assert.Equal(30, world.ActiveSectorsForProbe.Distinct().Count());
        Assert.Equal(30, world.NewlyActiveSectors.Count);
        Assert.True(world.IsInActiveArea(0, 500, 500));
        Assert.True(world.IsInActiveArea(0, 500 - 2 * Sector.SectorSize, 500 + 2 * Sector.SectorSize));
        Assert.False(world.IsInActiveArea(0, 500 - 3 * Sector.SectorSize, 500));
        Assert.All(world.ActiveSectorsForProbe, s => Assert.False(s.IsSleeping));

        world.OnTick();

        Assert.Equal(30, world.ActiveSectorsForProbe.Count);
        Assert.Empty(world.NewlyActiveSectors);
    }

    [Fact]
    public void ASectorKeptAwakeByTheGrace_IsNotNewlyActive_AndOneLeftBehindSleeps()
    {
        Sector.SleepDelayMs = 10L * 60 * 1000;
        var world = BareWorld(2048);
        var home = new Point3D(200, 200, 0, 0);
        var away = new Point3D(1600, 1600, 0, 0);
        var player = OnlinePlayer(world, home);
        world.OnTick();
        var homeSector = world.GetSector(home)!;
        var corner = world.GetSector(0, home.X / Sector.SectorSize + 2, home.Y / Sector.SectorSize + 2)!;

        world.MoveCharacter(player, away, fireRegionEvents: false);
        world.OnTick();

        // The sector the player stood in is inside its SECTORSLEEP grace: still
        // ticking, and not a sector that "just woke up". A window cell nobody was
        // ever in has no grace and goes back to sleep.
        Assert.Contains(homeSector, world.ActiveSectorsForProbe);
        Assert.True(world.IsInActiveArea(0, home.X, home.Y));
        Assert.DoesNotContain(homeSector, world.NewlyActiveSectors);
        Assert.False(homeSector.IsSleeping);
        Assert.DoesNotContain(corner, world.ActiveSectorsForProbe);
        Assert.True(corner.IsSleeping);
        Assert.Equal(25, world.NewlyActiveSectors.Count);
        Assert.Equal(26, world.ActiveSectorsForProbe.Count);
    }

    // ------------------------------------------------------------------
    //  A spawn point resolving MORE1
    // ------------------------------------------------------------------

    private const string SpawnScript = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_slice
        TYPE=t_spawn_char

        [CHARDEF c_slice_probe]
        DEFNAME=c_slice_probe
        ID=0x190
        NAME=slice probe
        CAN=0x04

        [SPAWN spawn_slice_first]
        ID=c_slice_probe

        [EOF]
        """;

    private const string LaterSpawnScript = """
        [SPAWN spawn_slice_later]
        ID=c_slice_probe

        [EOF]
        """;

    private static (GameWorld World, ResourceHolder Res) SpawnWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        string file = Path.Combine(Path.GetTempPath(), $"sphnet_hotspot_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, SpawnScript);
        var res = new ResourceHolder(lf.CreateLogger<ResourceHolder>()) { ScpBaseDir = Path.GetDirectoryName(file) ?? "" };
        res.LoadResourceFile(file);
        new DefinitionLoader(res, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();

        var world = new GameWorld(lf);
        world.InitMap(0, 256, 256);
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return (world, res);
    }

    private static Item SpawnPoint(GameWorld world, ResourceHolder res, Point3D at, Action<Item> configure)
    {
        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, at);
        configure(stone);
        stone.InitializeSpawnComponent(world, res);
        return stone;
    }

    [Fact]
    public void ASpawnPointFindsItsGroupThroughTheSpawnSlice_IncludingOneLoadedLater()
    {
        var (world, res) = SpawnWorld();

        var slice = res.GetResourcesOfType(ResType.Spawn);
        Assert.Equal(res.GetAllResources().Where(l => l.Id.Type == ResType.Spawn).ToList(), slice.ToList());
        Assert.Single(slice);
        Assert.Same(slice, res.GetResourcesOfType(ResType.Spawn));

        var first = res.ResolveDefName("spawn_slice_first");
        Assert.True(first.IsValid);
        var stone = SpawnPoint(world, res, new Point3D(100, 100, 0, 0), s => s.More1 = (uint)first.Index);
        Assert.Equal("spawn_slice_first", stone.SpawnChar!.GetSpawnDefName(), ignoreCase: true);

        // A resource that arrives afterwards (a resync, a later file) is in the next
        // answer: the slice is tied to the table's contents, not built once.
        string later = Path.Combine(Path.GetTempPath(), $"sphnet_hotspot_{Guid.NewGuid():N}.scp");
        File.WriteAllText(later, LaterSpawnScript);
        res.LoadResourceFile(later);
        Assert.Equal(2, res.GetResourcesOfType(ResType.Spawn).Count);
        var second = res.ResolveDefName("spawn_slice_later");
        var stone2 = SpawnPoint(world, res, new Point3D(110, 100, 0, 0), s => s.More1 = (uint)second.Index);
        Assert.Equal("spawn_slice_later", stone2.SpawnChar!.GetSpawnDefName(), ignoreCase: true);

        // A MORE1 that names no group is still a plain chardef index.
        int chardef = res.ResolveDefName("c_slice_probe").Index;
        var stone3 = SpawnPoint(world, res, new Point3D(120, 100, 0, 0), s => s.More1 = (uint)chardef);
        Assert.Equal("c_slice_probe", stone3.SpawnChar!.GetSpawnDefName(), ignoreCase: true);
    }

    // ------------------------------------------------------------------
    //  RESTOCK reaches the spawn points of a sleeping world
    // ------------------------------------------------------------------

    /// <summary>Source-X RESTOCK gives every spawn point one component tick whatever
    /// its sector is doing (CSector::Restock -> CCSpawn::OnTickComponent): the next
    /// check is set and, below the amount, one child is made. RESPAWN does not touch
    /// spawn points at all - it raises dead NPCs.</summary>
    [Fact]
    public void Restock_GivesASleepingSpawnPointOneChildPerTick_UpToItsAmount()
    {
        var (world, res) = SpawnWorld();
        var stone = SpawnPoint(world, res, new Point3D(100, 100, 0, 0), s =>
        {
            s.SetTag("MORE1_DEFNAME", "c_slice_probe");
            s.Amount = 3;
        });
        Assert.True(world.GetSector(stone.Position)!.IsSleeping);   // no player has ever been near

        Assert.Equal(0, world.RespawnDeadNpcs());
        Assert.Empty(stone.SpawnChar!.SpawnedUids);

        for (int expected = 1; expected <= 3; expected++)
        {
            Assert.True(world.RestockSpawner(stone));
            Assert.Equal(expected, stone.SpawnChar.SpawnedUids.Count);
        }
        Assert.False(world.RestockSpawner(stone));      // full
        Assert.Equal(3, stone.SpawnChar.SpawnedUids.Count);
        Assert.All(stone.SpawnChar.SpawnedUids, uid => Assert.NotNull(world.FindChar(uid)));
    }

    [Fact]
    public void Restock_LeavesAStoppedSpawnPointStopped_AndTheSectorVerbReachesItsOwnPoints()
    {
        var (world, res) = SpawnWorld();
        var stopped = SpawnPoint(world, res, new Point3D(100, 100, 0, 0), s =>
        {
            s.SetTag("MORE1_DEFNAME", "c_slice_probe");
            s.Amount = 2;
        });
        stopped.SpawnChar!.Stop();
        Assert.False(world.RestockSpawner(stopped));
        Assert.Empty(stopped.SpawnChar.SpawnedUids);

        var running = SpawnPoint(world, res, new Point3D(105, 100, 0, 0), s =>
        {
            s.SetTag("MORE1_DEFNAME", "c_slice_probe");
            s.Amount = 2;
        });
        var sector = world.GetSector(running.Position)!;
        Assert.True(sector.TryExecuteCommand("RESTOCK", "", null!));
        Assert.Single(running.SpawnChar!.SpawnedUids);
        Assert.Empty(stopped.SpawnChar.SpawnedUids);
    }

    // ------------------------------------------------------------------
    //  The item timer drain's time budget
    // ------------------------------------------------------------------

    /// <summary>With a budget, a tick stops running item timers when its time is up
    /// and the rest keep their deadline: every timer still fires exactly once, over
    /// more ticks. Without one (a bare world) they all fire on the tick asked for.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TheItemTimerBudget_SpreadsADrain_AndLosesNoTimer(int budgetMs)
    {
        var world = BareWorld();
        world.ItemTimerBudgetMs = budgetMs;
        var fired = new Dictionary<Item, int>(ReferenceEqualityComparer.Instance);
        Item.OnTimerExpired = item =>
        {
            fired[item] = fired.GetValueOrDefault(item) + 1;
            // Longer than the 1 ms budget, so a budgeted drain cannot reach a second one.
            var burn = System.Diagnostics.Stopwatch.StartNew();
            while (burn.Elapsed.TotalMilliseconds < 2.5) { }
            return TriggerResult.True;      // the script keeps the item
        };
        long due = Environment.TickCount64 - 1000;    // all overdue, the first one longest
        var items = new List<Item>();
        for (int i = 0; i < 6; i++)
        {
            var item = world.CreateItem();      // never placed: no sector to be asleep in
            item.SetTimeout(due + i);
            items.Add(item);
        }
        Assert.Equal(6, world.TimerQueueCount);

        world.OnTick();

        if (budgetMs == 0)
        {
            Assert.Equal(6, fired.Count);
            Assert.Equal(0, world.TimerQueueBudgetDeferred);
        }
        else
        {
            Assert.Single(fired);                   // the first always runs, then time is up
            Assert.Same(items[0], fired.Keys.Single());
            Assert.Equal(5, world.TimerQueueBudgetDeferred);
            Assert.Equal(5, world.TimerQueueCount); // put back under the deadlines they had
            for (int tick = 0; tick < 5; tick++)
                world.OnTick();
        }

        Assert.Equal(6, fired.Count);
        Assert.All(fired.Values, count => Assert.Equal(1, count));
        Assert.Equal(0, world.TimerQueueCount);
    }

    // ------------------------------------------------------------------
    //  The walk check and the route search
    // ------------------------------------------------------------------

    private const ushort WallTile = 0x0520;    // synthetic impassable wall
    private const ushort RugTile = 0x0521;     // synthetic flat decoration, blocks nothing

    private static (GameWorld World, WalkCheck Walk, MapDataManager Map, Character Mover) WalkWorld()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(WallTile, new ItemTileData { Flags = TileFlag.Impassable | TileFlag.Wall, Height = 20, Name = "wall" });
        map.SetSyntheticItemTile(RugTile, new ItemTileData { Flags = TileFlag.None, Height = 0, Name = "rug" });
        TestHarness.SeedItemDefs(WallTile, RugTile);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var mover = world.CreateCharacter();
        mover.IsPlayer = true;
        mover.MaxStam = 100; mover.Stam = 100;
        return (world, new WalkCheck(world), map, mover);
    }

    private static Direction DirectionBetween(Point3D from, Point3D to) => (to.X - from.X, to.Y - from.Y) switch
    {
        (0, -1) => Direction.North,
        (1, -1) => Direction.NorthEast,
        (1, 0) => Direction.East,
        (1, 1) => Direction.SouthEast,
        (0, 1) => Direction.South,
        (-1, 1) => Direction.SouthWest,
        (-1, 0) => Direction.West,
        (-1, -1) => Direction.NorthWest,
        _ => throw new InvalidOperationException($"{from} -> {to} is not one step"),
    };

    /// <summary>Walk a route with the real step check, committing each step the way
    /// the game does; fails on the first step the walker is refused.</summary>
    private static void AssertEveryStepIsAccepted(GameWorld world, WalkCheck walk, Character mover,
        Point3D start, List<Point3D> path)
    {
        world.PlaceCharacter(mover, start);
        foreach (var next in path)
        {
            var here = mover.Position;
            var dir = DirectionBetween(here, next);
            Assert.True(walk.CheckMovement(mover, here, dir, out int z),
                $"the route's step {dir} from {here} to {next} is refused by the step check");
            world.MoveCharacter(mover, new Point3D(next.X, next.Y, (sbyte)z, 0), fireRegionEvents: false);
        }
    }

    /// <summary>A walker asking for the tile next to it used to be told "one step",
    /// whatever stood there: the shortcut for a neighbouring goal never asked the
    /// walk rule. The step was then refused, and the walker asked again - a bot at
    /// Trinsic did that three times a second until it was lifted out.</summary>
    [Fact]
    public void ANeighbouringGoalTheStepCannotReach_IsNotAOneStepRoute()
    {
        var (world, walk, map, mover) = WalkWorld();
        var start = new Point3D(50, 50, 0, 0);
        var goal = new Point3D(51, 50, 0, 0);
        map.AddSyntheticStatic(0, 51, 50, WallTile, 0);
        world.PlaceCharacter(mover, start);

        Assert.False(walk.CheckMovement(mover, start, Direction.East, out _));

        var path = new Pathfinder(world).FindPath(start, goal, 0, self: mover, maxNodes: 400, maxRadius: 12);

        Assert.Null(path);
    }

    [Fact]
    public void ANeighbouringGoalTheStepReaches_IsStillOneStep()
    {
        var (world, walk, _, mover) = WalkWorld();
        var start = new Point3D(50, 50, 0, 0);
        var goal = new Point3D(51, 51, 0, 0);
        world.PlaceCharacter(mover, start);

        var path = new Pathfinder(world).FindPath(start, goal, 0, self: mover, maxNodes: 400, maxRadius: 12);

        Assert.NotNull(path);
        Assert.Equal([goal], path);
        AssertEveryStepIsAccepted(world, walk, mover, start, path);
    }

    /// <summary>A diagonal step needs both tiles beside it (CheckValidMove,
    /// CCharStatus.cpp:1991-2006). The search skipped that test, so its routes cut
    /// corners the walker is refused at. It now asks the step's own question.</summary>
    [Fact]
    public void ARouteDoesNotCutACornerTheStepRefuses()
    {
        var (world, walk, map, mover) = WalkWorld();
        var start = new Point3D(50, 50, 0, 0);
        var goal = new Point3D(51, 51, 0, 0);
        map.AddSyntheticStatic(0, 51, 50, WallTile, 0);     // the tile east of the start
        world.PlaceCharacter(mover, start);

        Assert.False(walk.CheckMovement(mover, start, Direction.SouthEast, out _));

        var path = new Pathfinder(world).FindPath(start, goal, 0, self: mover, maxNodes: 400, maxRadius: 12);

        Assert.NotNull(path);
        Assert.Equal(2, path.Count);
        Assert.Equal(goal.X, path[^1].X);
        Assert.Equal(goal.Y, path[^1].Y);
        AssertEveryStepIsAccepted(world, walk, mover, start, path);
    }

    [Fact]
    public void ARouteRoundAWall_HoldsNoStepTheWalkerIsRefused()
    {
        var (world, walk, map, mover) = WalkWorld();
        // A wall across the way with one gap, and a second, offset wall behind it:
        // the short way through is a staircase of corners.
        for (int y = 40; y <= 60; y++)
            if (y != 47)
                map.AddSyntheticStatic(0, 55, y, WallTile, 0);
        for (int y = 40; y <= 60; y++)
            if (y != 53)
                map.AddSyntheticStatic(0, 58, y, WallTile, 0);
        var start = new Point3D(50, 50, 0, 0);
        var goal = new Point3D(62, 50, 0, 0);
        world.PlaceCharacter(mover, start);

        var path = new Pathfinder(world).FindPath(start, goal, 0, self: mover, maxNodes: 4000, maxRadius: 24);

        Assert.NotNull(path);
        Assert.Equal(goal.X, path[^1].X);
        Assert.Equal(goal.Y, path[^1].Y);
        AssertEveryStepIsAccepted(world, walk, mover, start, path);
    }

    /// <summary>The step's batch form answers each direction as the single-step
    /// check does.</summary>
    [Fact]
    public void TheEightDirectionCheck_AgreesWithTheSingleStepCheck()
    {
        var (world, walk, map, mover) = WalkWorld();
        map.AddSyntheticStatic(0, 51, 50, WallTile, 0);
        map.AddSyntheticStatic(0, 49, 51, WallTile, 0);
        var at = new Point3D(50, 50, 0, 0);
        world.PlaceCharacter(mover, at);

        Span<int> landing = stackalloc int[8];
        int mask = walk.CheckPathSteps(mover, 0, at.X, at.Y, at.Z, landing);

        for (int d = 0; d < 8; d++)
        {
            bool single = walk.CheckMovement(mover, at, (Direction)d, out int z);
            Assert.Equal(single, (mask & (1 << d)) != 0);
            if (single)
                Assert.Equal(z, landing[d]);
        }
        Assert.NotEqual(0, mask);
        Assert.NotEqual(0xFF, mask);
    }

    /// <summary>The text description of the tile ahead is written when a step is
    /// refused, or for a probe - not for every accepted step of every walker.</summary>
    [Fact]
    public void TheWalkDiagnosticDescribesTheTileAhead_OnlyForARefusalOrAProbe()
    {
        var (world, walk, map, mover) = WalkWorld();
        var at = new Point3D(50, 50, 0, 0);
        map.AddSyntheticStatic(0, 51, 50, RugTile, 0);      // east: a rug, walkable
        map.AddSyntheticStatic(0, 50, 51, WallTile, 0);     // south: a wall
        world.PlaceCharacter(mover, at);

        Assert.True(walk.CheckMovementDetailed(mover, at, Direction.East, checkChars: false, out _, out var stepped));
        Assert.Equal(1, stepped.FwdStaticTotal);
        Assert.Equal("", stepped.FwdStaticDump);
        Assert.StartsWith("accepted", stepped.FwdReason);

        Assert.True(walk.CheckMovementDetailed(mover, at, Direction.East, out _, out var probed));
        Assert.Equal(1, probed.FwdStaticTotal);
        Assert.Contains("rug", probed.FwdStaticDump);

        Assert.False(walk.CheckMovementDetailed(mover, at, Direction.South, checkChars: false, out _, out var refused));
        Assert.Equal(1, refused.FwdStaticTotal);
        Assert.Equal(1, refused.FwdImpassableCount);
        Assert.Contains("wall", refused.FwdStaticDump);
        Assert.NotEmpty(WalkCheck.DescribeDiagnostic(refused, Direction.South, at));
    }

    // ------------------------------------------------------------------
    //  The measurement window
    // ------------------------------------------------------------------

    [Fact]
    public void ThePerfWindowReportsWhereAnAverageTickWent_AndHowMuchOfItRan()
    {
        var window = new PerfMeasurementWindow(startTick: 100);
        long[] first = new long[PerfMeasurementWindow.PhaseNames.Length];
        long[] second = new long[PerfMeasurementWindow.PhaseNames.Length];
        first[0] = 4_000; first[2] = 10_000;
        second[0] = 8_000; second[2] = 2_000;
        window.RecordPhases(first);
        window.RecordPhases(second);
        window.RecordTick(40_000); window.RecordTickCpu(40_000, 10_000);    // starved
        window.RecordTick(10_000); window.RecordTickCpu(10_000, 9_000);
        window.RecordTick(30_000); window.RecordTickCpu(30_000, -1);        // host could not say

        var report = window.BuildReport(currentTick: 103, workerCount: 1, multicoreEnabled: true,
            playingClients: 0, chars: 0, items: 0);

        Assert.Equal(PerfMeasurementWindow.PhaseNames, report.Phases.Select(p => p.Name).ToArray());
        var world = report.Phases.Single(p => p.Name == "world_tick");
        Assert.Equal(6.0, world.AvgMs);
        Assert.Equal(8.0, world.MaxMs);
        var commit = report.Phases.Single(p => p.Name == "npc_commit");
        Assert.Equal(6.0, commit.AvgMs);
        Assert.Equal(10.0, commit.MaxMs);

        Assert.Equal(2, report.Tick.CpuSamples);
        Assert.Equal(9.5, report.Tick.AvgCpuMs);
        Assert.Equal(15.5, report.Tick.AvgOffCpuMs);
        Assert.Equal(30.0, report.Tick.MaxOffCpuMs);
        Assert.Equal(1, report.Tick.StarvedTicks);
    }
}
