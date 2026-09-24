using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Walking onto an open plank puts the character ashore at the first spot it
/// can stand on along its facing (CheckLocationEffects -> MoveToValidSpot,
/// CCharAct.cpp:5038/5369). Only double-clicking the plank did anything, so the water
/// between plank and quay could not be crossed.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ShipPlankDisembarkTests
{
    private static (GameWorld World, Character Ch) Setup(bool allWater)
    {
        var map = new SphereNet.MapData.MapDataManager("");
        ushort land = 3;
        if (allWater)
        {
            land = 0xA8;
            map.SetSyntheticLandTile(land, new LandTileData { Flags = TileFlag.Wet | TileFlag.Impassable });
        }
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: land);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Dex = 50; ch.MaxStam = 50; ch.Stam = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    private static Item Plank(GameWorld world)
    {
        var plank = world.CreateItem();
        plank.BaseId = 0x3ED5;
        plank.ItemType = ItemType.ShipPlank;
        world.PlaceItem(plank, new Point3D(101, 100, 0, 0));
        return plank;
    }

    [Fact]
    public void SteppingOntoThePlankLandsOnTheShoreAhead()
    {
        var (world, ch) = Setup(allWater: false);
        Plank(world);
        ch.Direction = Direction.East;

        Assert.True(new MovementEngine(world).TryMove(ch, Direction.East, running: false, sequence: 1));

        Assert.Equal(102, ch.X);
        Assert.Equal(100, ch.Y);
    }

    [Fact]
    public void WithNoShoreInReachTheCharacterStaysOnThePlank()
    {
        var (world, ch) = Setup(allWater: true);
        Plank(world);
        ch.Direction = Direction.East;
        var engine = new MovementEngine(world);

        Assert.False(engine.MoveToValidSpot(ch, Direction.East, 18, 1, out _));
    }
}
