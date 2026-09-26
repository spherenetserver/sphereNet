using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Link-dead handling (Source-X CClient::CharDisconnect / CanInstantLogOut) and the
/// disconnect diagnostics: a client's own close is seen on receive, the log line
/// carries the traffic around the drop, and @LogOut sets the linger decision.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DisconnectLingerTests
{
    [Fact]
    public void AClientThatClosesItsSocketIsSeenOnReceive()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect((IPEndPoint)listener.LocalEndpoint);
        var serverSide = listener.AcceptSocket();
        var state = new NetState(LoggerFactory.Create(_ => { }).CreateLogger<NetState>());
        state.Init(serverSide);

        Assert.Equal(0, state.Receive()); // open and quiet: nothing to read

        client.Client.Shutdown(SocketShutdown.Both);
        client.Close();
        int result = 0;
        for (int i = 0; i < 50 && result == 0; i++)
        {
            Thread.Sleep(10);
            result = state.Receive();
        }

        Assert.Equal(-1, result);
    }

    [Fact]
    public void TheTrafficSummaryNamesTheLastPacketsSent()
    {
        var state = TestHarness.CreateActiveNetState(LoggerFactory.Create(_ => { }), 1);
        var a = new PacketBuffer(3); a.WriteByte(0x1A); a.WriteByte(0); a.WriteByte(3);
        var b = new PacketBuffer(2); b.WriteByte(0x73); b.WriteByte(0);
        state.Send(a);
        state.Send(b);

        string summary = state.DescribeRecentTraffic();

        Assert.Contains("nothing received", summary);
        Assert.Contains("0x1Ax3 0x73x2", summary);
    }

    private static (GameClient Client, Character Player, TriggerDispatcher Dispatcher) Online(PrivLevel priv = PrivLevel.Player)
    {
        var lf = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var state = TestHarness.CreateActiveNetState(lf, 2);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());
        var dispatcher = new TriggerDispatcher();
        client.SetEngines(triggerDispatcher: dispatcher);
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.PrivLevel = priv;
        player.Hits = player.MaxHits = 50;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, player);
        player.IsOnline = true;
        return (client, player, dispatcher);
    }

    [Fact]
    public void APlayerLingersAfterADrop()
    {
        GameClient.ClientLingerSeconds = 300;
        var (client, player, _) = Online();

        client.OnDisconnect();

        Assert.True(player.IsClientLingering);
    }

    [Fact]
    public void LogOutSeesTheLingerTimeAndCanCancelIt()
    {
        // CClient.cpp:192-201: ARGN1 = linger seconds, ARGN2 = instant logout, both read back.
        GameClient.ClientLingerSeconds = 300;
        var (client, player, dispatcher) = Online();
        long seenLinger = -1, seenInsta = -1;
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "LogOut", (_, args) =>
        {
            seenLinger = args.N1;
            seenInsta = args.N2;
            args.N2 = 1;
            return TriggerResult.Default;
        });

        client.OnDisconnect();

        Assert.Equal(300, seenLinger);
        Assert.Equal(0, seenInsta);
        Assert.False(player.IsClientLingering);
    }

    [Fact]
    public void ACounselorLeavesAtOnce()
    {
        // CanInstantLogOut: GetPrivLevel() > PLEVEL_Player.
        GameClient.ClientLingerSeconds = 300;
        var (client, player, _) = Online(PrivLevel.Counsel);

        client.OnDisconnect();

        Assert.False(player.IsClientLingering);
    }
}
