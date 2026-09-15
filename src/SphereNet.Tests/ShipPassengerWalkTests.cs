using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A passenger standing still on a moving deck.
///
/// The smooth-move packet (0xF6) carries the hull AND everything on it, so on each
/// receiving client the passengers are already at the new tile when it arrives.
/// Upstream does not then also send a per-character move for them: addCharMove is the
/// branch for clients that do NOT get the smooth-move packet
/// (CCMultiMovable.cpp:375).
///
/// The view delta here decides by comparing each known mobile against the position it
/// last told the client about. A ship step moves every passenger a tile without that
/// comparison knowing why, so it sent a 0x77 for each of them, once per step - and a
/// 0x77 with a direction is a walk on screen. From the deck it looked like the other
/// player jogging on the spot for the whole voyage.
///
/// What fixes it is telling each viewer where the passenger now is, which is exactly
/// what the packet already did. These pin that mechanism.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ShipPassengerWalkTests
{
    private readonly ITestOutputHelper _out;
    public ShipPassengerWalkTests(ITestOutputHelper output) => _out = output;

    private const byte MobileMoving = 0x77;

    private static GameWorld NewWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(lf);
        world.InitMap(0, 512, 512);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>A client the view pipeline will actually run for: BuildViewDelta
    /// returns null unless the client is in the playing state.</summary>
    private static GameClient Viewer(GameWorld world, int port, out Character me)
    {
        var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, port);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        me = Player(world, 100, 100);
        TestHarness.AttachCharacter(client, me);
        return client;
    }

    private static Character Player(GameWorld world, short x, short y)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.IsOnline = true;     // an offline body is not drawn for an ordinary viewer
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    private static int CountMoves(GameClient client, uint uid)
    {
        int n = 0;
        foreach (var p in TestHarness.GetQueuedPackets(client.NetState))
        {
            var s = p.Span;
            if (s.Length < 5 || s[0] != MobileMoving) continue;
            uint serial = (uint)((s[1] << 24) | (s[2] << 16) | (s[3] << 8) | s[4]);
            if (serial == uid) n++;
        }
        return n;
    }

    private static void Tick(GameClient client)
    {
        client.ViewNeedsRefresh = true;
        var delta = client.BuildViewDelta();
        if (delta != null)
            client.ApplyViewDelta(delta);
    }

    [Fact]
    public void APassengerCarriedByThePacketIsNotAlsoWalkedAcrossTheDeck()
    {
        var world = NewWorld();
        var viewerClient = Viewer(world, 16410, out _);
        var passenger = Player(world, 101, 100);

        Tick(viewerClient);
        int baseline = CountMoves(viewerClient, passenger.Uid.Value);

        // The ship step: the passenger is carried a tile, and the smooth-move packet
        // has already told this viewer about it.
        world.MoveCharacter(passenger, new Point3D(102, 100, 0, 0));
        viewerClient.UpdateKnownCharPosition(passenger);
        Tick(viewerClient);

        int after = CountMoves(viewerClient, passenger.Uid.Value);
        _out.WriteLine($"moves sent for the passenger: {baseline} -> {after}");
        Assert.Equal(baseline, after);
    }

    [Fact]
    public void AnOrdinaryStepStillDrawsAsAStep()
    {
        // The control, and the reason this cannot simply stop sending moves: a player
        // who actually walks has to be seen walking.
        var world = NewWorld();
        var viewerClient = Viewer(world, 16411, out _);
        var walker = Player(world, 101, 100);

        Tick(viewerClient);
        int baseline = CountMoves(viewerClient, walker.Uid.Value);

        world.MoveCharacter(walker, new Point3D(102, 100, 0, 0));
        Tick(viewerClient);

        int after = CountMoves(viewerClient, walker.Uid.Value);
        _out.WriteLine($"moves sent for the walker: {baseline} -> {after}");
        Assert.True(after > baseline, "a real step sent no move packet");
    }

    [Fact]
    public void ASecondShipStepIsStillSilentForTheSamePassenger()
    {
        // The report was a passenger walking for the WHOLE voyage, so one suppressed
        // step is not the claim - every step has to be.
        var world = NewWorld();
        var viewerClient = Viewer(world, 16412, out _);
        var passenger = Player(world, 101, 100);

        Tick(viewerClient);
        int baseline = CountMoves(viewerClient, passenger.Uid.Value);

        for (short x = 102; x <= 108; x++)
        {
            world.MoveCharacter(passenger, new Point3D(x, 100, 0, 0));
            viewerClient.UpdateKnownCharPosition(passenger);
            Tick(viewerClient);
        }

        _out.WriteLine($"after seven ship steps: {CountMoves(viewerClient, passenger.Uid.Value)} (was {baseline})");
        Assert.Equal(baseline, CountMoves(viewerClient, passenger.Uid.Value));
    }
}
