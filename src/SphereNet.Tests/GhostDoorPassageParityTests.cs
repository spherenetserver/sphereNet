using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A ghost is not stopped by a door (port plan İŞ-30 / PLAN-405).
///
/// Source-X gives every DEAD char CAN_C_GHOST (GetCanMoveFlags, CCharStatus.cpp:739)
/// and that flag clears CAN_I_BLOCK off a door tile (CChar.cpp:760). Only a DOOR:
/// a wall still needs CAN_C_PASSWALLS, which a ghost does not get.
///
/// Nothing here read the dead flag as movement geometry, so a player who died in a
/// room whose door had swung shut stayed shut in - and this engine closes doors on
/// its own after 20 seconds.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class GhostDoorPassageParityTests
{
    private const ushort DoorTile = 0x0675;   // synthetic Impassable door
    private const ushort WallTile = 0x0676;   // synthetic Impassable wall

    private static (GameWorld World, WalkCheck Walker, MapDataManager Map) Setup()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(DoorTile, new ItemTileData
        { Flags = TileFlag.Impassable | TileFlag.Door, Height = 20 });
        map.SetSyntheticItemTile(WallTile, new ItemTileData
        { Flags = TileFlag.Impassable, Height = 20 });

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return (world, new WalkCheck(world), map);
    }

    private static Character Mover(GameWorld world, bool dead)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.Player;   // no GM all-move bypass
        world.PlaceCharacter(ch, new Point3D(50, 50, 0, 0));
        if (dead)
            ch.SetStatFlag(StatFlag.Dead);
        return ch;
    }

    // ---- map statics ---------------------------------------------------

    [Fact]
    public void ALivingCharIsStoppedByAClosedDoorTile()
    {
        var (world, walker, map) = Setup();
        map.AddSyntheticStatic(0, 50, 49, DoorTile, 0);
        var ch = Mover(world, dead: false);

        Assert.False(walker.CheckMovement(ch, ch.Position, Direction.North, out _));
    }

    [Fact]
    public void AGhostWalksThroughTheSameDoorTile()
    {
        var (world, walker, map) = Setup();
        map.AddSyntheticStatic(0, 50, 49, DoorTile, 0);
        var ch = Mover(world, dead: true);

        Assert.True(walker.CheckMovement(ch, ch.Position, Direction.North, out _));
    }

    [Fact]
    public void AGhostIsStillStoppedByAWall()
    {
        // CAN_C_GHOST clears CAN_I_BLOCK off a DOOR only; walls answer to
        // CAN_C_PASSWALLS, which the dead flag does not grant.
        var (world, walker, map) = Setup();
        map.AddSyntheticStatic(0, 50, 49, WallTile, 0);
        var ch = Mover(world, dead: true);

        Assert.False(walker.CheckMovement(ch, ch.Position, Direction.North, out _));
    }

    // ---- door items ----------------------------------------------------

    [Fact]
    public void AGhostWalksThroughAClosedDoorItem()
    {
        var (world, walker, _) = Setup();
        var door = world.CreateItem();
        door.BaseId = WallTile;              // plain impassable art...
        door.ItemType = ItemType.DoorLocked; // ...it is the TYPE that makes it a door
        door.SetAttr(ObjAttributes.Move_Never);
        world.PlaceItem(door, new Point3D(50, 49, 0, 0));

        var alive = Mover(world, dead: false);
        Assert.False(walker.CheckMovement(alive, alive.Position, Direction.North, out _));

        var ghost = Mover(world, dead: true);
        Assert.True(walker.CheckMovement(ghost, ghost.Position, Direction.North, out _));
    }
}
