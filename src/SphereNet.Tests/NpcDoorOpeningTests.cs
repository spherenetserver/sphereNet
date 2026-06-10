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
