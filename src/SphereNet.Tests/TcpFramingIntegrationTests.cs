using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.World;
using SphereNet.Network.Manager;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What TCP actually delivers (port plan İŞ-42 / PLAN-604).
///
/// The existing login integration test feeds each client packet as one whole read,
/// which is the one thing a real socket never promises. TCP is a byte stream: a
/// packet can arrive split across reads, several packets can arrive in one, and the
/// seed itself can be cut in half. Those are exactly the cases that work on a
/// loopback and fail across a real network.
///
/// These drive the same production pipeline (NetworkManager.ProcessInput over
/// NetState.InjectReceived) but hand it the bytes the way a socket would: in
/// arbitrary pieces. The pump is run after every piece, so a framer that consumed
/// or dropped a partial packet would show up immediately.
/// </summary>
public class TcpFramingIntegrationTests
{
    private static readonly MethodInfo s_processInput =
        typeof(NetworkManager).GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("NetworkManager.ProcessInput not found");

    private const uint Seed = 0x12345678;

    private static void Pump(NetworkManager nm, NetState state, byte[] wire)
    {
        state.InjectReceived(wire);
        s_processInput.Invoke(nm, [state]);
    }

    /// <summary>Hand the pipeline one byte at a time - the worst case a socket can
    /// produce, and the one that catches a framer reading past what it has.</summary>
    private static void PumpBytewise(NetworkManager nm, NetState state, byte[] wire)
    {
        foreach (var b in wire)
            Pump(nm, state, [b]);
    }

    private static List<byte> Outgoing(NetState state) =>
        TestHarness.GetQueuedPackets(state)
            .Where(p => p.Span.Length > 0)
            .Select(p => p.Span[0])
            .ToList();

    private static NetworkManager NewManager(ILoggerFactory lf) =>
        new(maxClients: 8, lf) { UseNoCrypt = true, UseCrypt = false };

    private static (GameClient client, NetState state) NewConnection(
        ILoggerFactory lf, GameWorld world, AccountManager accounts, int id = 1)
    {
        var state = TestHarness.CreateActiveNetState(lf, id);
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        state.LoginRequestHandler = (_, acct, pwd) => client.HandleLoginRequest(acct, pwd);
        return (client, state);
    }

