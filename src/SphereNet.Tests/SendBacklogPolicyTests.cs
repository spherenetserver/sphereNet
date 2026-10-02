using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;
using SphereNet.Network.Packets;
using SphereNet.Network.State;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The outbound backlog policy. Source-X queues every packet and drains as the socket
/// accepts data (CNetworkOutput::QueuePacketTransaction, CNetworkOutput.cpp:621-649 -
/// MAXQUEUESIZE only demotes priority) and closes a client only on a socket error
/// (sendData, CNetworkOutput.cpp:537-571). A fixed 4096-packet queue cap used to drop a
/// GM who teleported into a crowded area with ALLSHOW on: the single view refresh
/// queues an object packet plus a tooltip per character and item in sight.
/// </summary>
public sealed class SendBacklogPolicyTests
{
    private sealed class Sink(List<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> fmt)
        {
            lock (lines) lines.Add(fmt(state, ex));
        }
    }

    private sealed class Pair : IDisposable
    {
        public required NetState State;
        public required Socket Client;
        public required Socket Server;
        public required List<string> Lines;

        public void Dispose()
        {
            try { Client.Dispose(); } catch { }
            try { Server.Dispose(); } catch { }
        }
    }

    /// <summary>A game connection over real loopback sockets, no encryption, with
    /// small socket buffers so a burst backs up in the server's outbound buffer.</summary>
    private static Pair Connect()
    {
        using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(1);
        var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ReceiveBufferSize = 4096
        };
        client.Connect(listener.LocalEndPoint!);
        var server = listener.Accept();

        var lines = new List<string>();
        var state = new NetState(new Sink(lines));
        state.Init(server);
        server.SendBufferSize = 4096;
        state.ConnectionType = ConnectType.Game;
        state.IsSeeded = true;
        var crypto = state.Crypto;
        var t = crypto.GetType();
        t.GetField("_initialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, true);
        t.GetField("_encType", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(crypto, EncryptionType.None);
        return new Pair { State = state, Client = client, Server = server, Lines = lines };
    }

    /// <summary>A 0xF3-shaped world item packet carrying its sequence number.</summary>
    private static byte[] WorldItem(int seq)
    {
        var b = new byte[26];
        b[0] = 0xF3;
        b[1] = 0x00; b[2] = 0x01;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(4), 0x40000000 + seq);
        return b;
    }

    /// <summary>A 0xDC tooltip-revision packet carrying its sequence number.</summary>
    private static byte[] Tooltip(int seq)
    {
        var b = new byte[9];
        b[0] = 0xDC;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(1), 0x40000000 + seq);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(5), seq);
        return b;
    }

    private static void Spin(Func<bool> step, int timeoutMs = 20_000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!step())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException("condition not reached");
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void AViewBurstFarPastTheOldQueueCapIsDeliveredCompleteAndInOrder()
    {
        using var p = Connect();
        var sent = new List<byte[]>();
        // 12,000 objects, each with its tooltip: 24,000 packets queued before the
        // first flush - six times the old 4096-packet cap.
        for (int i = 0; i < 12_000; i++)
        {
            sent.Add(WorldItem(i));
            sent.Add(Tooltip(i));
        }
        foreach (var raw in sent)
            p.State.Send(new PacketBuffer(raw));

        Assert.False(p.State.IsClosing);
        Assert.Equal(sent.Count, p.State.QueuedPacketCount);

        // The client reads slowly; the server flushes every pass.
        var received = new MemoryStream();
        var buf = new byte[1024];
        p.Client.Blocking = false;
        Spin(() =>
        {
            p.State.FlushOutput();
            Assert.False(p.State.IsClosing);
            int n = p.Client.Receive(buf, 0, buf.Length, SocketFlags.None, out var err);
            if (n > 0) received.Write(buf, 0, n);
            else if (err != SocketError.WouldBlock && err != SocketError.Success)
                throw new SocketException((int)err);
            return p.State.QueuedPacketCount == 0 && p.State.PendingSendBytes == 0
                && received.Length == p.State.OutByteCounter;
        });

        Assert.False(p.State.IsClosing);
        var bytes = received.ToArray();
        int off = 0;
        int idx = 0;
        while (off < bytes.Length)
        {
            var pkt = HuffmanCompression.DecompressFromServer(bytes, off, bytes.Length - off, out int used);
            Assert.True(used > 0);
            off += used;
            Assert.True(idx < sent.Count, "more packets arrived than were sent");
            Assert.Equal(sent[idx], pkt);
            idx++;
        }
        Assert.Equal(sent.Count, idx);
    }

    [Fact]
    public void AClientWhoseSocketAcceptsNothingIsDisconnectedWithAClearLogLine()
    {
        using var p = Connect();
        p.State.SendStallTimeoutMs = 300;
        // The client never reads: once the kernel buffers fill, nothing moves.
        // Keep the backlog topped up until they do (loopback buffers vary by OS).
        int seq = 0;
        Spin(() =>
        {
            if (p.State.PendingSendBytes < 256 * 1024)
                for (int i = 0; i < 20_000; i++)
                    p.State.Send(new PacketBuffer(WorldItem(seq++)));
            p.State.FlushOutput();
            return p.State.IsClosing;
        }, 15_000);

        Assert.True(p.State.PendingSendBytes > 0);
        lock (p.Lines)
            Assert.Contains(p.Lines, l => l.Contains("Send stalled") && l.Contains("accepted no data"));
    }

    [Fact]
    public void ABacklogPastTheMemoryCeilingIsDisconnected()
    {
        using var p = Connect();
        p.State.MaxSendBacklogBytes = 64 * 1024;
        for (int i = 0; i < 40_000; i++)
            p.State.Send(new PacketBuffer(WorldItem(i)));

        p.State.FlushOutput();

        Assert.True(p.State.IsClosing);
        lock (p.Lines)
            Assert.Contains(p.Lines, l => l.Contains("ceiling"));
    }

    [Fact]
    public void ABacklogThatKeepsDrainingIsNeverTreatedAsAStall()
    {
        using var p = Connect();
        p.State.SendStallTimeoutMs = 3000;
        // Fill until the kernel buffers are full and a real backlog sits in the
        // server's outbound buffer.
        int seq = 0;
        Spin(() =>
        {
            for (int i = 0; i < 20_000; i++)
                p.State.Send(new PacketBuffer(WorldItem(seq++)));
            p.State.FlushOutput();
            return p.State.PendingSendBytes > 64 * 1024;
        }, 10_000);
        Assert.False(p.State.IsClosing);

        // A slow but live client: a small slice every pass, for several times
        // the stall timeout. Progress, however slow, is never a stall.
        var buf = new byte[16 * 1024];
        p.Client.Blocking = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long total = 0;
        while (sw.ElapsedMilliseconds < 5000 && (p.State.QueuedPacketCount > 0 || p.State.PendingSendBytes > 0))
        {
            p.State.FlushOutput();
            Assert.False(p.State.IsClosing);
            int n = p.Client.Receive(buf, 0, buf.Length, SocketFlags.None, out _);
            if (n > 0) total += n;
            Thread.Sleep(1);
        }

        Assert.True(total > 0);
        Assert.False(p.State.IsClosing);
    }

    [Fact]
    public void UnderPressureSharedChatterIsShedButStateAndUnicastAreKept()
    {
        using var p = Connect();
        // Back the queue up past the 1024-packet soft threshold.
        for (int i = 0; i < 1500; i++)
            p.State.Send(new PacketBuffer(WorldItem(i)));
        int before = p.State.QueuedPacketCount;

        static PacketBuffer Shared(byte[] raw)
        {
            var pkt = new PacketBuffer(raw);
            pkt.MarkShared(1);
            return pkt;
        }

        // Overhead speech and a sound broadcast to the crowd: shed.
        p.State.EnqueueShared(Shared([0xAE, 0x00, 0x0A, 0, 0, 0, 0, 0, 0, 0]));
        p.State.EnqueueShared(Shared([0x54, 0x01, 0x00, 0x2A, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.Equal(before, p.State.QueuedPacketCount);

        // A shared state packet (mobile moving, 0x77) is kept.
        var move = new byte[17];
        move[0] = 0x77;
        p.State.EnqueueShared(Shared(move));
        Assert.Equal(before + 1, p.State.QueuedPacketCount);

        // Speech sent to this connection alone is never shed.
        p.State.Send(new PacketBuffer(new byte[] { 0xAE, 0x00, 0x0A, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.Equal(before + 2, p.State.QueuedPacketCount);
        Assert.False(p.State.IsClosing);
    }

    [Fact]
    public void BelowTheSoftThresholdSharedChatterIsDelivered()
    {
        using var p = Connect();
        var pkt = new PacketBuffer(new byte[] { 0xAE, 0x00, 0x0A, 0, 0, 0, 0, 0, 0, 0 });
        pkt.MarkShared(1);
        p.State.EnqueueShared(pkt);
        Assert.Equal(1, p.State.QueuedPacketCount);
    }
}
