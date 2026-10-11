using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;

namespace SphereNet.Tests;

/// <summary>The sector-style region index must answer exactly what a full scan over
/// every region answers: the smallest containing region, the earliest registered
/// among equal areas, ships tracked as they move.</summary>
public class RegionIndexTests
{
    private static Region AddRegion(GameWorld world, string name, byte map, params (int X1, int Y1, int X2, int Y2)[] rects)
    {
        var region = new Region { Name = name, MapIndex = map };
        foreach (var r in rects)
            region.AddRect((short)r.X1, (short)r.Y1, (short)r.X2, (short)r.Y2);
        world.AddRegion(region);
        return region;
    }

    [Fact]
    public void Index_MatchesFullScan_OnRandomOverlappingRegions()
    {
        var world = TestHarness.CreateWorld();
        var rng = new Random(7);
        AddRegion(world, "continent", 0, (0, 0, 6143, 4095));
        AddRegion(world, "continent_twin", 0, (0, 0, 6143, 4095)); // equal area: earlier wins
        for (int i = 0; i < 300; i++)
        {
            byte map = (byte)rng.Next(0, 3);
            int rectCount = rng.Next(1, 4);
            var rects = new (int, int, int, int)[rectCount];
            for (int r = 0; r < rectCount; r++)
            {
                int x = rng.Next(0, 6000), y = rng.Next(0, 4000);
                int w = rng.Next(0, 400), h = rng.Next(0, 400);
                rects[r] = (x, y, x + w, y + h);
            }
            AddRegion(world, $"r{i}", map, rects);
        }
        // Equal-area pair registered in order: the first must win both ways.
        AddRegion(world, "same_a", 0, (1000, 1000, 1009, 1009));
        AddRegion(world, "same_b", 0, (1000, 1000, 1009, 1009));
        var ship = AddRegion(world, "ship", 0, (1003, 1003, 1005, 1005));
        ship.Flags |= RegionFlag.Ship;
        world.InvalidateRegionCache();

        for (int i = 0; i < 20_000; i++)
        {
            var pt = new Point3D((short)rng.Next(-10, 6300), (short)rng.Next(-10, 4200), 0, (byte)rng.Next(0, 4));
            Assert.Same(world.FindRegionUncached(pt), world.FindRegion(pt));
        }
        Assert.Equal("same_a", world.FindRegion(new Point3D(1001, 1001, 0, 0))?.Name);
        Assert.Equal("ship", world.FindRegion(new Point3D(1004, 1004, 0, 0))?.Name);
    }

    [Fact]
    public void ShipRegion_FollowsItsFootprint_WithoutRebuild()
    {
        var world = TestHarness.CreateWorld();
        AddRegion(world, "sea", 0, (0, 0, 2000, 2000));
        var ship = new Region { Name = "ship", MapIndex = 0, Flags = RegionFlag.Ship };
        ship.AddRect(100, 100, 104, 104);
        world.AddRegion(ship);

        Assert.Same(ship, world.FindRegion(new Point3D(102, 102, 0, 0)));

        ship.SetFootprint(500, 500, 504, 504);
        Assert.Equal("sea", world.FindRegion(new Point3D(102, 102, 0, 0))?.Name);
        Assert.Same(ship, world.FindRegion(new Point3D(502, 502, 0, 0)));
    }

    [Fact]
    public void AddAndRemove_RebuildTheIndex()
    {
        var world = TestHarness.CreateWorld();
        AddRegion(world, "town", 0, (0, 0, 500, 500));
        var pt = new Point3D(50, 50, 0, 0);
        Assert.Equal("town", world.FindRegion(pt)?.Name);

        var house = AddRegion(world, "house", 0, (48, 48, 52, 52));
        Assert.Same(house, world.FindRegion(pt));

        Assert.True(world.RemoveRegion(house.Uid));
        Assert.Equal("town", world.FindRegion(pt)?.Name);
    }
}
