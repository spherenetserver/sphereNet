using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Field bug (2026-07-19): ".cast fires at myself without ever showing a
/// cursor". A target response that does not echo the armed 0x6C cursor id can
/// never be a legit answer — ClassicUO zeroes its stored cursor id after any
/// server-side cancel (issue #1373), so a click/cancel fired against that dead
/// session arrives with id 0 and used to be consumed as a live pick (the old
/// guard only swallowed it inside a 2s replacement window).
/// </summary>
[Collection("VendorStateSerial")]
public sealed class TargetCursorSessionTests
{
    private static (GameWorld World, SphereNet.Game.Clients.GameClient Client, Character Player) CreateClientWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(50, 50, 0, 0));
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new AccountManager(loggerFactory), 2604);
        TestHarness.AttachCharacter(client, player);
        return (world, client, player);
    }

    [Fact]
    public void StaleIdZeroResponse_IsDropped_AndTheRealAnswerStillLands()
    {
        var (_, client, player) = CreateClientWorld();

        uint pickedSerial = 0;
        int fired = 0;
        client.SetPendingTarget((serial, x, y, z, gfx) => { fired++; pickedSerial = serial; });
        uint armedId = client.ActiveTargetCursorId;
        Assert.NotEqual(0u, armedId);

        // Stale click against a dead session (id 0) — must be ignored, not
        // consumed as a live self/ground pick.
        client.HandleTargetResponse(0, 0, player.Uid.Value, 50, 50, 0, 0);
        Assert.Equal(0, fired);
        Assert.Equal(armedId, client.ActiveTargetCursorId);

        // A response echoing a different (replaced) cursor id is ignored too.
        client.HandleTargetResponse(0, armedId ^ 0x5A5A5A5A, player.Uid.Value, 50, 50, 0, 0);
        Assert.Equal(0, fired);

        // The genuine answer (echoing the armed id) is delivered.
        client.HandleTargetResponse(0, armedId, player.Uid.Value, 50, 50, 0, 0);
        Assert.Equal(1, fired);
        Assert.Equal(player.Uid.Value, pickedSerial);
    }
}
