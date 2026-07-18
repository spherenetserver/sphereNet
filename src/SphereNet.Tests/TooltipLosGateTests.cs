using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Network;

namespace SphereNet.Tests;

/// <summary>
/// V3 (perf/parity): the AOS tooltip send is gated on distance + visibility only.
/// Source-X never raycasts LOS in the tooltip paths (CClientMsg_AOSTooltip.cpp:55
/// filters by GetDistSight vs visual range; the 0xD6 request handler checks
/// CanSee, receive.cpp:3628) — the client draws the object through walls, so the
/// tooltip must arrive too, and view-apply must not pay a per-object raycast.
/// </summary>
public sealed class TooltipLosGateTests
{
    private const ushort WallGraphic = 0x0080;

    private static GameWorld MakeWorldWithWallTile()
    {
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(WallGraphic, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 20, Name = "wall" });

        var world = new GameWorld(TestHarness.CreateLoggerFactory());
        world.InitMap(0, 512, 512);
        world.MapData = md;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void Tooltip_SentForItemBehindWall_NoLosRaycastGate()
    {
        using var loggerFactory = TestHarness.CreateLoggerFactory();
        var world = MakeWorldWithWallTile();
        world.ToolTipMode = 1;

        var state = TestHarness.CreateActiveNetState(loggerFactory, Random.Shared.Next(20_000, 30_000));
        state.ClientVersionNumber = 70_020_000;
        var client = new GameClient(state, world, new AccountManager(loggerFactory),
            loggerFactory.CreateLogger<GameClient>());
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);

        // Wall on the midpoint tile breaks the ray between player and item.
        var wall = world.CreateItem();
        wall.BaseId = WallGraphic;
        world.PlaceItem(wall, new Point3D(103, 100, 0, 0));

        var item = world.CreateItem();
        item.Name = "behind the wall";
        world.PlaceItem(item, new Point3D(106, 100, 0, 0));

        Assert.False(world.CanSeeLOS(player.Position, item.Position));

        client.SendAosTooltip(item, requested: false);
        var opcodes = TestHarness.GetQueuedPackets(state).Select(p => p.Span[0]).ToList();
        Assert.Contains(opcodes, op => op is 0xD6 or 0xDC);
    }
}
