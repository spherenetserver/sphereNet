using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.World;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 01 / C2 — inbound exception firewall. A throw from a packet handler (the
/// largest attacker-reachable surface: game logic + synchronous .scp triggers)
/// must be contained to the offending connection instead of escaping the input
/// phase and killing the whole server (which would also skip the shutdown save).
///
/// These drive the REAL private ProcessInput pump in-process (the same code path
/// a socket read reaches through ProcessAllInput), the way the login integration
/// tests do. The socket-bound siblings of this fix — per-connection isolation in
/// ProcessAllInput, OnConnectionAccepted in CheckNewConnections, and FlushOutput
/// in ProcessAllOutput — need a live socket/accept loop and are covered by the
/// structural change (each wraps its call in the same try/catch → MarkClosing),
/// not re-driven here.
/// </summary>
public sealed class NetworkExceptionFirewallTests
{
    private static readonly MethodInfo s_processInput =
        typeof(NetworkManager).GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("NetworkManager.ProcessInput not found");

    private const uint Seed = 0x12345678;
    private const byte ThrowOpcode = 0xF9; // variable-length, overridden per test

    private sealed class ThrowingHandler() : PacketHandler(ThrowOpcode, 0)
    {
        public int Calls { get; private set; }
        public override void OnReceive(PacketBuffer buffer, NetState state)
        {
            Calls++;
            throw new InvalidOperationException("simulated handler fault");
        }
    }

    private static void Pump(NetworkManager nm, NetState state, byte[] wire)
    {
        state.InjectReceived(wire);
        s_processInput.Invoke(nm, [state]);
    }

    private static List<byte> Outgoing(NetState state) =>
        TestHarness.GetQueuedPackets(state)
            .Where(p => p.Span.Length > 0)
            .Select(p => p.Span[0])
            .ToList();

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

    private static (GameClient client, NetState state) NewConnection(
        ILoggerFactory lf, GameWorld world, AccountManager accounts, int id)
    {
        var state = TestHarness.CreateActiveNetState(lf, id);
        var client = new GameClient(state, world, accounts, lf.CreateLogger<GameClient>());
        state.LoginRequestHandler = (_, acct, pwd) => client.HandleLoginRequest(acct, pwd);
        return (client, state);
    }

    private static byte[] SeedBytes()
    {
        var b = new byte[4];
        b[0] = (byte)((Seed >> 24) & 0xFF); b[1] = (byte)((Seed >> 16) & 0xFF);
        b[2] = (byte)((Seed >> 8) & 0xFF); b[3] = (byte)(Seed & 0xFF);
        return b;
    }

    // 0x80 login request: opcode + account(30) + password(30) + nextKey(1) = 62.
    private static byte[] LoginPacket(string account, string password)
    {
        var b = new byte[62];
        b[0] = 0x80;
        for (int i = 0; i < 30 && i < account.Length; i++) b[1 + i] = (byte)account[i];
        for (int i = 0; i < 30 && i < password.Length; i++) b[31 + i] = (byte)password[i];
        return b;
    }

    // Variable-length packet whose handler is the ThrowingHandler: [op, len_hi, len_lo, payload].
    private static byte[] ThrowingPacket() => [ThrowOpcode, 0x00, 0x04, 0x00];

    private static byte[] Concat(byte[] a, byte[] c)
    {
        var r = new byte[a.Length + c.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(c, 0, r, a.Length, c.Length);
        return r;
    }

    [Fact]
    public void HandlerThrow_IsContained_AndClosesOnlyThatConnection()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = new NetworkManager(maxClients: 8, lf) { UseNoCrypt = true, UseCrypt = false };
        var throwing = new ThrowingHandler();
        nm.Packets.Register(throwing);

        var (_, state) = NewConnection(lf, world, accounts, 1);
        // Establish seed + no-crypt + a Login connection (proven pipeline).
        Pump(nm, state, Concat(SeedBytes(), LoginPacket("hero", "secret")));
        Assert.True(state.IsSeeded);
        Assert.Equal(ConnectType.Login, state.ConnectionType);

        // The throwing handler runs but its exception must NOT propagate out of
        // the input pump — it is caught and the connection is marked closing.
        var ex = Record.Exception(() => Pump(nm, state, ThrowingPacket()));
        Assert.Null(ex);
        Assert.Equal(1, throwing.Calls);   // the handler really was reached
        Assert.True(state.IsClosing);      // only this connection is dropped
    }

    [Fact]
    public void SecondConnection_KeepsWorking_AfterAnotherConnectionThrows()
    {
        var (world, accounts, lf) = CreateEnv();
        var nm = new NetworkManager(maxClients: 8, lf) { UseNoCrypt = true, UseCrypt = false };
        nm.Packets.Register(new ThrowingHandler());

        var (_, victim) = NewConnection(lf, world, accounts, 1);
        Pump(nm, victim, Concat(SeedBytes(), LoginPacket("hero", "secret")));
        Pump(nm, victim, ThrowingPacket()); // victim faults and is closed
        Assert.True(victim.IsClosing);

        // A second, independent connection logs in normally and gets its server
        // list — the first connection's fault did not poison the pipeline.
        var (_, other) = NewConnection(lf, world, accounts, 2);
        Pump(nm, other, Concat(SeedBytes(), LoginPacket("second", "secret")));

        Assert.False(other.IsClosing);
        Assert.Equal(ConnectType.Login, other.ConnectionType);
        Assert.Contains((byte)0xA8, Outgoing(other)); // 0xA8 server list
    }
}
