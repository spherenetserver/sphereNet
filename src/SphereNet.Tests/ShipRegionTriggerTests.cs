using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

// Verifies the @RegionLeave/@RegionEnter fire site for movable multis
// (Source-X CCMultiMovable): ShipEngine.MoveDelta consults the gated
// OnShipRegionChange hook BEFORE anything moves when the multi anchor
// crosses a region boundary; returning false (script RETURN 1) blocks the
// whole step. Engine wiring routes the hook into FireItemTrigger with
// ARGO = region and SRC = pilot — these tests drive the hook directly,
// the same way ShipRedeedTriggerTests covers the other ship hooks.
public class ShipRegionTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static (ShipEngine engine, Ship ship) MakeShip(GameWorld world, Point3D pos)
    {
        var multi = world.CreateItem();
        multi.BaseId = 0x4000;
        world.PlaceItem(multi, pos);
        var engine = new ShipEngine(world, new MultiRegistry(), null);
        return (engine, new Ship(multi));
    }

    private static Region MakeRegion(GameWorld world, string name, short x1, short y1, short x2, short y2)
    {
        var region = new Region { Name = name, MapIndex = 0 };
        region.AddRect(x1, y1, x2, y2);
        world.AddRegion(region);
        return region;
    }

    // Region rects in these tests start at x=112 (8-aligned): FindRegion
    // caches lookups per 8x8 cell, so a boundary inside a cell resolves at
    // cell granularity — the same precision characters get. An aligned edge
    // keeps the assertions exact.
    [Fact]
    public void MoveDelta_CrossingBoundary_FiresHookWithOldAndNewRegion()
    {
        var world = CreateWorld();
        var region = MakeRegion(world, "harbor", 112, 0, 200, 200);
        var (engine, ship) = MakeShip(world, new Point3D(111, 100, 0, 0));

        Region? seenOld = new Region();
        Region? seenNew = null;
        engine.OnShipRegionChange = (_, oldR, newR) => { seenOld = oldR; seenNew = newR; return true; };

        Assert.True(engine.MoveDelta(ship, 1, 0, 0));

        Assert.Null(seenOld);                      // left wilderness
        Assert.Same(region, seenNew);              // entered the region
        Assert.Equal(112, ship.MultiItem.X);       // move applied
    }

    [Fact]
    public void MoveDelta_HookReturnsFalse_BlocksTheStep()
    {
        var world = CreateWorld();
        MakeRegion(world, "forbidden", 112, 0, 200, 200);
        var (engine, ship) = MakeShip(world, new Point3D(111, 100, 0, 0));
        engine.OnShipRegionChange = (_, _, _) => false; // script RETURN 1

        Assert.False(engine.MoveDelta(ship, 1, 0, 0));

        Assert.Equal(111, ship.MultiItem.X); // nothing moved
    }

    [Fact]
    public void MoveDelta_NoBoundaryCross_DoesNotFireHook()
    {
        var world = CreateWorld();
        MakeRegion(world, "harbor", 105, 0, 200, 200);
        var (engine, ship) = MakeShip(world, new Point3D(50, 100, 0, 0));
        int fires = 0;
        engine.OnShipRegionChange = (_, _, _) => { fires++; return true; };

        Assert.True(engine.MoveDelta(ship, 1, 0, 0)); // wilderness → wilderness

        Assert.Equal(0, fires);
        Assert.Equal(51, ship.MultiItem.X);
    }
}
