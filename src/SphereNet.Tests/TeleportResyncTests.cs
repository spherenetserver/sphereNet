using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field report: a help-menu "stuck" teleported the player from an item @Timer
/// (TOPOBJ.GO). The server moved the character - region change, the new area's
/// items all went out - but the player's own client was never told where it now
/// stood (no 0x20), so it stayed where it was. A teleport nobody resynced leaves a
/// pending resync that the next view update performs.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TeleportResyncTests
{
    [Fact]
    public void APendingResyncSendsThePlayerItsNewPosition()
    {
        var logs = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19581);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x190;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        ch.MoveTo(new Point3D(1500, 1500, 0, 0));      // a script GO: no client handler
        TestHarness.ClearQueuedPackets(client.NetState);
        client.ResyncPending = true;                    // what OnCharacterMoved marks

        client.UpdateClientView();

        Assert.False(client.ResyncPending);
        Assert.Contains(TestHarness.GetQueuedPackets(client.NetState),
            p => p.Span.Length > 0 && p.Span[0] == 0x20);   // draw-player at the new spot
    }

    [Fact]
    public void ADirectResyncClearsThePendingOne()
    {
        var logs = LoggerFactory.Create(_ => { });
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 19582);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(1000, 1000, 0, 0));
        TestHarness.AttachCharacter(client, ch);

        client.ResyncPending = true;
        client.Resync();                                // a handler that resyncs itself
        Assert.False(client.ResyncPending);             // so the view update will not repeat it
    }
}
