using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The outer input loop over real loopback sockets: what NetworkManager.ProcessAllInput
/// and Tick do with bytes that are already buffered when the socket has nothing new.
///
/// The in-process framing tests call the private ProcessInput directly, so they cannot
/// see the decision the outer loop makes BEFORE it: whether to process this connection
/// at all on a pass where the socket gave nothing. Every test here goes through a
/// listening socket, a real accept and real sends.
/// </summary>
public sealed class NetworkInputLoopTests
{
    private const byte TestOpcode = 0xF9;   // variable length; its handler is replaced per test

    private sealed class CountingHandler(int throwOnCalls = 0) : PacketHandler(TestOpcode, 0)
    {
        public int Calls;
        public override void OnReceive(PacketBuffer buffer, NetState state)
        {
            Calls++;
            if (Calls <= throwOnCalls)
                throw new InvalidOperationException("simulated handler fault");
        }
    }

    private static readonly byte[] TestPacket = [TestOpcode, 0x00, 0x05, 0x01, 0x02];

    private sealed class Loopback : IDisposable
    {
        public readonly NetworkManager Mgr;
        private readonly int _port;
        private readonly List<Socket> _clients = [];

        public Loopback(NetworkManager mgr)
        {
            Mgr = mgr;
            Assert.True(mgr.Start("127.0.0.1", 0));
            var listen = (Socket)typeof(NetworkManager)
                .GetField("_listenSocket", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(mgr)!;
            _port = ((IPEndPoint)listen.LocalEndPoint!).Port;
        }

        public (Socket Client, NetState State) Connect()
        {
            var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            client.Connect(IPAddress.Loopback, _port);
            _clients.Add(client);
            int localPort = ((IPEndPoint)client.LocalEndPoint!).Port;
            NetState? found = null;
            Spin(() =>
            {
                Mgr.CheckNewConnections();
                for (int i = 0; ; i++)
                {
                    var s = Mgr.GetState(i);
                    if (s == null) break;
                    if (s.IsInUse && s.RemoteEndPoint?.Port == localPort) { found = s; return true; }
                }
                return false;
            });
            return (client, found!);
        }

        public void PumpUntil(Func<bool> condition) => Spin(() =>
        {
            Mgr.ProcessAllInput();
            return condition();
        });

        private static void Spin(Func<bool> step)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!step())
            {
                if (sw.ElapsedMilliseconds > 5000)
                    throw new TimeoutException("loopback condition not reached");
                Thread.Sleep(1);
            }
        }

