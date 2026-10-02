using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using SphereNet.Game.Diagnostics;

namespace SphereNet.LoadTest;

/// <summary>
/// One real-socket client: logs in through the login and game connections exactly as
/// a no-encryption client does (0xEF/0x80 -> 0xA8 -> 0xA0 -> 0x8C, then 0xEF/0x91 ->
/// 0xA9 -> 0x5D -> 0x1B) and then walks, talks and double-clicks on a fixed,
/// per-bot-seeded script.
///
/// Measurement: the time from writing a 0x02 move request to decoding the 0x22 (or
/// 0x21) that answers its sequence number. Steps are paced at walking speed so the
/// server's own step throttle never holds a request back - what is left is the time
/// the server took to read, process and answer it.
///
/// The accounts and characters are ordinary players the synthetic shard generator
/// wrote into the save, so nothing about the bots is special-cased by the server.
/// </summary>
internal sealed class LoadBot
{
    private const int MaxStepsInFlight = 4;
    private static readonly sbyte[] Dx = [0, 1, 1, 1, 0, -1, -1, -1];
    private static readonly sbyte[] Dy = [-1, -1, 0, 1, 1, 1, 0, -1];

    private readonly int _id;
    private readonly Random _rng;
    private readonly string _account;
    private readonly string _charName;
    private readonly ServerStreamDecoder _decoder = new();
    private readonly long[] _stepSentTs = new long[256];
    private readonly bool[] _stepMeasured = new bool[256];
    private readonly object _stepLock = new();
    private readonly uint[] _knownItems = new uint[64];
    private int _knownItemCount;
    private int _knownItemCursor;

    private Socket? _socket;
    private TaskCompletionSource<bool>? _charListSeen;
    private TaskCompletionSource<bool>? _loginConfirmed;
    private byte _nextSeq;
    private int _stepsInFlight;
    private byte _facing;
    private int _x, _y, _homeX, _homeY;
    private uint _serial;

    public LoadBot(int id, int seed)
    {
        _id = id;
        _rng = new Random(unchecked(seed * 7919 + id * 104729));
        _account = SyntheticShard.AccountName(id);
        _charName = SyntheticShard.CharName(id);
    }

    /// <summary>Set by the runner for the measured window only.</summary>
    public static volatile bool Measuring;

    public string? FailReason { get; private set; }
    public bool LoggedIn { get; private set; }
    public bool Disconnected { get; private set; }
    public bool DisconnectedWhileMeasuring { get; private set; }
    public double LoginMs { get; private set; }

    public long BytesIn;
    public long BytesOut;
    public long PacketsIn;
    public int MovesSent;
    public int MovesSentMeasured;
    public int Acks;
    public int Rejects;
    /// <summary>Steps the server never answered (written off after 5 s).</summary>
    public int StepsLost;
    /// <summary>Steps still in flight when a reject reset the sequence; the server
    /// drops them by design (the client resends from 0).</summary>
    public int StepsDroppedByReject;
    public int Speeches;
    public int DoubleClicks;
    /// <summary>Move answer latencies of the measured window, in microseconds.</summary>
    public readonly List<int> AckLatencyUs = new(512);

    public async Task<bool> LoginAsync(string host, int port, CancellationToken ct)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            uint authId;
            using (var login = await ConnectAsync(host, port, ct))
            {
                await SendAsync(login, BotPacketBuilder.BuildLoginSeed(0x7F000001), ct);
                await SendAsync(login, BotPacketBuilder.BuildAccountLogin(_account, SyntheticShard.Password), ct);
                var list = await ReadRawPacketAsync(login, ct);
                if (list[0] != 0xA8) return Fail($"expected 0xA8 server list, got 0x{list[0]:X2}");
                await SendAsync(login, BotPacketBuilder.BuildServerSelect(0), ct);
                var relay = await ReadRawPacketAsync(login, ct);
                if (relay[0] != 0x8C) return Fail($"expected 0x8C relay, got 0x{relay[0]:X2}");
                authId = BinaryPrimitives.ReadUInt32BigEndian(relay.AsSpan(7));
            }

