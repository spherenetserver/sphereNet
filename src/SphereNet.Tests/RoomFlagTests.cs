using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;

namespace SphereNet.Tests;

/// <summary>
/// A ROOMDEF's FLAGS line, and the one flag a room gets a say in.
///
/// The map pack writes FLAGS on 1703 of its 1828 room definitions and the loader
/// read four keys, none of them that one. Most of what those lines carry -
/// guarded, nobuilding, underground - is the containing AREA's business upstream
/// too, so dropping them changed nothing. REGION_FLAG_INSTA_LOGOUT is the
/// exception: CanInstantLogOut asks the area and then the room, and its own comment
/// says so - "Allows Room flag to work!" (CClient.cpp:159). A room declaring it
/// could not grant it here, because a Room had no flags at all.
/// </summary>
public sealed class RoomFlagTests
{
    private static Room RoomWith(RegionFlag flags)
    {
        var room = new Room { Name = "probe", MapIndex = 0, Flags = flags };
        room.AddRect(90, 90, 110, 110);
        return room;
    }

    [Fact]
    public void ARoomAnswersForTheFlagsItWasGiven()
    {
        var room = RoomWith(RegionFlag.InstaLogout | RegionFlag.NoBuild);

        Assert.True(room.IsFlag(RegionFlag.InstaLogout));
        Assert.True(room.IsFlag(RegionFlag.NoBuild));
        Assert.False(room.IsFlag(RegionFlag.Guarded));
    }

    [Fact]
    public void ARoomWithNoFlagsAnswersForNone()
    {
        var room = RoomWith(RegionFlag.None);

        Assert.False(room.IsFlag(RegionFlag.InstaLogout));
        Assert.Equal(RegionFlag.None, room.Flags);
    }

    /// <summary>And the world finds it by point, which is how the logout check
    /// reaches it.</summary>
    [Fact]
    public void TheWorldFindsTheRoomStoodIn()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.AddRoom(RoomWith(RegionFlag.InstaLogout));

        var found = world.FindRoom(new Point3D(100, 100, 0, 0));
        Assert.NotNull(found);
        Assert.True(found!.IsFlag(RegionFlag.InstaLogout));

        // Outside the rectangle there is no room, and so no instant logout.
        Assert.Null(world.FindRoom(new Point3D(200, 200, 0, 0)));
    }
}
