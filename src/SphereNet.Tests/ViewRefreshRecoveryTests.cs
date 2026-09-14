using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A client keeps its refresh request until the view has actually been sent (review
/// finding B12).
///
/// The multicore tick cleared ViewNeedsRefresh BEFORE calling ApplyViewDelta. If Apply
/// or the static-door sync threw, the tick's own handler recovered the NPCs it had
/// consumed and dropped to single-thread mode - but nothing re-armed THIS client's
/// refresh. The delta had been dropped on the floor, and until the player moved or
/// something near them changed, whatever was missing from their screen stayed missing:
/// an item that never appeared, a creature that never went away.
///
/// It is the quiet kind of failure. The server logs a tick error, recovers, and keeps
/// running at full speed; only one player sees a world that is subtly wrong, with
/// nothing on their side to make it right again.
/// </summary>
public sealed class ViewRefreshRecoveryTests
{
    private readonly ITestOutputHelper _out;
    public ViewRefreshRecoveryTests(ITestOutputHelper output) => _out = output;

    private static (GameClient Client, GameWorld World) Playing()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7301);
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        me.MaxHits = 100; me.Hits = 100;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, me);
        return (client, world);
    }

    [Fact]
    public void AFailedApplyLeavesTheRefreshRequestArmed()
    {
        var (client, _) = Playing();
        client.ViewNeedsRefresh = true;

        // A delta the apply cannot survive. Which exception it is does not matter -
        // the claim is about what happens to the FLAG when one escapes.
        var delta = new ClientViewDelta();
        delta.NewChars.Add((null!, false));

        Assert.ThrowsAny<Exception>(() =>
            SphereNet.Server.Program.SendViewAndConsumeRefresh(client, delta));

        _out.WriteLine($"after a failed apply: ViewNeedsRefresh={client.ViewNeedsRefresh}");

        // Still armed, so the next tick rebuilds and resends. Cleared, the player was
        // left with a half-updated screen and no event coming to fix it.
        Assert.True(client.ViewNeedsRefresh);
    }

    [Fact]
    public void TheFailureIsNotSwallowed()
    {
        var (client, _) = Playing();
        client.ViewNeedsRefresh = true;
        var delta = new ClientViewDelta();
        delta.NewChars.Add((null!, false));

        // The tick's own handler decides whether to abandon the tick and fall back to
        // single-thread mode. Catching it here would hide a failing Apply behind a view
        // that merely looks slow.
        Assert.ThrowsAny<Exception>(() =>
            SphereNet.Server.Program.SendViewAndConsumeRefresh(client, delta));
    }

    [Fact]
    public void ASuccessfulApplyConsumesTheRequest()
    {
        var (client, world) = Playing();
        client.ViewNeedsRefresh = true;

        var other = world.CreateCharacter();
        other.BaseId = 0x0190;
        other.MaxHits = 50; other.Hits = 50;
        world.PlaceCharacter(other, new Point3D(101, 100, 0, 0));

        var delta = new ClientViewDelta();
        delta.NewChars.Add((other, false));

        SphereNet.Server.Program.SendViewAndConsumeRefresh(client, delta);

        _out.WriteLine($"after a successful apply: ViewNeedsRefresh={client.ViewNeedsRefresh}");

        // The control: keeping the flag on failure must not keep it on success, or
        // every client would rebuild its whole view every tick forever.
        Assert.False(client.ViewNeedsRefresh);
    }

    [Fact]
    public void AnEmptyDeltaStillConsumesTheRequest()
    {
        var (client, _) = Playing();
        client.ViewNeedsRefresh = true;

        SphereNet.Server.Program.SendViewAndConsumeRefresh(client, new ClientViewDelta());

        // "Nothing changed near you" is a real answer and a completed one.
        Assert.False(client.ViewNeedsRefresh);
    }
}