    private static (GameWorld world, AccountManager accounts, ILoggerFactory lf) CreateEnv()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        var accounts = new AccountManager(lf) { AutoCreateAccounts = true };
        return (world, accounts, lf);
    }

    private static byte[] SeedBytes()
    {
        var b = new byte[4];
        b[0] = unchecked((byte)(Seed >> 24)); b[1] = unchecked((byte)(Seed >> 16));
        b[2] = unchecked((byte)(Seed >> 8)); b[3] = unchecked((byte)Seed);
        return b;
    }

    private static byte[] LoginPacket(string account, string password)
    {
        var b = new byte[62];
        b[0] = 0x80;
        for (int i = 0; i < account.Length && i < 30; i++) b[1 + i] = (byte)account[i];
        for (int i = 0; i < password.Length && i < 30; i++) b[31 + i] = (byte)password[i];
        return b;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var r = new byte[parts.Sum(p => p.Length)];
        int o = 0;
        foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
        return r;
    }

    // ---- the seed ---------------------------------------------------------

    [Fact]
    public void TheClassicSeedSurvivesBeingCutInHalf()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);
        var (_, state) = NewConnection(lf, world, accounts);
        var seed = SeedBytes();

        Pump(nm, state, seed[..1]);
        Assert.False(state.IsSeeded);          // one byte is not a seed
        Pump(nm, state, seed[1..3]);
        Assert.False(state.IsSeeded);
        Pump(nm, state, seed[3..]);

        Assert.True(state.IsSeeded);
        Assert.Equal(Seed, state.Seed);
    }

    [Fact]
    public void TheSeedAndTheFirstPacketInOneReadBothLand()
    {
        // Coalescing: a real client's seed and login often arrive together.
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);
        var (_, state) = NewConnection(lf, world, accounts);

        Pump(nm, state, Concat(SeedBytes(), LoginPacket("tcpuser", "pw")));

        Assert.True(state.IsSeeded);
        Assert.Contains((byte)0xA8, Outgoing(state));   // server list went out
    }

    // ---- a packet in pieces ------------------------------------------------

    [Fact]
    public void AFixedLengthPacketSplitAcrossReadsIsStillOnePacket()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);
        var (_, state) = NewConnection(lf, world, accounts);
        Pump(nm, state, SeedBytes());

        var login = LoginPacket("splituser", "pw");
        Pump(nm, state, login[..1]);      // opcode alone
        Assert.DoesNotContain((byte)0xA8, Outgoing(state));
        Pump(nm, state, login[1..40]);    // part of the body
        Assert.DoesNotContain((byte)0xA8, Outgoing(state));
        Pump(nm, state, login[40..]);     // the rest

        Assert.Contains((byte)0xA8, Outgoing(state));
    }

    [Fact]
    public void APacketDeliveredOneByteAtATimeStillArrivesExactlyOnce()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);
        var (_, state) = NewConnection(lf, world, accounts);

        PumpBytewise(nm, state, Concat(SeedBytes(), LoginPacket("bytewise", "pw")));

        Assert.True(state.IsSeeded);
        // Exactly one server list - not none from a dropped tail, not two from a
        // framer that re-read what it had already consumed.
        Assert.Single(Outgoing(state), o => o == 0xA8);
    }

    // ---- several packets in one read ---------------------------------------

    [Fact]
    public void TwoCompletePacketsInOneReadAreBothHandled()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);
        var (_, state) = NewConnection(lf, world, accounts);
        Pump(nm, state, SeedBytes());

        Pump(nm, state, Concat(LoginPacket("first", "pw"), LoginPacket("second", "pw")));

        Assert.Equal(2, Outgoing(state).Count(o => o == 0xA8));
    }

    [Fact]
    public void AWholePacketFollowedByHalfOfTheNextLeavesTheHalfWaiting()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);
        var (_, state) = NewConnection(lf, world, accounts);
        Pump(nm, state, SeedBytes());

        var second = LoginPacket("second", "pw");
        Pump(nm, state, Concat(LoginPacket("first", "pw"), second[..20]));
        Assert.Single(Outgoing(state), o => o == 0xA8);   // only the first

        Pump(nm, state, second[20..]);
        Assert.Equal(2, Outgoing(state).Count(o => o == 0xA8)); // now both
    }

    // ---- reconnect ---------------------------------------------------------

    [Fact]
    public void TheSameAccountCanComeBackOnAFreshConnection()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);

        var (_, first) = NewConnection(lf, world, accounts, id: 1);
        Pump(nm, first, Concat(SeedBytes(), LoginPacket("returning", "pw")));
        Assert.Contains((byte)0xA8, Outgoing(first));
        first.MarkClosing();

        // A second socket, a new seed, the same account - what a reconnect is.
        var (_, second) = NewConnection(lf, world, accounts, id: 2);
        Pump(nm, second, Concat(SeedBytes(), LoginPacket("returning", "pw")));

        Assert.True(second.IsSeeded);
        Assert.Contains((byte)0xA8, Outgoing(second));
    }

    [Fact]
    public void AReconnectingSocketCarriesNoneOfThePreviousOnesState()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = NewManager(lf);

        var (_, first) = NewConnection(lf, world, accounts, id: 1);
        Pump(nm, first, SeedBytes());
        Assert.True(first.IsSeeded);
        first.MarkClosing();

        var (_, second) = NewConnection(lf, world, accounts, id: 2);

        // The new connection starts unseeded and with its own crypto state, so a
        // half-delivered packet on the old socket cannot bleed into it.
        Assert.False(second.IsSeeded);
        Assert.False(second.Crypto.IsInitialized);
    }
}
