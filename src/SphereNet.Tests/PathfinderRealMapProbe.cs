using System.Diagnostics;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>Timing probe: the tile search on the real map, over the short hops a
/// walker needs (a road node to a bank's stopping spot). Skips without the muls.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PathfinderRealMapProbe(ITestOutputHelper output)
{
    [Fact]
    public void ShortHopsOnTheRealMap()
    {
        const string mul = @"C:\sphereNetServer\mul";
        if (Gate.Missing(output, "mul tables", !File.Exists(Path.Combine(mul, "tiledata.mul")))) return;

        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);
        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var walker = world.CreateCharacter();
        walker.IsPlayer = true;
        var pf = new Pathfinder(world);

        (string Name, int X1, int Y1, int X2, int Y2)[] hops =
        [
            ("moonglow road9 -> bank spot", 4472, 1180, 4472, 1153),
            ("moonglow road9 -> bank spot2", 4472, 1180, 4465, 1161),
            ("bucs wp1001 -> bank spot", 2718, 2179, 2730, 2182),
            ("bucs wp1001 -> bank spot2", 2718, 2179, 2726, 2188),
            ("britain open ground", 1430, 1680, 1460, 1700),
        ];
        foreach (var h in hops)
        {
            var s = world.Standing.ResolveStandingSurface(walker, 0, h.X1, h.Y1, 0, WalkCheck.StandingPolicy.Settle);
            var g = world.Standing.ResolveStandingSurface(walker, 0, h.X2, h.Y2, 0, WalkCheck.StandingPolicy.Settle);
            var start = new Point3D((short)h.X1, (short)h.Y1, (sbyte)s.Z, 0);
            var goal = new Point3D((short)h.X2, (short)h.Y2, (sbyte)g.Z, 0);
            walker.Position = start;
            pf.FindPath(start, goal, 0, self: walker, maxNodes: 800, maxRadius: 48); // warm
            var sw = Stopwatch.StartNew();
            var path = pf.FindPath(start, goal, 0, self: walker, maxNodes: 800, maxRadius: 48);
            sw.Stop();
            output.WriteLine($"{h.Name}: {(path == null ? "NO PATH" : path.Count + " steps")} " +
                             $"{sw.Elapsed.TotalMilliseconds:F1} ms (startZ {s.Z} found {s.Found}, goalZ {g.Z} found {g.Found})");
        }
    }

    /// <summary>The worst case an NPC meets: a target it cannot reach, so the search
    /// spends its whole node budget, in a sector as crowded as a town.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(600)]
    public void UnreachableSearchInACrowdedSector(int decorations)
    {
        const string mul = @"C:\sphereNetServer\mul";
        if (Gate.Missing(output, "mul tables", !File.Exists(Path.Combine(mul, "tiledata.mul")))) return;
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);
        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var rng = new Random(1);
        // Six hundred small decorations over the Britain bank sector.
        for (int i = 0; i < decorations; i++)
        {
            var deco = world.CreateItem();
            deco.BaseId = 0x0E7F; // a keg: blocks nothing a walker cares about
            world.PlaceItem(deco, new Point3D((short)(1408 + rng.Next(64)), (short)(1664 + rng.Next(64)), 0, 0));
        }
        var walker = world.CreateCharacter();
        walker.IsPlayer = false;
        var pf = new Pathfinder(world);
        var s = world.Standing.ResolveStandingSurface(walker, 0, 1430, 1690, 0, WalkCheck.StandingPolicy.Settle);
        var start = new Point3D(1430, 1690, (sbyte)s.Z, 0);
        walker.Position = start;
        // A goal on open ground that is walled off: the sea west of nowhere would
        // do; a tile far outside the 14-tile box forces the full budget.
        var goal = new Point3D(1430, 1720, 0, 0);
        pf.FindPath(start, goal, 0, self: walker, maxNodes: 500, maxRadius: 14);
        var sw = Stopwatch.StartNew();
        const int runs = 20;
        for (int i = 0; i < runs; i++)
            pf.FindPath(start, goal, 0, self: walker, maxNodes: 500, maxRadius: 14);
        sw.Stop();
        output.WriteLine($"unreachable 500-node search, {decorations} items in sector: {sw.Elapsed.TotalMilliseconds / runs:F2} ms each");
    }

    /// <summary>Where a walk check's time goes, on the real map in a town.</summary>
    [Fact]
    public void WalkCheckCostBreakdown()
    {
        const string mul = @"C:\sphereNetServer\mul";
        if (Gate.Missing(output, "mul tables", !File.Exists(Path.Combine(mul, "tiledata.mul")))) return;
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);
        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var walker = world.CreateCharacter();
        var sx = new SourceXWalk(world);
        const int n = 20000;
        var rng = new Random(2);
        var pts = Enumerable.Range(0, n).Select(_ => (X: 1410 + rng.Next(60), Y: 1660 + rng.Next(60))).ToArray();
        long sum = 0;
        var sw = Stopwatch.StartNew();
        foreach (var (x, y) in pts) { var b = map.GetStaticBlock(0, x, y, out int ox, out int oy); sum += b.Length + ox; }
        output.WriteLine($"GetStaticBlock: {sw.Elapsed.TotalMilliseconds * 1000 / n:F2} us (avg block {sum / n})");
        sw.Restart();
        foreach (var (x, y) in pts) sum += map.GetTerrainTile(0, x, y).TileId;
        output.WriteLine($"GetTerrainTile: {sw.Elapsed.TotalMilliseconds * 1000 / n:F2} us");
        sw.Restart();
        foreach (var (x, y) in pts) sum += sx.ClimbHeightAt(walker, 0, x, y, 0);
        output.WriteLine($"ClimbHeightAt: {sw.Elapsed.TotalMilliseconds * 1000 / n:F2} us");
        sw.Restart();
        foreach (var (x, y) in pts) sum += sx.CanStandAt(walker, 0, x, y, 0, 2, true, out int nz, out string _, out uint _) ? nz : 0;
        output.WriteLine($"CanStandAt: {sw.Elapsed.TotalMilliseconds * 1000 / n:F2} us");
        sw.Restart();
        foreach (var (x, y) in pts) sum += sx.CheckValidMove(walker, 0, x, y, 0, Direction.East, true, out int nz2, out _) ? nz2 : 0;
        output.WriteLine($"CheckValidMove: {sw.Elapsed.TotalMilliseconds * 1000 / n:F2} us");
        sw.Restart();
        foreach (var (x, y) in pts) sum += SphereNet.Game.Definitions.CharDefHelper.GetCanFlags(walker) == 0 ? 1 : 0;
        output.WriteLine($"GetCanFlags: {sw.Elapsed.TotalMilliseconds * 1000 / n:F2} us");
        sw.Restart();
        foreach (var (x, y) in pts) sum += world.FindRegion(new Point3D((short)x, (short)y, 0, 0)) == null ? 1 : 0;
        output.WriteLine($"FindRegion: {sw.Elapsed.TotalMilliseconds * 1000 / n:F2} us {sum}");
    }

    /// <summary>A walk check in town with no houses in the world and with two thousand
    /// spread over the map: the multi area index keeps the far ones out of it.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2000)]
    public void WalkCheckWithManyHousesInTheWorld(int houses)
    {
        const string mul = @"C:\sphereNetServer\mul";
        if (Gate.Missing(output, "mul tables", !File.Exists(Path.Combine(mul, "tiledata.mul")))) return;
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager(mul);
        map.Load();
        map.InitMap(0, 7168, 4096);
        var world = new GameWorld(lf);
        world.InitMap(0, 7168, 4096);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var rng = new Random(3);
        for (int i = 0; i < houses; i++)
        {
            var house = world.CreateItem();
            house.BaseId = 0x0064;
            house.ItemType = SphereNet.Core.Enums.ItemType.Multi;
            world.PlaceItem(house, new Point3D((short)rng.Next(200, 5000), (short)rng.Next(2000, 4000), 0, 0));
        }
        var walker = world.CreateCharacter();
        var sx = new SourceXWalk(world);
        const int n = 20000;
        var pts = Enumerable.Range(0, n).Select(_ => (X: 1410 + rng.Next(60), Y: 1660 + rng.Next(60))).ToArray();
        long sum = 0;
        var sw = Stopwatch.StartNew();
        foreach (var (x, y) in pts) sum += sx.CheckValidMove(walker, 0, x, y, 0, Direction.East, true, out int nz, out _) ? nz : 0;
        output.WriteLine($"CheckValidMove with {houses} houses in the world: {sw.Elapsed.TotalMilliseconds * 1000 / n:F2} us ({sum})");
    }
}
