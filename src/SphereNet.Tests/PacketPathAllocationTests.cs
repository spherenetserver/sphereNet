using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;
using SphereNet.Network.Manager;
using SphereNet.Network.Packets;
using SphereNet.Network.State;

namespace SphereNet.Tests;

/// <summary>
/// Garbage on the packet path, measured: flushing unicast packets compresses each into
/// the connection's send buffer (no array per packet), and an incoming packet's payload
/// is rented and returned (no array per packet). The receive side is checked to still
/// hand handlers the right bytes.
/// </summary>
public sealed class PacketPathAllocationTests
{
    private static (NetState State, Socket Client, Socket Server) Connect()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = 4 * 1024 * 1024,
        };
        client.Connect(listener.LocalEndPoint!);
        var server = listener.Accept();
        server.SendBufferSize = 4 * 1024 * 1024;

        var state = new NetState(NullLogger.Instance);
        state.Init(server);
        state.ConnectionType = ConnectType.Game;
        state.IsSeeded = true;
        var crypto = state.Crypto;
        var t = crypto.GetType();
        t.GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, true);
        t.GetField("_encType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, EncryptionType.None);
        return (state, client, server);
    }

    private static PacketBuffer WorldItem(int seq)
    {
        var p = new PacketBuffer(26);
        p.WriteByte(0xF3);
        p.WriteUInt16(0x0001);
        p.WriteByte(0);
        p.WriteUInt32(0x40000000u + (uint)seq);
        for (int i = 0; i < 18; i++)
            p.WriteByte((byte)i);
        return p;
    }

    private static void Drain(Socket client)
    {
        var buf = new byte[1 << 16];
        client.Blocking = false;
        while (client.Receive(buf, 0, buf.Length, SocketFlags.None, out var err) > 0 && err == SocketError.Success) { }
    }

    [Fact]
    public void FlushingUnicastPacketsAllocatesNoArrayPerPacket()
    {
        var (state, client, server) = Connect();
        try
        {
            // Warm up: grows the send buffer once.
            for (int i = 0; i < 2000; i++) state.Send(WorldItem(i));
            state.FlushOutput();
            Drain(client);

            for (int i = 0; i < 2000; i++) state.Send(WorldItem(i));
            long before = GC.GetAllocatedBytesForCurrentThread();
            state.FlushOutput();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Drain(client);

            // The array per packet it replaced was about 2000 x 56 bytes.
            Assert.True(allocated < 16 * 1024, $"flush of 2000 packets allocated {allocated} bytes");
            Assert.False(state.IsClosing);
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }
    }

    private sealed class Recorder : PacketHandler
    {
        public readonly List<byte[]> Payloads = [];
        public Recorder() : base(0x06, 5) { }
        public override void OnReceive(PacketBuffer buffer, NetState state) =>
            Payloads.Add(buffer.ReadBytes(buffer.Remaining));
    }

    [Fact]
    public void IncomingPayloadsAreRentedYetHandlersSeeTheirOwnBytes()
    {
        var mgr = new NetworkManager(1, NullLoggerFactory.Instance) { UseCrypt = false, UseNoCrypt = true };
        var recorder = new Recorder();
        var packets = (PacketManager)typeof(NetworkManager)
            .GetField("_packetManager", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(mgr)!;
        packets.Register(recorder);
        var state = mgr.GetState(0)!;
        typeof(NetState).GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(state, true);
        state.IsSeeded = true;
        var crypto = state.Crypto;
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        crypto.GetType().GetField("_initialized", flags)!.SetValue(crypto, true);
        crypto.GetType().GetField("_encType", flags)!.SetValue(crypto, EncryptionType.None);

        var stream = new List<byte>();
        for (int i = 0; i < 200; i++)
            stream.AddRange([0x06, (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i]);
        state.InjectReceived([.. stream]);
        typeof(NetworkManager).GetMethod("ProcessInput", flags)!.Invoke(mgr, [state]);

        Assert.Equal(200, recorder.Payloads.Count);
        for (int i = 0; i < 200; i++)
            Assert.Equal(new[] { (byte)(i >> 24), (byte)(i >> 16), (byte)(i >> 8), (byte)i }, recorder.Payloads[i]);
    }
}
