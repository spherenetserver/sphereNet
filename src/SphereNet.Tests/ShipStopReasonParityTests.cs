using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Ships;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Why a ship stopped, said out loud (port plan İŞ-36 / PLAN-504).
///
/// Source-X tracks the reason and has the tiller speak it (CCMultiMovable.cpp:
/// 852-860): the edge of the world is DEFMSG_TILLER_TURB_WATER, anything else is
/// DEFMSG_TILLER_STOPPED. Both lines already existed here as message keys that
/// nothing ever spoke - a ship simply went quiet and the crew was left guessing.
///
/// The tiles a blocked order DID cover stay covered: the reference advances its
/// delta per tile and applies whatever it managed (:841).
/// </summary>
public sealed class ShipStopReasonParityTests
{
    private const ushort HullNorth = 0x4000;
    private const ushort DeckTile = 0x3E40;
    private const ushort WaterLand = 0x00A8;
    private const ushort ReefTile = 0x0FFF;

    private static (GameWorld World, ShipEngine Engine, Ship Ship, List<string> Spoken) Setup(
        int mapW = 64, int mapH = 64, Point3D? at = null)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, mapW, mapH, landZ: 0, landTile: WaterLand);
        map.SetSyntheticLandTile(WaterLand, new LandTileData
        { Flags = TileFlag.Impassable | TileFlag.Wet, Name = "water" });
        // A dry, impassable static near the water plane is how a reef/dock blocks a
        // hull (CanSailInto): impassable and NOT wet.
        map.SetSyntheticItemTile(ReefTile, new ItemTileData
        { Flags = TileFlag.Impassable, Height = 5 });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, mapW, mapH);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
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
        var spoken = new List<string>();
        engine.OnTillerSpeak = (_, line) => spoken.Add(line);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(5, 5, 0, 0));

        var ship = engine.PlaceShip(owner, HullNorth, at ?? new Point3D(20, 20, 0, 0), Direction.North);
        Assert.NotNull(ship);
        spoken.Clear();                 // placement chatter is not what we measure
        return (world, engine, ship!, spoken);
    }

    [Fact]
    public void SailingOffTheEdgeIsTurbulentWater()
    {
        // Anchored two tiles from the west bound: the hull's own -1 edge means the
        // second step would put it off the map.
        var (_, engine, ship, spoken) = Setup(at: new Point3D(2, 20, 0, 0));

        bool moved = engine.Move(ship, Direction.West, 4);

        Assert.False(moved);
        Assert.Single(spoken);
        Assert.Equal(SphereNet.Game.Messages.ServerMessages.Get(
            SphereNet.Game.Messages.Msg.TillerTurbWater), spoken[0]);
    }

    [Fact]
    public void SomethingInTheWayIsADifferentLine()
    {
        var (world, engine, ship, spoken) = Setup();
        // A reef straight ahead, well inside the map.
        for (short x = 19; x <= 21; x++)
            world.MapData!.AddSyntheticStatic(0, x, 16, ReefTile, 0);

        bool moved = engine.Move(ship, Direction.North, 4);

        Assert.False(moved);
        Assert.Single(spoken);
        Assert.Equal(SphereNet.Game.Messages.ServerMessages.Get(
            SphereNet.Game.Messages.Msg.TillerStopped), spoken[0]);
        // ...and the two lines are not the same line.
        Assert.NotEqual(SphereNet.Game.Messages.ServerMessages.Get(
            SphereNet.Game.Messages.Msg.TillerTurbWater), spoken[0]);
    }

    [Fact]
    public void TheTilesItDidCoverStayCovered()
    {
        var (world, engine, ship, _) = Setup();
        for (short x = 19; x <= 21; x++)
            world.MapData!.AddSyntheticStatic(0, x, 16, ReefTile, 0);
        short startY = ship.MultiItem.Y;

        Assert.False(engine.Move(ship, Direction.North, 4));

        // It sailed north until the reef stopped it, and stayed where it got to.
        Assert.True(ship.MultiItem.Y < startY);
        Assert.Equal(18, ship.MultiItem.Y);
    }

    [Fact]
    public void AClearRunSaysNothing()
    {
        var (_, engine, ship, spoken) = Setup();

        Assert.True(engine.Move(ship, Direction.North, 2));

        Assert.Empty(spoken);
    }
}
