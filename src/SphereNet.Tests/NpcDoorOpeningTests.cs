using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// NPC door opening (reference parity: NPCs open adjacent unlocked doors and
/// walk through). Covers the pure door state flip and the blocked-tile door
/// lookup the AI uses before re-validating a blocked step.
/// </summary>
public class NpcDoorOpeningTests
{
    private static GameWorld CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        return world;
    }

    [Fact]
    public void TryOpenDoorState_ClosedDoor_FlipsArtAndTags()
    {
        var world = CreateWorld();
        var door = world.CreateItem();
        door.BaseId = 0x06A5;
        door.ItemType = ItemType.Door;
        world.PlaceItem(door, new Point3D(1000, 1000, 0, 0));

        Assert.True(DoorHelper.TryOpenDoorState(door));
        Assert.Equal(0x06A6, door.BaseId);
        Assert.True(door.TryGetTag("DOOR_OPEN", out string? open) && open == "1");
    }

    [Fact]
    public void TryOpenDoorState_ClassicDoor_ShiftsAroundTheHinge()
    {
        var world = CreateWorld();
        var door = world.CreateItem();
        door.BaseId = 0x06A5; // wooden_1 slot 0 → open shift (-1,+1)
        door.ItemType = ItemType.Door;
        world.PlaceItem(door, new Point3D(1000, 1000, 0, 0));

        Assert.True(DoorHelper.TryOpenDoorState(door));
        // Source-X Use_Door: the leaf swings — art +1 AND position moves.
        Assert.Equal(0x06A6, door.BaseId);
        Assert.Equal(999, door.X);
        Assert.Equal(1001, door.Y);
    }

    [Fact]
    public void GetDoorDir_MatchesTheSourceXTable()
    {
        Assert.Equal(0, DoorHelper.GetDoorDir(0x06A5));  // wooden_1 closed CCW
        Assert.Equal(1, DoorHelper.GetDoorDir(0x06A6));  // its open art
        Assert.Equal(15, DoorHelper.GetDoorDir(0x06B4)); // last slot of the set
        Assert.Equal(0, DoorHelper.GetDoorDir(0x190E));  // bar door anomaly
        Assert.Equal(1, DoorHelper.GetDoorDir(0x190F));
        Assert.Equal(-1, DoorHelper.GetDoorDir(0x0001)); // not a door
        // Odd slots are the exact negation of their even pair.
        for (int d = 0; d < 16; d += 2)
        {
            var (ox, oy) = DoorHelper.GetDoorShift(d);
            var (cx, cy) = DoorHelper.GetDoorShift(d + 1);
            Assert.Equal((-ox, -oy), ((short, short))(cx, cy));
        }
    }

    [Fact]
    public void TryOpenDoorState_Portcullis_UsesOffsetTwo()
    {
        var world = CreateWorld();
        var door = world.CreateItem();
        door.BaseId = 0x06F5;
        door.ItemType = ItemType.Portculis;
        world.PlaceItem(door, new Point3D(1000, 1000, 0, 0));

        Assert.True(DoorHelper.TryOpenDoorState(door));
        Assert.Equal(0x06F7, door.BaseId);
    }

    [Fact]
    public void TryOpenDoorState_LockedDoor_Refuses()
    {
        var world = CreateWorld();
        var door = world.CreateItem();
        door.BaseId = 0x06A5;
        door.ItemType = ItemType.DoorLocked;
        world.PlaceItem(door, new Point3D(1000, 1000, 0, 0));

        Assert.False(DoorHelper.TryOpenDoorState(door));
        Assert.Equal(0x06A5, door.BaseId);
        Assert.False(door.TryGetTag("DOOR_OPEN", out _));
    }

    [Fact]
    public void TryOpenDoorState_AlreadyOpen_IsSuccessNoOp()
    {
        var world = CreateWorld();
        var door = world.CreateItem();
        door.BaseId = 0x06A6;
        door.ItemType = ItemType.Door;
        door.SetTag("DOOR_OPEN", "1");
        world.PlaceItem(door, new Point3D(1000, 1000, 0, 0));

        Assert.True(DoorHelper.TryOpenDoorState(door));
        Assert.Equal(0x06A6, door.BaseId);
    }

    [Fact]
    public void FindClosedDoorAt_FindsClosedSkipsLockedAndOpen()
    {
        var world = CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var pos = new Point3D(1000, 1000, 0, 0);

        var locked = world.CreateItem();
        locked.BaseId = 0x06A5;
        locked.ItemType = ItemType.DoorLocked;
        world.PlaceItem(locked, pos);
        Assert.Null(ai.FindClosedDoorAt(pos));

        var opened = world.CreateItem();
        opened.BaseId = 0x06A6;
        opened.ItemType = ItemType.Door;
        opened.SetTag("DOOR_OPEN", "1");
        world.PlaceItem(opened, pos);
        Assert.Null(ai.FindClosedDoorAt(pos));

        var closed = world.CreateItem();
        closed.BaseId = 0x06A5;
        closed.ItemType = ItemType.Door;
        world.PlaceItem(closed, pos);
        Assert.Same(closed, ai.FindClosedDoorAt(pos));
    }
}
