using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// Field bug (2026-07-18): double-clicking an NPC while standing near a map
/// STATIC door toggled that door (open/close sound included) and rebroadcast
/// the door art under the NPC's serial — the mobile "vanished" client-side
/// until the next view refresh. The static-door fallback must only run when
/// the clicked uid resolves to neither an item nor a character, and must never
/// reuse a non-synthetic serial for the door art broadcast.
/// </summary>
public sealed class StaticDoorDclickHijackTests
{
    private const ushort ClosedDoorTile = 0x06A5; // wooden door slot 0, closed leaf

    private static (GameWorld World, GameClient Client, NetState State,
        SphereNet.Game.Objects.Characters.Character Player) MakeWorldWithDoor(
        ILoggerFactory loggerFactory)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        map.SetSyntheticItemTile(ClosedDoorTile, new ItemTileData { Flags = TileFlag.Door });
        // Door static 1 tile east of the player — inside the 2-tile search radius.
        map.AddSyntheticStatic(0, 101, 100, ClosedDoorTile, 0);

        var world = new GameWorld(TestHarness.CreateLoggerFactory());
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(20_000, 30_000));
        state.ClientVersionNumber = 70_020_000;
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = PrivLevel.GM; // staff pack-inspection branch for non-human NPCs
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        return (world, client, state, player);
    }

    [Fact]
    public void DclickNpcNearStaticDoor_DoesNotToggleDoor_OpensPackInstead()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var (world, client, state, _) = MakeWorldWithDoor(loggerFactory);

        var cow = world.CreateCharacter();
        cow.BodyId = 0x00D8; // cow — non-human, non-mountable
        cow.Hits = cow.MaxHits = 30;
        world.PlaceCharacter(cow, new Point3D(100, 101, 0, 0));

        client.HandleDoubleClick(cow.Uid.Value);

        // The nearby static door must stay closed — the char dclick may not
        // fall through into the static-door toggle.
        Assert.False(world.IsMapStaticDoorOpen(0, 101, 100, 0));

        var packets = TestHarness.GetQueuedPackets(state).ToList();
        // No delete for the mobile, no world-item re-type of its serial.
        Assert.DoesNotContain(packets, p => p.Span[0] == 0x1D);
        // GM dclick on a non-human NPC opens its pack (0x24 container).
        Assert.Contains(packets, p => p.Span[0] == 0x24);
    }

    [Fact]
    public void DclickSyntheticDoorSerial_StillTogglesDoor()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var (world, client, _, _) = MakeWorldWithDoor(loggerFactory);

        uint doorSerial = (uint)(Serial.ItemFlag |
            (uint)((101 & 0x7FFF) << 16) | (uint)((100 & 0x3FFF) << 3) | 0);
        client.HandleDoubleClick(doorSerial);

        Assert.True(world.IsMapStaticDoorOpen(0, 101, 100, 0));
    }
}
