using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// CCMultiMovable::Move (CCMultiMovable.cpp:654-871): a sailing order is checked tile
/// by tile ahead of the hull and applied as ONE MoveDelta - one region check and one
/// movement broadcast per command - and with OF_MapBoundarySailing a hull crossing the
/// map edge comes out at the opposite edge with everything aboard.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ShipMoveSourceXTests
{
    private const ushort HullNorth = 0x4000;
    private const ushort DeckTile = 0x3E40;
    private const ushort WaterLand = 0x00A8;
    private const ushort ReefTile = 0x0FFF;
    private const int MapSize = 64;

    private sealed record Bench(GameWorld World, ShipEngine Engine, Ship Ship, Character Passenger);

    private static Bench Setup(Point3D at)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, MapSize, MapSize, landZ: 0, landTile: WaterLand);
        map.SetSyntheticLandTile(WaterLand, new LandTileData
        { Flags = TileFlag.Impassable | TileFlag.Wet, Name = "water" });
        map.SetSyntheticItemTile(ReefTile, new ItemTileData
        { Flags = TileFlag.Impassable, Height = 5 });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, MapSize, MapSize);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new MultiRegistry();
        foreach (ushort id in new ushort[] { HullNorth, 0x4001, 0x4002, 0x4003 })
        {
            var def = new MultiDef { Id = id, Name = "test ship" };
            for (short dx = -1; dx <= 1; dx++)
                for (short dy = -1; dy <= 1; dy++)
                    def.Components.Add(new MultiComponent
                    { TileId = DeckTile, DeltaX = dx, DeltaY = dy, DeltaZ = 0, Visible = true });
            def.RecalcBounds();
            registry.Register(def);
        }

        var engine = new ShipEngine(world, registry, map);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(30, 30, 0, 0));
        var ship = engine.PlaceShip(owner, HullNorth, at, Direction.North);
        Assert.NotNull(ship);

        // A component (a tiller-like fixture) on the hull's edge moves with it.
        var component = world.CreateItem();
        world.PlaceItem(component, new Point3D((short)(at.X + 1), at.Y, at.Z, at.Map));
        ship!.AddComponent(component);

        var passenger = world.CreateCharacter();
        passenger.IsPlayer = true;
        // Standing on the deck plane (shipZ + 3, ListObjs CCMultiMovable.cpp:142).
        world.PlaceCharacter(passenger, new Point3D(at.X, at.Y, (sbyte)(at.Z + 3), at.Map));
        return new Bench(world, engine, ship!, passenger);
    }

    private static List<(ObjBase Obj, Point3D At)> Snapshot(Bench b) =>
        b.Engine.ListShipObjects(b.Ship).Select(o => (o, o.Position)).ToList();

    private static void AssertAllMovedBy(List<(ObjBase Obj, Point3D At)> before, int dx, int dy)
    {
        foreach (var (obj, at) in before)
        {
            Assert.Equal(at.X + dx, obj.Position.X);
            Assert.Equal(at.Y + dy, obj.Position.Y);
        }
    }

    // --- S02: one delta per order ----------------------------------------

    [Fact]
    public void AThreeTileOrderMovesOnceWithOneBroadcast()
    {
        var b = Setup(new Point3D(20, 30, 0, 0));
        int moved = 0;
        b.Engine.OnShipMoved = _ => moved++;
        var before = Snapshot(b);

        Assert.True(b.Engine.Move(b.Ship, Direction.North, 3));

        Assert.Equal(1, moved);
        AssertAllMovedBy(before, 0, -3);
        Assert.Equal(27, b.Passenger.Y);
    }

    [Fact]
    public void AnObstructedOrderMovesTheReachableDistanceInOneStep()
    {
        var b = Setup(new Point3D(20, 30, 0, 0));
        // The leading edge meets the reef on the third tile: two tiles are clear.
        for (short x = 19; x <= 21; x++)
            b.World.MapData!.AddSyntheticStatic(0, x, 26, ReefTile, 0);
        int moved = 0;
        b.Engine.OnShipMoved = _ => moved++;

        Assert.False(b.Engine.Move(b.Ship, Direction.North, 4));

        Assert.Equal(1, moved);
        Assert.Equal(28, b.Ship.MultiItem.Y);
    }

    [Fact]
    public void ARegionVetoKeepsTheWholeOrderWhereItIs()
    {
        var b = Setup(new Point3D(20, 30, 0, 0));
        var region = new Region { Name = "forbidden", MapIndex = 0 };
        region.AddRect(24, 0, 60, 60);
        b.World.AddRegion(region);
        int asked = 0, moved = 0;
        b.Engine.OnShipRegionChange = (_, _, _) => { asked++; return false; };
        b.Engine.OnShipMoved = _ => moved++;

        // Upstream does not act on MoveDelta's answer: the order completes, unmoved.
        Assert.True(b.Engine.Move(b.Ship, Direction.East, 4));

        Assert.Equal(1, asked);
        Assert.Equal(0, moved);
        Assert.Equal(20, b.Ship.MultiItem.X);
    }

    // --- S01: MapBoundarySailing -----------------------------------------

    [Fact]
    public void WithoutTheOptionTheEdgeStopsTheShip()
    {
        GameClient.ServerOptionFlags &= ~OptionFlags.MapBoundarySailing;
        var b = Setup(new Point3D(1, 30, 0, 0));

        Assert.False(b.Engine.Move(b.Ship, Direction.West, 1));
        Assert.Equal(1, b.Ship.MultiItem.X);
    }

    [Theory]
    // anchor, direction, expected anchor; the 3x3 hull spans anchor-1..anchor+1.
    [InlineData(1, 30, Direction.West, 62, 30)]
    [InlineData(62, 30, Direction.East, 1, 30)]
    [InlineData(30, 1, Direction.North, 30, 62)]
    [InlineData(30, 62, Direction.South, 30, 1)]
    public void WithTheOptionTheHullComesOutAtTheOppositeEdgeWithEverythingAboard(
        int x, int y, Direction dir, int expectX, int expectY)
    {
        GameClient.ServerOptionFlags |= OptionFlags.MapBoundarySailing;
        var b = Setup(new Point3D((short)x, (short)y, 0, 0));
        var before = Snapshot(b);
        Assert.Contains(before, e => ReferenceEquals(e.Obj, b.Passenger));
        Assert.True(before.Count >= 3);   // hull, the component and the passenger
        int moved = 0;
        Point3D? crossedFrom = null;
        b.Engine.OnShipMoved = _ => moved++;
        b.Engine.OnShipCrossedMapBoundary = (_, old) => crossedFrom = old;

        Assert.True(b.Engine.Move(b.Ship, dir, 1));

        Assert.Equal(expectX, b.Ship.MultiItem.X);
        Assert.Equal(expectY, b.Ship.MultiItem.Y);
        AssertAllMovedBy(before, expectX - x, expectY - y);
        Assert.Equal(1, moved);
        Assert.Equal(new Point3D((short)x, (short)y, 0, 0), crossedFrom);
    }

    [Theory]
    // A diagonal order through a corner: upstream wraps ONE axis per step (west,
    // north, east, south), the other axis then fails the edge test, and the wrap
    // already in the delta is still applied (CCMultiMovable.cpp:691-846). Objects
    // whose new point is off the map stay put (MoveDelta :309).
    [InlineData(1, 1, Direction.NorthWest, 63, 1)]
    [InlineData(62, 62, Direction.SouthEast, 0, 62)]
    [InlineData(62, 1, Direction.NorthEast, 62, 63)]
    [InlineData(1, 62, Direction.SouthWest, 63, 62)]
    public void ACornerWrapsOneAxisAndStopsOnTheOther(int x, int y, Direction dir, int expectX, int expectY)
    {
        GameClient.ServerOptionFlags |= OptionFlags.MapBoundarySailing;
        var b = Setup(new Point3D((short)x, (short)y, 0, 0));
        var before = Snapshot(b);
        Point3D? crossedFrom = null;
        b.Engine.OnShipCrossedMapBoundary = (_, old) => crossedFrom = old;

        Assert.False(b.Engine.Move(b.Ship, dir, 1));

        Assert.Equal(expectX, b.Ship.MultiItem.X);
        Assert.Equal(expectY, b.Ship.MultiItem.Y);
        Assert.NotNull(crossedFrom);
        int ddx = expectX - x, ddy = expectY - y;
        foreach (var (obj, at) in before)
        {
            int nx = at.X + ddx, ny = at.Y + ddy;
            bool valid = nx >= 0 && nx < MapSize && ny >= 0 && ny < MapSize;
            Assert.Equal(valid ? nx : at.X, obj.Position.X);
            Assert.Equal(valid ? ny : at.Y, obj.Position.Y);
        }
    }

    [Fact]
    public void AWrapWhoseFarSideIsBlockedStillKeepsTheJump()
    {
        GameClient.ServerOptionFlags |= OptionFlags.MapBoundarySailing;
        var b = Setup(new Point3D(1, 30, 0, 0));
        for (short y = 29; y <= 31; y++)
            b.World.MapData!.AddSyntheticStatic(0, 61, y, ReefTile, 0);
        Point3D? crossedFrom = null;
        b.Engine.OnShipCrossedMapBoundary = (_, old) => crossedFrom = old;

        Assert.False(b.Engine.Move(b.Ship, Direction.West, 1));

        // Upstream keeps the wrap in its delta when the far side is blocked
        // (CCMultiMovable.cpp:841-846): the hull lands on the far edge.
        Assert.Equal(63, b.Ship.MultiItem.X);
        Assert.Equal(new Point3D(1, 30, 0, 0), crossedFrom);
    }

    [Fact]
    public void AnOrderWithinTheMapDoesNotCountAsACrossing()
    {
        GameClient.ServerOptionFlags |= OptionFlags.MapBoundarySailing;
        var b = Setup(new Point3D(20, 30, 0, 0));
        bool crossed = false;
        b.Engine.OnShipCrossedMapBoundary = (_, _) => crossed = true;

        Assert.True(b.Engine.Move(b.Ship, Direction.West, 2));

        Assert.Equal(18, b.Ship.MultiItem.X);
        Assert.False(crossed);
    }
}