            _socket = await ConnectAsync(host, port, ct);
            _charListSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _loginConfirmed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() => ReceiveLoopAsync(ct), ct);
            await SendAsync(_socket, BotPacketBuilder.BuildLoginSeed(authId), ct);
            await SendAsync(_socket, BotPacketBuilder.BuildGameLogin(_account, SyntheticShard.Password, authId), ct);

            if (!await WaitAsync(_charListSeen.Task, 30_000, ct))
                return Fail("no 0xA9 character list");
            await SendAsync(_socket, BotPacketBuilder.BuildCharSelect(0, _charName), ct);
            if (!await WaitAsync(_loginConfirmed.Task, 30_000, ct))
                return Fail("no 0x1B login confirm");

            LoggedIn = true;
            LoginMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>The scripted behaviour: a step every 420-480 ms (mostly straight
    /// ahead, sometimes a turn, drifting back home when it strays), a line of speech
    /// every ~6 s and a double-click every ~4 s on an item it has seen or on itself.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        if (_socket == null) return;
        long freq = Stopwatch.Frequency;
        long now = Stopwatch.GetTimestamp();
        long nextStep = now + _rng.Next(0, 450) * freq / 1000;
        long nextSpeech = now + _rng.Next(1000, 6000) * freq / 1000;
        long nextClick = now + _rng.Next(500, 4000) * freq / 1000;
        try
        {
            while (!ct.IsCancellationRequested && !Disconnected)
            {
                now = Stopwatch.GetTimestamp();
                if (now >= nextStep)
                {
                    SendStep();
                    nextStep = now + _rng.Next(420, 481) * freq / 1000;
                }
                if (now >= nextSpeech)
                {
                    Speeches++;
                    Send(BotPacketBuilder.BuildSpeech($"load {_id} says {_rng.Next(1000)}"));
                    nextSpeech = now + _rng.Next(5000, 7000) * freq / 1000;
                }
                if (now >= nextClick)
                {
                    DoubleClicks++;
                    Send(BotPacketBuilder.BuildDoubleClick(PickClickTarget()));
                    nextClick = now + _rng.Next(3000, 5000) * freq / 1000;
                }
                long wake = Math.Min(nextStep, Math.Min(nextSpeech, nextClick));
                int delayMs = (int)Math.Max(1, (wake - Stopwatch.GetTimestamp()) * 1000 / freq);
                await Task.Delay(delayMs, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Close()
    {
        try { _socket?.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket?.Dispose(); } catch { }
    }

    private void SendStep()
    {
        byte dir;
        int dist = Math.Max(Math.Abs(_x - _homeX), Math.Abs(_y - _homeY));
        if (dist > 10)
            dir = DirectionTo(_homeX - _x, _homeY - _y);
        else if (_rng.Next(100) < 80)
            dir = _facing;
        else
            dir = (byte)_rng.Next(8);

        byte seq;
        lock (_stepLock)
        {
            if (_stepsInFlight >= MaxStepsInFlight)
            {
                // Write off steps the server never answered so a lost answer cannot
                // stall this bot for the rest of the run; they count as lost.
                long cutoff = Stopwatch.GetTimestamp() - 5 * Stopwatch.Frequency;
                for (int s = 0; s < 256; s++)
                {
                    if (_stepSentTs[s] != 0 && _stepSentTs[s] < cutoff)
                    {
                        _stepSentTs[s] = 0;
                        _stepsInFlight--;
                        StepsLost++;
                    }
                }
                if (_stepsInFlight >= MaxStepsInFlight)
                    return;
            }
            seq = _nextSeq;
            _nextSeq = seq == 255 ? (byte)1 : (byte)(seq + 1);
            _stepsInFlight++;
            _stepSentTs[seq] = Stopwatch.GetTimestamp();
            _stepMeasured[seq] = Measuring;
            MovesSent++;
            if (Measuring) MovesSentMeasured++;
            // A step in the facing direction moves; any other direction is a turn.
            if (dir == _facing)
            {
                _x += Dx[dir];
                _y += Dy[dir];
            }
            _facing = dir;
        }
        Send(BotPacketBuilder.BuildMoveRequest(dir, seq));
    }

    private uint PickClickTarget()
    {
        int count = Volatile.Read(ref _knownItemCount);
        if (count == 0 || _rng.Next(4) == 0)
            return _serial; // self: paperdoll
        return _knownItems[_rng.Next(Math.Min(count, _knownItems.Length))];
    }

    private static byte DirectionTo(int dx, int dy)
    {
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        for (byte d = 0; d < 8; d++)
            if (Dx[d] == sx && Dy[d] == sy)
                return d;
        return 0;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        var socket = _socket!;
        var buffer = new byte[64 * 1024];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int read = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct);
                if (read <= 0) break;
                Interlocked.Add(ref BytesIn, read);
                _decoder.Feed(buffer.AsSpan(0, read), OnPacket);
            }
        }
        catch (OperationCanceledException) { return; }
        catch (Exception) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            FailReason ??= "receive: " + ex.Message;
        }
        if (!ct.IsCancellationRequested)
        {
            Disconnected = true;
            if (Measuring) DisconnectedWhileMeasuring = true;
            _charListSeen?.TrySetResult(false);
            _loginConfirmed?.TrySetResult(false);
        }
    }

    private void OnPacket(ReadOnlySpan<byte> p)
    {
        long now = Stopwatch.GetTimestamp();
        PacketsIn++;
        switch (p[0])
        {
            case 0x22 when p.Length >= 2:
                AnswerStep(p[1], now, accepted: true);
                break;
            case 0x21 when p.Length >= 8:
            {
                AnswerStep(p[1], now, accepted: false);
                lock (_stepLock)
                {
                    // The server pins its sequence to 0 on a reject and ignores what
                    // was already in flight.
                    for (int s = 0; s < 256; s++)
                    {
                        if (_stepSentTs[s] != 0)
                        {
                            _stepSentTs[s] = 0;
                            StepsDroppedByReject++;
                        }
                    }
                    _stepsInFlight = 0;
                    _nextSeq = 0;
                    _x = BinaryPrimitives.ReadUInt16BigEndian(p[2..]);
                    _y = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
                    _facing = (byte)(p[6] & 0x07);
                }
                break;
            }
            case 0x1A when p.Length >= 7:
            {
                uint serial = BinaryPrimitives.ReadUInt32BigEndian(p[3..]) & 0x7FFFFFFF;
                if ((serial & 0x40000000) == 0)
                {
                    _knownItems[_knownItemCursor] = serial;
                    _knownItemCursor = (_knownItemCursor + 1) % _knownItems.Length;
                    if (_knownItemCount < _knownItems.Length)
                        Volatile.Write(ref _knownItemCount, _knownItemCount + 1);
                }
                break;
            }
            case 0xA9:
                _charListSeen?.TrySetResult(true);
                break;
            case 0x1B when p.Length >= 17:
                _serial = BinaryPrimitives.ReadUInt32BigEndian(p[1..]);
                _x = _homeX = BinaryPrimitives.ReadUInt16BigEndian(p[11..]);
                _y = _homeY = BinaryPrimitives.ReadUInt16BigEndian(p[13..]);
                _facing = (byte)(p[17 < p.Length ? 17 : 16] & 0x07);
                _loginConfirmed?.TrySetResult(true);
                break;
            case 0x20 when p.Length >= 19:
                lock (_stepLock)
                {
                    if (BinaryPrimitives.ReadUInt32BigEndian(p[1..]) == _serial)
                    {
                        _x = BinaryPrimitives.ReadUInt16BigEndian(p[11..]);
                        _y = BinaryPrimitives.ReadUInt16BigEndian(p[13..]);
                        _facing = (byte)(p[17] & 0x07);
                    }
                }
                break;
        }
    }

    private void AnswerStep(byte seq, long now, bool accepted)
    {
        lock (_stepLock)
        {
            long sent = _stepSentTs[seq];
            if (sent == 0)
                return; // an answer to a step a reject already wrote off
            _stepSentTs[seq] = 0;
            if (_stepsInFlight > 0) _stepsInFlight--;
            if (accepted) Acks++; else Rejects++;
            if (_stepMeasured[seq] && Measuring)
                AckLatencyUs.Add((int)Math.Min(int.MaxValue, (now - sent) * 1_000_000 / Stopwatch.Frequency));
        }
    }

    private void Send(byte[] packet)
    {
        var socket = _socket;
        if (socket == null || Disconnected) return;
        try
        {
            // Small packets on a NoDelay socket: a blocking send completes at once
            // unless the server stopped reading, which is itself the failure to see.
            socket.Send(packet);
            Interlocked.Add(ref BytesOut, packet.Length);
        }
        catch (Exception ex)
        {
            FailReason ??= "send: " + ex.Message;
            Disconnected = true;
            if (Measuring) DisconnectedWhileMeasuring = true;
        }
    }

    private async Task SendAsync(Socket socket, byte[] packet, CancellationToken ct)
    {
        await socket.SendAsync(packet.AsMemory(), SocketFlags.None, ct);
        Interlocked.Add(ref BytesOut, packet.Length);
    }

    private static async Task<Socket> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };
        await socket.ConnectAsync(host, port, ct);
        return socket;
    }

    /// <summary>Login-connection packets are not compressed: 0xA8 is variable, 0x8C is
    /// 11 bytes, 0x82 (refused) is 2.</summary>
    private async Task<byte[]> ReadRawPacketAsync(Socket socket, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(30_000);
        var op = await ReadExactAsync(socket, 1, timeout.Token);
        int length = op[0] switch
        {
            0x8C => 11,
            0x82 => 2,
            0xA8 => -1,
            _ => throw new InvalidDataException($"unexpected login packet 0x{op[0]:X2}"),
        };
        if (length == -1)
        {
            var len = await ReadExactAsync(socket, 2, timeout.Token);
            length = (len[0] << 8) | len[1];
            var rest = await ReadExactAsync(socket, length - 3, timeout.Token);
            var packet = new byte[length];
            packet[0] = op[0];
            packet[1] = len[0];
            packet[2] = len[1];
            rest.CopyTo(packet, 3);
            return packet;
        }
        var body = await ReadExactAsync(socket, length - 1, timeout.Token);
        var fixedPacket = new byte[length];
        fixedPacket[0] = op[0];
        body.CopyTo(fixedPacket, 1);
        if (fixedPacket[0] == 0x82)
            throw new InvalidDataException($"login refused (reason {fixedPacket[1]})");
        return fixedPacket;
    }

    private async Task<byte[]> ReadExactAsync(Socket socket, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int done = 0;
        while (done < count)
        {
            int n = await socket.ReceiveAsync(buffer.AsMemory(done), SocketFlags.None, ct);
            if (n <= 0) throw new IOException("connection closed during login");
            done += n;
        }
        Interlocked.Add(ref BytesIn, count);
        return buffer;
    }

    private static async Task<bool> WaitAsync(Task<bool> task, int timeoutMs, CancellationToken ct)
    {
        var done = await Task.WhenAny(task, Task.Delay(timeoutMs, ct));
        return done == task && task.Result;
    }

    private bool Fail(string reason)
    {
        FailReason = reason;
        return false;
    }
}
