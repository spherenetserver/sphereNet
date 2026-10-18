using SphereNet.Core.Types;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>GameWorld.FindRoom answers through a cell index of the rooms; it must
/// return what a scan of the room list returns (the first room that contains the
/// point), and follow rects added after the index was built.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class RoomIndexTests
{
    [Fact]
    public void TheIndexAgreesWithAScanOfTheRoomList()
    {
        var world = TestHarness.CreateWorld();
        var rng = new Random(11);
        for (int i = 0; i < 300; i++)
        {
            var room = new Room { MapIndex = (byte)(i % 7 == 0 ? 1 : 0) };
            int rects = rng.Next(1, 4);
            for (int r = 0; r < rects; r++)
            {
                short x = (short)rng.Next(0, 900), y = (short)rng.Next(0, 900);
                room.AddRect(x, y, (short)(x + rng.Next(0, 90)), (short)(y + rng.Next(0, 90)));
            }
            world.AddRoom(room);
        }
        for (int i = 0; i < 20000; i++)
        {
            var pt = new Point3D((short)rng.Next(-5, 1000), (short)rng.Next(-5, 1000), 0, (byte)(i % 5 == 0 ? 1 : 0));
            Assert.Same(world.FindRoomUncached(pt), world.FindRoom(pt));
        }
    }

    [Fact]
    public void ARectAddedLaterIsFound()
    {
        var world = TestHarness.CreateWorld();
        var room = new Room { MapIndex = 0 };
        room.AddRect(10, 10, 20, 20);
        world.AddRoom(room);
        var far = new Point3D(500, 500, 0, 0);
        Assert.Null(world.FindRoom(far));     // builds the index

        room.AddRect(490, 490, 510, 510);
        Assert.Same(room, world.FindRoom(far));
    }
}