        public void Dispose()
        {
            foreach (var c in _clients)
                try { c.Dispose(); } catch { }
            Mgr.Dispose();
        }
    }

    private static byte[] Seed() => [0x7F, 0x00, 0x00, 0x01];

    private static byte[] LoginPacket(string account, string password)
    {
        var b = new byte[62];
        b[0] = 0x80;
        for (int i = 0; i < account.Length && i < 29; i++) b[1 + i] = (byte)account[i];
        for (int i = 0; i < password.Length && i < 29; i++) b[31 + i] = (byte)password[i];
        return b;
    }

    private static byte[] GameLoginPacket(uint authId, string account, string password)
    {
        var b = new byte[65];
        b[0] = 0x91;
        b[1] = (byte)(authId >> 24); b[2] = (byte)(authId >> 16); b[3] = (byte)(authId >> 8); b[4] = (byte)authId;
        for (int i = 0; i < account.Length && i < 29; i++) b[5 + i] = (byte)account[i];
        for (int i = 0; i < password.Length && i < 29; i++) b[35 + i] = (byte)password[i];
        return b;
    }

    private static byte[] Repeat(byte[] packet, int times) =>
        Enumerable.Range(0, times).SelectMany(_ => packet).ToArray();

    private static NetworkManager NoCryptManager() =>
        new(2, NullLoggerFactory.Instance) { UseNoCrypt = true, UseCrypt = false };

    private static (Socket Client, NetState State) LoggedIn(Loopback lb)
    {
        var (client, state) = lb.Connect();
        client.Send([.. Seed(), .. LoginPacket("loopuser", "pw")]);
        lb.PumpUntil(() => state.ConnectionType == ConnectType.Login);
        return (client, state);
    }

    // ---- N01: every received packet is processed, none held back by count -----

    /// <summary>Records the 16-bit sequence number each test packet carries.</summary>
    private sealed class SequenceHandler() : PacketHandler(TestOpcode, 0)
    {
        public readonly List<int> Seen = [];
        public override void OnReceive(PacketBuffer buffer, NetState state) =>
            Seen.Add(buffer.ReadUInt16());
    }

    private static byte[] Sequenced(int seq) =>
        [TestOpcode, 0x00, 0x05, (byte)(seq >> 8), (byte)seq];

    /// <summary>The live failure: a busy client in a crowded area (tooltip batches,
    /// clicks, moves, pings) sends far more small packets per tick than the old
    /// per-pass cap of 50, and the cap counted each such pass toward a disconnect.
    /// Source-X processes everything received each pass (CNetworkInput::
    /// processData, CNetworkInput.cpp:211-224) and drops no one for packet count.
    /// Here a client sends bursts of 1000 valid small packets plus pings, burst after
    /// burst over the real socket path: it stays connected and every packet is
    /// dispatched exactly once, in order.</summary>
    [Fact]
    public void RepeatedBurstsOfAThousandPacketsAreAllDispatchedInOrderAndNeverDropped()
    {
        var nm = NoCryptManager();
        var handler = new SequenceHandler();
        nm.Packets.Register(handler);
        using var lb = new Loopback(nm);
        var (client, state) = LoggedIn(lb);

        const int perBurst = 1000;
        const int bursts = 8;
        int next = 0;
        for (int burst = 0; burst < bursts; burst++)
        {
            var bytes = new List<byte>(perBurst * 7);
            for (int i = 0; i < perBurst; i++)
            {
                bytes.AddRange(Sequenced(next++));
                bytes.AddRange([0x73, 0x00]);   // interleaved pings
            }
            client.Send(bytes.ToArray());

            int expected = next;
            lb.PumpUntil(() => handler.Seen.Count >= expected || state.IsClosing);

            Assert.False(state.IsClosing, $"dropped during burst {burst}");
            Assert.Equal(expected, handler.Seen.Count);
        }

        Assert.Equal(Enumerable.Range(0, bursts * perBurst), handler.Seen);
        Assert.Equal(0, state.ReceivedData.Length);
        Assert.True(state.IsInUse);
    }

    /// <summary>One pass consumes every whole packet already buffered: nothing is left
    /// for a later pass by a packet count.</summary>
    [Fact]
    public void OnePassDispatchesEveryBufferedPacket()
    {
        var (mgr, state) = Seeded();
        var handler = new SequenceHandler();
        mgr.Packets.Register(handler);

        state.InjectReceived(Enumerable.Range(0, 1000).SelectMany(Sequenced).ToArray());
        Process(mgr, state);

        Assert.Equal(Enumerable.Range(0, 1000), handler.Seen);
        Assert.Equal(0, state.ReceivedData.Length);
        Assert.False(state.IsClosing);
    }

    private sealed class ClosingHandler() : PacketHandler(TestOpcode, 0)
    {
        public int Calls;
        public override void OnReceive(PacketBuffer buffer, NetState state)
        {
            Calls++;
            state.MarkClosing();
        }
    }

    /// <summary>Once a handler closes the connection, the rest of the buffer is not
    /// processed: upstream's loop checks isReadClosed before each packet
    /// (CNetworkInput.cpp:213).</summary>
    [Fact]
    public void ProcessingStopsOnceTheConnectionIsClosing()
    {
        var (mgr, state) = Seeded();
        var handler = new ClosingHandler();
        mgr.Packets.Register(handler);

        state.InjectReceived(Repeat(TestPacket, 3));
        Process(mgr, state);

        Assert.Equal(1, handler.Calls);
        Assert.True(state.IsClosing);
    }

    [Fact]
    public void APacketAfterAFaultingHandlerIsProcessedWithoutNewData()
    {
        var nm = NoCryptManager();
        var handler = new CountingHandler(throwOnCalls: 1);
        nm.Packets.Register(handler);
        using var lb = new Loopback(nm);
        var (client, state) = LoggedIn(lb);

        client.Send(Repeat(TestPacket, 2));
        lb.PumpUntil(() => handler.Calls >= 1);

        for (int pass = 0; pass < 5; pass++)
            nm.ProcessAllInput();

        Assert.Equal(2, handler.Calls);
        Assert.Equal(1, state.PacketExceptionCount);
        Assert.Equal(0, state.ReceivedData.Length);
        Assert.False(state.IsClosing);
    }

    // ---- N04: the partial-packet deadline runs while the socket is silent -----

    [Theory]
    [InlineData(1)]   // opcode only
    [InlineData(2)]   // opcode and half the length field
    [InlineData(3)]   // full header, body missing
    public void AStalledPartialPacketIsDroppedByTickWhileTheSocketIsSilent(int bytes)
    {
        var nm = NoCryptManager();
        using var lb = new Loopback(nm);
        var (client, state) = LoggedIn(lb);

        byte[] speechHeader = [0xAD, 0x00, 0x64];   // 0xAD claiming 100 bytes
        client.Send(speechHeader[..bytes]);
        lb.PumpUntil(() => state.ReceivedData.Length == bytes);

        Assert.True(state.PendingPacketStartTick > 0, "the partial packet must start its deadline");

        nm.Tick();
        Assert.True(state.IsInUse, "calibration: nothing is due yet");

        state.PendingPacketStartTick -= 20_000;   // the deadline is 15 s
        state.LastActivityTick = Environment.TickCount64;
        nm.ProcessAllInput();
        nm.Tick();

        Assert.False(state.IsInUse);
    }

    // ---- N05: a reused slot starts clean --------------------------------------

    [Theory]
    [InlineData(3u)]   // enhanced client
    [InlineData(2u)]   // Kingdom Reborn
    public void AReusedSlotCarriesNothingFromThePreviousClient(uint previousClientType)
    {
        var nm = new NetworkManager(1, NullLoggerFactory.Instance) { UseNoCrypt = true, UseCrypt = false };
        using var lb = new Loopback(nm);

        var (first, state) = lb.Connect();
        state.ClientTypeFlag = previousClientType;
        state.ClientLanguage = "TRK";
        state.PacketExceptionCount = 10;
        state.ClientExpansion = Expansion.SA;
        state.ScreenWidth = 1024;
        state.ScreenHeight = 768;
        state.ClientVersion = "7.0.11.4";
        state.AssistVersion = 99;
        state.ViewRange = 7;
        state.HuffmanReceiveEnabled = true;
        SetPrivate(state.Crypto, "<RelayClientVersion>k__BackingField", 70011400u);
        SetPrivate(state.Crypto, "<LastDetectionDiagnostic>k__BackingField", "stale");
        SetPrivate(state, "_rttMs", 42);
        SetPrivate(state, "_rttPingSentTick", 1000L);
        SetPrivate(state, "_rttLastPingSentTick", 1000L);
        SetPrivate(state, "_rttPingSeq", (byte)0x85);
        SetPrivate(state, "_recentOutCount", 3);
        SetPrivate(state, "<LastReceiveTick>k__BackingField", 5000L);
        state.MarkClosing();
        nm.Tick();
        first.Dispose();

        var (second, reused) = lb.Connect();
        Assert.Same(state, reused);

        Assert.Equal(0u, reused.ClientTypeFlag);
        Assert.False(reused.IsEnhancedClient);
        Assert.False(reused.IsKingdomRebornClient);
        Assert.False(reused.SupportsNewMapDisplay);
        Assert.Equal("ENU", reused.ClientLanguage);
        Assert.Equal(0, reused.PacketExceptionCount);
        Assert.Equal(Expansion.None, reused.ClientExpansion);
        Assert.Equal(0, reused.ScreenWidth);
        Assert.Equal(0, reused.ScreenHeight);
        Assert.Equal("", reused.ClientVersion);
        Assert.Equal(0u, reused.AssistVersion);
        Assert.Equal(NetState.DefaultViewRange, reused.ViewRange);
        Assert.False(reused.HuffmanReceiveEnabled);
        Assert.Equal(0u, reused.Crypto.RelayClientVersion);
        Assert.Equal("", reused.Crypto.LastDetectionDiagnostic);
        Assert.False(reused.HasRtt);
        Assert.Equal(0L, reused.LastReceiveTick);
        Assert.Equal("nothing received", reused.DescribeRecentTraffic());

        // A plaintext game login on the new connection must not pick the old relay
        // version back up.
        second.Send([.. Seed(), .. GameLoginPacket(0x01020304, "fresh", "pw")]);
        lb.PumpUntil(() => reused.ConnectionType == ConnectType.Game);
        Assert.Equal(0u, reused.ClientVersionNumber);
        Assert.False(reused.SupportsNewMapDisplay);
    }

    private static void SetPrivate(object target, string field, object value) =>
        target.GetType()
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(target, value);

    // ---- in-process framing helpers ------------------------------------------

    private static (NetworkManager Mgr, NetState State) Seeded()
    {
        var mgr = new NetworkManager(4, NullLoggerFactory.Instance);
        var state = mgr.GetState(0)!;
        SetPrivate(state, "<IsInUse>k__BackingField", true);
        state.Id = 1;
        state.IsSeeded = true;
        SetPrivate(state.Crypto, "_initialized", true);
        SetPrivate(state.Crypto, "_encType", EncryptionType.None);
        return (mgr, state);
    }

    private static void Process(NetworkManager mgr, NetState state) =>
        typeof(NetworkManager)
            .GetMethod("ProcessInput", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(mgr, [state]);

}
