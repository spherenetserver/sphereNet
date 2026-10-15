using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using SphereNet.Network.Encryption;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// A simulated UO client that connects via TCP and performs automated actions.
/// Used for stress testing the server with realistic network load.
/// </summary>
public sealed class BotClient : IDisposable
{
    private readonly ILogger _logger;
    private readonly Random _rng;
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _behaviorTask;
    private Task? _receiveTask;
    
    private readonly string _accountName;
    private readonly string _password;
    private readonly string _charName;
    private readonly int _botId;
    
    private byte _moveSequence;
    // The direction each in-flight move went out with, by sequence. 0x22 carries
    // only the sequence and the client applies the step itself.
    private readonly byte[] _stepDirBySeq = new byte[256];
    // The behaviour loop, the trade-accept task and the action API all write to the
    // one stream; two writes at once could interleave their bytes.
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    // Set by 0x55, which ends the login burst. A step sent before the burst's own
    // 0x20 lands is answered on the old sequence while the 0x20 resets the bot's,
    // and the next step is refused as out of order.
    private readonly TaskCompletionSource _loginBurstDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private uint _authId;
    private uint _lastShopVendor;
    private uint _charUid;
    private short _x, _y;
    private sbyte _z;
    private bool _disposed;

    private readonly BotWorldModel _world = new();
    private BotAI? _ai;
    private BotAnomalyScanner? _anomalyScanner;
    private System.Collections.Concurrent.ConcurrentQueue<BotAnomaly>? _anomalySink;
    
    // Buffers for game server packets (Huffman compressed)
    private readonly List<byte> _decompressedBuffer = new();
    private readonly List<byte> _compressedBuffer = new(); // Raw compressed data
    private bool _isGamePhase;

    /// <summary>Client version the bot announces, packed major &lt;&lt; 24 | minor &lt;&lt; 16 |
    /// build &lt;&lt; 8 | extra. Default 7.0.0.0.</summary>
    public uint ClientVersion { get; init; } = ReferenceClientProtocol.Pack(7, 0, 0, 0);

    /// <summary>Open the game connection the way the 2D client does: the relay's auth
    /// id as a raw 4-byte seed, no 0xEF, so the server knows the version only from
    /// the relay and from the 0xBD answer. Off: an 0xEF seed with the version.</summary>
    public bool RawGameSeed { get; init; }

    /// <summary>Every packet the server sent on the game connection, whole and
    /// exactly as written (the server compresses each packet on its own, so one
    /// decoded Huffman unit is one packet).</summary>
    public Action<byte[]>? ServerPacketObserver { get; set; }

    public BotState State { get; internal set; } = BotState.Disconnected;
    public int PacketsSent { get; private set; }
    public int PacketsReceived { get; private set; }
    public int BytesSent { get; private set; }
    public int BytesReceived { get; private set; }
    public long ConnectTimeMs { get; private set; }
    public long LastActivityMs { get; private set; }
    public int BotId => _botId;
    public BotWorldModel World => _world;

    private BotActionApi? _actionApi;
    public BotActionApi Actions => _actionApi ??= new BotActionApi(this);

    public void SetAnomalySink(System.Collections.Concurrent.ConcurrentQueue<BotAnomaly> sink)
    {
        _anomalySink = sink;
        _anomalyScanner = new BotAnomalyScanner(_botId, _world);
    }

    public const string AccountPrefix = "spherenetBot";
    public const string CharPrefix = "SphereBot";

    public BotClient(int botId, ILogger logger, string? password = null)
    {
        _botId = botId;
        _logger = logger;
        _rng = new Random(botId * 31337);
        _accountName = $"{AccountPrefix}{botId:D4}";
        _password = string.IsNullOrEmpty(password) ? "botpass" : password;
        _charName = $"{CharPrefix}{botId}";
    }

    /// <summary>The account this bot logs in with. The prefix is one a player can
    /// never create (it is a reserved save-section family, AccountNameValidator), so
    /// the engine makes these accounts itself (BotEngine.EnsureAccount).</summary>
    public string AccountName => _accountName;

    /// <summary>Name-shape test only, for cleanup. A privilege asks
    /// <see cref="BotEngine.IsLiveBotAccount"/>.</summary>
    public static bool IsBotAccountName(string name) => 
        name.StartsWith(AccountPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Name-shape test only. NEVER use this to grant a privilege: the
    /// character name is chosen by the player, so an ordinary "SphereBotanist"
    /// matches. Ask <see cref="BotEngine.IsLiveBotCharacter"/> instead, which knows
    /// which characters the server itself is driving.</summary>
    public static bool IsBotCharName(string name) => 
        name.StartsWith(CharPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The character name this bot session logs in as.</summary>
    public string CharName => _charName;

    public async Task<bool> ConnectAndLoginAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            State = BotState.Connecting;
            long startMs = Environment.TickCount64;

            // === PHASE 1: Login Server ===
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.ReceiveTimeout = 10000;
            _tcp.SendTimeout = 5000;

            await _tcp.ConnectAsync(host, port, ct);
            _stream = _tcp.GetStream();
            
            ConnectTimeMs = Environment.TickCount64 - startMs;
            State = BotState.LoggingIn;

            // Step 1: Login Seed + Account Login
            uint seed = (uint)(_rng.Next() & 0x7FFFFFFF);
            await SendPacketAsync(VersionSeed(seed), ct);
            await SendPacketAsync(BotPacketBuilder.BuildAccountLogin(_accountName, _password), ct);
            
            // Step 2: Wait for server list (0xA8). A client that sent no version in its
            // seed is asked for it first (0xBD) and answers before the list arrives.
            var response = await ReadPacketAsync(ct);
            for (int asked = 0; response != null && response[0] == 0xBD && asked < 3; asked++)
            {
                PacketsReceived++;
                await SendPacketAsync(BotPacketBuilder.BuildClientVersion(VersionText), ct);
                response = await ReadPacketAsync(ct);
            }
            if (response == null)
            {
                _logger.LogWarning("[Bot{Id}] Step 2 FAIL: No response after account login", _botId);
                return false;
            }
            if (response[0] != 0xA8)
            {
                _logger.LogWarning("[Bot{Id}] Step 2 FAIL: Expected 0xA8 (ServerList), got 0x{Op:X2}", _botId, response[0]);
                return false;
            }
            PacketsReceived++;

            // Step 3: Select server
            await SendPacketAsync(BotPacketBuilder.BuildServerSelect(0), ct);

            // Step 4: Wait for relay (0x8C)
            response = await ReadPacketAsync(ct);
            if (response == null)
            {
                _logger.LogWarning("[Bot{Id}] Step 4 FAIL: No response after server select", _botId);
                return false;
            }
            if (response[0] != 0x8C)
            {
                _logger.LogWarning("[Bot{Id}] Step 4 FAIL: Expected 0x8C (Relay), got 0x{Op:X2}", _botId, response[0]);
                return false;
            }
            PacketsReceived++;
            
            // Parse relay info: IP (4 bytes), port (2 bytes), authId (4 bytes)
            // Packet format: [0x8C] [IP 4 bytes] [Port 2 bytes] [AuthId 4 bytes]
            string gameHost = $"{response[1]}.{response[2]}.{response[3]}.{response[4]}";
            int gamePort = (response[5] << 8) | response[6];
            _authId = ReadUInt32BE(response, 7);

            // Close login server connection
            _stream?.Close();
            _tcp?.Close();
            
            // Small delay before reconnecting
            await Task.Delay(50, ct);

            // === PHASE 2: Game Server (reconnect) ===
            _tcp = new TcpClient();
            _tcp.NoDelay = true;
            _tcp.ReceiveTimeout = 10000;
            _tcp.SendTimeout = 5000;

            // Connect to game server (use original host if relay says 127.0.0.1)
            string connectHost = (gameHost == "127.0.0.1") ? host : gameHost;
            await _tcp.ConnectAsync(connectHost, gamePort, ct);
            _stream = _tcp.GetStream();

            // Step 5: Game server login (need to send seed again for game server)
            seed = (uint)(_rng.Next() & 0x7FFFFFFF);
            await SendPacketAsync(RawGameSeed
                ? [(byte)(_authId >> 24), (byte)(_authId >> 16), (byte)(_authId >> 8), (byte)_authId]
                : VersionSeed(seed), ct);
            await SendPacketAsync(BotPacketBuilder.BuildGameLogin(_accountName, _password, _authId), ct);
            await _stream.FlushAsync(ct);

            // Small delay to let server process and respond
            await Task.Delay(100, ct);

            // Mark that we are now in game phase (Huffman compressed packets)
            _isGamePhase = true;
            _decompressedBuffer.Clear();
            _compressedBuffer.Clear();

            // Step 6: Wait for char list (0xA9) - may receive other packets first (0xB9 features, etc)
            bool gotCharList = false;
            var receivedPackets = new List<byte>();
            
            // Check if data is available
            if (!_tcp.Connected)
            {
                _logger.LogWarning("[Bot{Id}] Step 6: TCP disconnected after game login", _botId);
                return false;
            }

            for (int i = 0; i < 20 && !gotCharList; i++)
            {
                response = await ReadGamePacketAsync(ct, 10000); // 10 second timeout, decompressed
                if (response == null) 
                {
                    _logger.LogWarning("[Bot{Id}] Step 6: Timeout waiting for char list (attempt {I}, connected={Connected}, dataAvail={Avail}, bufLen={BufLen})", 
                        _botId, i, _tcp?.Connected ?? false, _stream?.DataAvailable ?? false, _decompressedBuffer.Count);
                    break;
                }
                PacketsReceived++;
                receivedPackets.Add(response[0]);
                
                if (response[0] == 0xA9) // Char list
                    gotCharList = true;
            }

            if (!gotCharList)
            {
                _logger.LogWarning("[Bot{Id}] Step 6 FAIL: Did not receive char list. Got packets: [{Packets}]", 
                    _botId, string.Join(", ", receivedPackets.Select(p => $"0x{p:X2}")));
                return false;
            }

            // Step 7: Select character (slot 0)
            await SendPacketAsync(BotPacketBuilder.BuildCharSelect(0, _charName), ct);

            // Step 8: Wait for login confirm (0x1B)
            bool loggedIn = false;
            receivedPackets.Clear();
            for (int i = 0; i < 30 && !loggedIn; i++)
            {
                response = await ReadGamePacketAsync(ct); // Use game packet reader
                if (response == null) 
                {
                    _logger.LogWarning("[Bot{Id}] Step 8: Timeout waiting for login confirm (attempt {I})", _botId, i);
                    break;
                }
                PacketsReceived++;
                receivedPackets.Add(response[0]);
                
                if (response[0] == 0x1B) // Login confirm
                {
                    _charUid = ReadUInt32BE(response, 1);
                    _x = (short)ReadUInt16BE(response, 11);
                    _y = (short)ReadUInt16BE(response, 13);
                    _z = (sbyte)response[16];
                    _world.Direction = (byte)(response[17] & 0x07);
                    loggedIn = true;
                }
            }

            if (loggedIn)
            {
                State = BotState.Playing;
                LastActivityMs = Environment.TickCount64;
                _world.CharUid = _charUid;
                BotEngine.RegisterBotSerial(_charUid);
                _world.X = _x;
                _world.Y = _y;
                _world.Z = _z;
                _world.NoteServerPosition(_x, _y, _z);
                _world.MaxHits = 100;
                _world.Hits = 100;
                return true;
            }

            _logger.LogWarning("[Bot{Id}] Step 8 FAIL: Did not receive login confirm (0x1B). Got packets: [{Packets}]",
                _botId, string.Join(", ", receivedPackets.Select(p => $"0x{p:X2}")));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("[Bot{Id}] Login exception: {Error}", _botId, ex.Message);
            State = BotState.Disconnected;
            return false;
        }
    }

    public void StartBehavior(BotBehavior behavior, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _behaviorTask = Task.Run(() => RunBehaviorLoopAsync(behavior, _cts.Token));
    }

    public void StartBehavior(Behaviors.IBotBehavior roleBehavior, CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _receiveTask = Task.Run(() => ReceiveLoopAsync(_cts.Token));
        _behaviorTask = Task.Run(() => RunRoleBehaviorAsync(roleBehavior, _cts.Token));
    }

    /// <summary>Sole socket reader once the bot is playing: continuously reads
    /// packets and updates the world model + completes pending actions, so the
    /// behavior thread sees a current world and its WaitResult calls return
    /// promptly. Behaviors must NOT read the socket themselves (no draining) —
    /// two readers on one stream would corrupt it.</summary>
    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && State == BotState.Playing)
            {
                byte[]? packet = _isGamePhase
                    ? await ReadGamePacketAsync(ct, timeout: 200)
                    : await ReadPacketAsync(ct, timeout: 200);
                if (packet != null)
                {
                    PacketsReceived++;
                    ProcessIncomingPacket(packet);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] Receive loop error: {Error}", _botId, ex.Message);
        }
    }

    /// <summary>Wait out the login burst and a second more, as a client loads its
    /// world before the player can walk. The server redraws a character that has just
    /// come in (appearance, facing); a first step at sequence 0 that crosses that 0x20
    /// is accepted on the reset sequence, and the bot's own reset then sends 0 again.</summary>
    private async Task WaitForLoginBurstAsync(CancellationToken ct)
    {
        await Task.WhenAny(_loginBurstDone.Task, Task.Delay(5000, ct));
        await Task.Delay(1000, ct);
    }

    private async Task RunRoleBehaviorAsync(Behaviors.IBotBehavior roleBehavior, CancellationToken ct)
    {
        try
        {
            await WaitForLoginBurstAsync(ct);
            while (!ct.IsCancellationRequested && State == BotState.Playing)
            {
                // Incoming packets are drained by ReceiveLoopAsync (sole reader).
                await roleBehavior.RunAsync(this, Actions, ct);
                LastActivityMs = Environment.TickCount64;
                if (_anomalyScanner != null && _anomalySink != null)
                    _anomalyScanner.Tick(LastActivityMs, _anomalySink);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] Role behavior error: {Error}", _botId, ex.Message);
        }
        finally
        {
            if (_anomalySink != null)
                _anomalySink.Enqueue(new BotAnomaly
                {
                    Type = BotAnomalyType.UnexpectedDisconnect,
                    BotId = _botId,
                    TimestampMs = Environment.TickCount64,
                    Detail = "Bot disconnected during role behavior loop",
                    Severity = BotAnomalySeverity.Error,
                });
            State = BotState.Disconnected;
        }
    }

    private async Task RunBehaviorLoopAsync(BotBehavior behavior, CancellationToken ct)
    {
        try
        {
            await WaitForLoginBurstAsync(ct);
            while (!ct.IsCancellationRequested && State == BotState.Playing)
            {
                // Incoming packets are drained by ReceiveLoopAsync (sole reader).
                switch (behavior)
                {
                    case BotBehavior.Idle:
                        await Task.Delay(1000, ct);
                        break;
                    case BotBehavior.RandomWalk:
                        await DoRandomWalkAsync(ct);
                        break;
                    case BotBehavior.Combat:
                        await DoCombatAsync(ct);
                        break;
                    case BotBehavior.FullSimulation:
                        await DoFullSimulationAsync(ct);
                        break;
                    case BotBehavior.SmartAI:
                        await DoSmartAIAsync(ct);
                        break;
                }

                LastActivityMs = Environment.TickCount64;
                if (_anomalyScanner != null && _anomalySink != null)
                    _anomalyScanner.Tick(LastActivityMs, _anomalySink);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] Behavior error: {Error}", _botId, ex.Message);
        }
        finally
        {
            if (_anomalySink != null)
                _anomalySink.Enqueue(new BotAnomaly
                {
                    Type = BotAnomalyType.UnexpectedDisconnect,
                    BotId = _botId,
                    TimestampMs = Environment.TickCount64,
                    Detail = "Bot disconnected during behavior loop",
                    Severity = BotAnomalySeverity.Error,
                });
            State = BotState.Disconnected;
        }
    }

    private async Task DrainIncomingPacketsAsync(CancellationToken ct)
    {
        try
        {
            if (_stream == null || !_tcp!.Connected) return;
            
            // Keep reading while there's data in the stream or decompressed buffer
            while ((_stream.DataAvailable || _decompressedBuffer.Count > 0) && !ct.IsCancellationRequested)
            {
                var packet = _isGamePhase 
                    ? await ReadGamePacketAsync(ct, timeout: 50) 
                    : await ReadPacketAsync(ct, timeout: 50);
                    
                if (packet == null) break;
                
                PacketsReceived++;
                ProcessIncomingPacket(packet);
            }
        }
        catch { }
    }

    internal void ProcessIncomingPacket(byte[] packet)
    {
        if (packet.Length == 0) return;
        long now = Environment.TickCount64;

        // The receive loop runs on its own thread; guard world-model mutations
        // against the behavior thread's reads (FindNearest etc.).
        lock (_world.SyncRoot)
        {
        switch (packet[0])
        {
            case 0x20: // Draw Game Player (19 bytes)
                if (packet.Length >= 19)
                {
                    // A redraw resets the walk sequence on both sides (the server
                    // pins its own to 0 when it sends one). It is also how the server
                    // places the character after a teleport, so the jump it carries is
                    // not an anomaly.
                    _moveSequence = 0;
                    _world.Body = ReadUInt16BE(packet, 5);
                    _world.X = _x = (short)ReadUInt16BE(packet, 11);
                    _world.Y = _y = (short)ReadUInt16BE(packet, 13);
                    _world.Direction = (byte)(packet[17] & 0x07);
                    _world.Z = _z = (sbyte)packet[18];
                    _world.NoteServerPosition(_x, _y, _z);
                }
                break;

            case 0x78: // Draw Object (mobile appears)
                if (packet.Length >= 19)
                {
                    uint serial78 = ReadUInt32BE(packet, 3);
                    if (serial78 == _charUid)
                    {
                        ReadOwnEquipment(packet, now);
                        break;
                    }
                    if ((serial78 & 0x40000000) != 0) break;
                    _world.Mobiles[serial78] = new KnownMobile
                    {
                        Serial = serial78,
                        Body = ReadUInt16BE(packet, 7),
                        X = (short)ReadUInt16BE(packet, 9),
                        Y = (short)ReadUInt16BE(packet, 11),
                        Z = (sbyte)packet[13],
                        Notoriety = packet[18],
                        LastSeenMs = now
                    };
                }
                break;

            case 0x77: // Mobile Moving (17 bytes)
                if (packet.Length >= 17)
                {
                    uint serial77 = ReadUInt32BE(packet, 1);
                    if (serial77 == _charUid) break;
                    if (_world.Mobiles.TryGetValue(serial77, out var mob77))
                    {
                        mob77.Body = ReadUInt16BE(packet, 5);
                        mob77.X = (short)ReadUInt16BE(packet, 7);
                        mob77.Y = (short)ReadUInt16BE(packet, 9);
                        mob77.Z = (sbyte)packet[11];
                        mob77.Notoriety = packet[16];
                        mob77.LastSeenMs = now;
                    }
                    else
                    {
                        _world.Mobiles[serial77] = new KnownMobile
                        {
                            Serial = serial77,
                            Body = ReadUInt16BE(packet, 5),
                            X = (short)ReadUInt16BE(packet, 7),
                            Y = (short)ReadUInt16BE(packet, 9),
                            Z = (sbyte)packet[11],
                            Notoriety = packet[16],
                            LastSeenMs = now
                        };
                    }
                }
                break;

            case 0x1D: // Delete Object (5 bytes)
                if (packet.Length >= 5)
                {
                    uint serialDel = ReadUInt32BE(packet, 1);
                    _world.Mobiles.Remove(serialDel);
                    _world.KnownItems.Remove(serialDel);
                    ForgetFromContainers(serialDel);
                    if (_world.OpenContainers.Remove(serialDel, out var cont))
                        foreach (var ci in cont.Items)
                            _world.KnownItems.Remove(ci.Serial);
                }
                break;

            case 0xA1: // Health Update (9 bytes)
                if (packet.Length >= 9)
                {
                    uint serialHp = ReadUInt32BE(packet, 1);
                    if (serialHp == _charUid)
                    {
                        _world.MaxHits = (short)ReadUInt16BE(packet, 5);
                        _world.Hits = (short)ReadUInt16BE(packet, 7);
                        _world.IsDead = _world.Hits <= 0 && _world.MaxHits > 0;
                    }
                }
                break;

            case 0x11: // Status: gold at 58 in the full (type >= 1) form for the player
                if (packet.Length >= 62 && ReadUInt32BE(packet, 3) == _charUid && packet[42] >= 1)
                    _world.StatusGold = (int)ReadUInt32BE(packet, 58);
                break;

            case 0xA2: // Mana Update (9 bytes)
                if (packet.Length >= 9)
                {
                    uint serialMana = ReadUInt32BE(packet, 1);
                    if (serialMana == _charUid)
                    {
                        _world.MaxMana = (short)ReadUInt16BE(packet, 5);
                        _world.Mana = (short)ReadUInt16BE(packet, 7);
                    }
                }
                break;

            case 0xA3: // Stam Update (9 bytes)
                if (packet.Length >= 9)
                {
                    uint serialStam = ReadUInt32BE(packet, 1);
                    if (serialStam == _charUid)
                    {
                        _world.MaxStam = (short)ReadUInt16BE(packet, 5);
                        _world.Stam = (short)ReadUInt16BE(packet, 7);
                    }
                }
                break;

            case 0x22: // Move Ack (3 bytes)
                if (packet.Length >= 2)
                    ApplyAcceptedStep(packet[1]);
                // packet[1] is the sequence of the move being answered. Without
                // comparing it, a late ack for a move that already timed out was
                // applied to whatever move is in flight now.
                if (_actionApi?.CompleteMove(packet.Length >= 2 ? packet[1] : (byte)0, true) != false)
                {
                    _world.MoveRejectCount = 0;
                    _world.ConsecutivePickupRejects = 0;
                    _world.LastActionResult = BotActionResult.Success;
                    _world.LastActionTimeMs = now;
                    _world.TotalMoveAcks++;
                }
                else
                {
                    _world.LateMoveAcks++;
                }
                break;

            case 0x21: // Move Rejected (8 bytes)
                if (packet.Length >= 8)
                {
                    // The correction applies whatever sequence it carries: the server
                    // is saying where this character really is.
                    _world.MoveRejectCount++;
                    _world.TotalMoveRejects++;
                    short predictedX = _world.X, predictedY = _world.Y;
                    _world.X = _x = (short)ReadUInt16BE(packet, 2);
                    _world.Y = _y = (short)ReadUInt16BE(packet, 4);
                    _world.Direction = (byte)(packet[6] & 0x07);
                    _world.Z = _z = (sbyte)packet[7];
                    _world.NoteCorrection(predictedX, predictedY, _x, _y, _z);

                    // A refused step along the facing ran into the tile ahead.
                    byte refusedDir = _stepDirBySeq[packet[1]];
                    if ((refusedDir & 0x07) == _world.Direction)
                    {
                        var (bdx, bdy) = BotWorldModel.StepDelta(refusedDir);
                        _world.NoteBlocked((short)(_x + bdx), (short)(_y + bdy), now);
                    }
                    _world.LastActionTimeMs = now;

                    // The server pins its walk sequence to 0 on a reject and ignores
                    // the steps already in flight, which is what the real client's
                    // "clear the queue and resend from 0" is written against. A bot
                    // that kept counting up would be out of step with the server from
                    // the first reject onwards.
                    _moveSequence = 0;

                    // Whether it ANSWERS the move in flight is a different question.
                    if (_actionApi?.CompleteMove(packet[1], false) != false)
                        _world.LastActionResult = BotActionResult.Rejected;
                    else
                        _world.LateMoveAcks++;
                }
                break;

            case 0x6C: // Target Cursor (19 bytes)
                if (packet.Length >= 7)
                {
                    _world.HasPendingTarget = true;
                    _world.TargetCursorId = ReadUInt32BE(packet, 2);
                    _actionApi?.CompleteTarget();
                }
                break;

            case 0x1A: // World Item (variable)
                if (packet.Length >= 7)
                    HandleWorldItem(packet, now);
                break;

            case 0xF3: // World Item, 7.0.0+ form
                HandleWorldItemSA(packet, now);
                break;

            case 0x2C: // Death screen
                _world.DeathScreens++;
                break;

            case 0x3C: // Container Content (variable)
                if (packet.Length >= 5)
                {
                    HandleContainerContent(packet, now);
                    _actionApi?.CompletePickUp(true);
                }
                break;

            case 0x25: // Container Item Add (21 bytes, 6.0.1.7+)
                if (packet.Length >= 21)
                    HandleContainerItemAdd(packet, now);
                break;

            case 0x24: // Open Container (7 bytes to the 7.0.0 client the bot announces)
                if (packet.Length >= 7)
                {
                    uint contSerial24 = ReadUInt32BE(packet, 1);
                    ushort gumpId24 = ReadUInt16BE(packet, 5);
                    // Opening again starts from nothing: the 0x3C that follows (none
                    // for an empty container) lists all of it.
                    if (_world.OpenContainers.Remove(contSerial24, out var reopened))
                        foreach (var ci in reopened.Items)
                            ForgetKnown(ci);
                    _world.OpenContainers[contSerial24] = new BotContainerState
                        { Serial = contSerial24, GumpId = gumpId24 };
                    // Gump 0x30 is the shop window: the serial is the vendor.
                    if (gumpId24 == 0x0030)
                    {
                        _lastShopVendor = contSerial24;
                        if (_world.ActiveVendor != null) _world.ActiveVendor.VendorSerial = contSerial24;
                    }
                    _actionApi?.CompleteContainerOpen();
                }
                break;

            case 0xDD: // Compressed Gump (variable)
                if (packet.Length >= 23)
                {
                    uint gumpSerial = ReadUInt32BE(packet, 3);
                    uint gumpId = ReadUInt32BE(packet, 7);
                    int gumpX = (int)ReadUInt32BE(packet, 11);
                    int gumpY = (int)ReadUInt32BE(packet, 15);
                    _world.ActiveGump = new BotGumpState
                    {
                        Serial = gumpSerial,
                        GumpId = gumpId,
                        X = gumpX,
                        Y = gumpY,
                        IsOpen = true,
                        ReceivedMs = now,
                    };
                    _world.ActiveGumpReceivedMs = now;
                    _actionApi?.CompleteGump();
                }
                break;

            case 0x74: // Buy List (variable)
                if (packet.Length >= 8)
                    HandleBuyList(packet);
                break;

            case 0x9E: // Sell List (variable)
                if (packet.Length >= 9)
                    HandleSellList(packet);
                break;

            case 0x1C: // ASCII Message (variable)
                if (packet.Length >= 44)
                    HandleAsciiMessage(packet, now);
                break;

            case 0xAE: // Unicode Message (variable)
                if (packet.Length >= 48)
                    HandleUnicodeMessage(packet, now);
                break;

            case 0x2E: // Equip Item (15 bytes)
                if (packet.Length >= 15)
                {
                    uint eqSerial = ReadUInt32BE(packet, 1);
                    ushort eqItemId = ReadUInt16BE(packet, 5);
                    byte eqLayer = packet[8];
                    uint eqMobile = ReadUInt32BE(packet, 9);
                    ushort eqHue = ReadUInt16BE(packet, 13);
                    if (eqMobile == _charUid)
                    {
                        var eqItem = new BotKnownItem
                        {
                            Serial = eqSerial, ItemId = eqItemId,
                            Layer = eqLayer, Hue = eqHue, LastSeenMs = now,
                        };
                        _world.Equipment[eqLayer] = eqItem;
                        _world.KnownItems[eqSerial] = eqItem;
                        if (eqLayer == BackpackLayer)
                            _world.BackpackSerial = eqSerial;
                    }
                }
                break;

            case 0x55: // Login complete
                _loginBurstDone.TrySetResult();
                break;

            case 0x6F: // Secure Trade (variable)
                HandleSecureTrade(packet);
                break;

            case 0x27: // Reject Move Item (2 bytes)
                _world.LastMoveItemRejectReason = packet.Length >= 2 ? packet[1] : (byte)0xFF;
                _world.ConsecutivePickupRejects++;
                _world.LastActionResult = BotActionResult.Rejected;
                _world.LastActionTimeMs = now;
                _actionApi?.CompletePickUp(false);
                break;
        }
        }
    }

    private void HandleWorldItem(byte[] packet, long now)
    {
        // 0x1A: [opcode][len:2][serial:4][itemId:2][amount:2][x:2][y:2][z:1][dir:1][hue:2][flags:1]
        // Serial high bit indicates amount field present
        uint serial = ReadUInt32BE(packet, 3);
        bool hasAmount = (serial & 0x80000000) != 0;
        serial &= 0x7FFFFFFF;
        // 0x1A carries only items, whose serials have 0x40000000 set. Skipping those
        // as "mobiles" hid every ground item from the bot.

        int offset = 7;
        ushort itemId = ReadUInt16BE(packet, offset); offset += 2;
        bool hasStackId = (itemId & 0x8000) != 0;
        itemId &= 0x3FFF;
        if (hasStackId) offset += 2; // skip stack id

        ushort amount = 0;
        if (hasAmount && offset + 2 <= packet.Length)
        {
            amount = ReadUInt16BE(packet, offset);
            offset += 2;
        }

        if (offset + 5 > packet.Length) return;
        short x = (short)(ReadUInt16BE(packet, offset) & 0x7FFF);
        bool hasDir = (ReadUInt16BE(packet, offset) & 0x8000) != 0;
        offset += 2;
        short y = (short)(ReadUInt16BE(packet, offset) & 0x3FFF);
        bool hasHue = (ReadUInt16BE(packet, offset) & 0x8000) != 0;
        bool hasFlags = (ReadUInt16BE(packet, offset) & 0x4000) != 0;
        offset += 2;

        if (hasDir) offset++;
        sbyte z = offset < packet.Length ? (sbyte)packet[offset++] : (sbyte)0;
        ushort hue = 0;
        if (hasHue && offset + 2 <= packet.Length)
        {
            hue = ReadUInt16BE(packet, offset);
            offset += 2;
        }

        ForgetFromContainers(serial); // on the ground now, wherever it was
        _world.KnownItems[serial] = new BotKnownItem
        {
            Serial = serial, ItemId = itemId, X = x, Y = y, Z = z,
            Amount = amount, Hue = hue, LastSeenMs = now,
        };

        if (itemId == 0x0EED) // gold pile
            RecalcGold();
    }

    /// <summary>0xF3, the world item the server sends a 7.0.0+ client instead of 0x1A:
    /// [0xF3][0x0001][type][serial:4][graphic:2][inc:1][amount:2][amount:2][x:2][y:2]
    /// [z:1][light/dir:1][hue:2][flags:1] (+2 from 7.0.9). Type 2 is a mobile.</summary>
    private void HandleWorldItemSA(byte[] packet, long now)
    {
        if (packet.Length < 24 || packet[3] == 0x02) return;
        uint serial = ReadUInt32BE(packet, 4);
        ushort itemId = ReadUInt16BE(packet, 8);
        ushort amount = ReadUInt16BE(packet, 11);
        short x = (short)ReadUInt16BE(packet, 15);
        short y = (short)ReadUInt16BE(packet, 17);
        sbyte z = (sbyte)packet[19];
        ushort hue = ReadUInt16BE(packet, 21);
        ForgetFromContainers(serial);
        _world.KnownItems[serial] = new BotKnownItem
        {
            Serial = serial, ItemId = itemId, X = x, Y = y, Z = z,
            Amount = amount, Hue = hue, LastSeenMs = now,
        };
        if (itemId == 0x0EED) RecalcGold();
    }

    private void HandleContainerContent(byte[] packet, long now)
    {
        // 0x3C: [opcode][len:2][count:2] then per item (6.0.1.7+ = 20 bytes each)
        if (packet.Length < 5) return;
        ushort count = ReadUInt16BE(packet, 3);
        int offset = 5;

        // A 0x3C is the whole content of each container it names: what the bot held
        // for them before goes. Appending kept items long gone (given away, used up)
        // and offered them in trade, where the server answered "does not exist".
        var listed = new HashSet<uint>();
        var listedOrder = new HashSet<uint>();
        uint parentSerial = 0;
        for (int i = 0; i < count && offset + 20 <= packet.Length; i++)
        {
            uint itemSerial = ReadUInt32BE(packet, offset);
            ushort itemId = ReadUInt16BE(packet, offset + 4);
            offset += 7; // serial(4) + itemId(2) + unknown(1)
            ushort amount = ReadUInt16BE(packet, offset); offset += 2;
            short ix = (short)ReadUInt16BE(packet, offset); offset += 2;
            short iy = (short)ReadUInt16BE(packet, offset); offset += 2;
            offset++; // grid index (6.0.1.7+)
            uint contSerial = ReadUInt32BE(packet, offset); offset += 4;
            ushort hue = ReadUInt16BE(packet, offset); offset += 2;

            if (parentSerial == 0) parentSerial = contSerial;
            // The first item a packet names for a container starts that container's list.
            if (listedOrder.Add(contSerial) || !_world.LastContents.TryGetValue(contSerial, out var order))
                _world.LastContents[contSerial] = order = new List<BotKnownItem>();

            var item = new BotKnownItem
            {
                Serial = itemSerial, ItemId = itemId, Amount = amount,
                X = ix, Y = iy, Hue = hue,
                ContainerSerial = contSerial, LastSeenMs = now,
            };
            _world.KnownItems[itemSerial] = item;
            order.Add(item);

            if (_world.OpenContainers.TryGetValue(contSerial, out var cs))
            {
                if (listed.Add(contSerial))
                {
                    foreach (var old in cs.Items)
                        ForgetKnown(old);
                    cs.Items.Clear();
                    _world.KnownItems[itemSerial] = item;
                }
                cs.Items.Add(item);
            }
        }

        if (parentSerial != 0 && _world.OpenContainers.TryGetValue(parentSerial, out var container))
        {
            // items were already added above
        }

        RecalcGold();
    }

    /// <summary>Drop <paramref name="item"/> from KnownItems unless a newer entry for
    /// the same serial has replaced it.</summary>
    private void ForgetKnown(BotKnownItem item)
    {
        if (_world.KnownItems.TryGetValue(item.Serial, out var known) && ReferenceEquals(known, item))
            _world.KnownItems.Remove(item.Serial);
    }

    private void ForgetFromContainers(uint serial)
    {
        foreach (var cont in _world.OpenContainers.Values)
            cont.Items.RemoveAll(i => i.Serial == serial);
    }

    private void HandleContainerItemAdd(byte[] packet, long now)
    {
        // 0x25 (21 bytes, 6.0.1.7+): serial(4) itemId(2) unk(1) amount(2) x(2) y(2) grid(1) container(4) hue(2)
        uint serial = ReadUInt32BE(packet, 1);
        ushort itemId = ReadUInt16BE(packet, 5);
        ushort amount = ReadUInt16BE(packet, 8);
        short x = (short)ReadUInt16BE(packet, 10);
        short y = (short)ReadUInt16BE(packet, 12);
        uint contSerial = ReadUInt32BE(packet, 15);
        ushort hue = ReadUInt16BE(packet, 19);

        var item = new BotKnownItem
        {
            Serial = serial, ItemId = itemId, Amount = amount,
            X = x, Y = y, Hue = hue,
            ContainerSerial = contSerial, LastSeenMs = now,
        };
        _world.KnownItems[serial] = item;

        // An item arriving here has left wherever it was before.
        ForgetFromContainers(serial);
        if (_world.OpenContainers.TryGetValue(contSerial, out var cs))
            cs.Items.Add(item);

        _world.ConsecutivePickupRejects = 0;
        _world.LastActionResult = BotActionResult.Success;
        _world.LastActionTimeMs = now;

        if (itemId == 0x0EED) RecalcGold();
    }

    private void HandleBuyList(byte[] packet)
    {
        // 0x74: [opcode][len:2][shop container:4][count:1] then [price:4][name len:1][name]
        // per item. The entries carry no serial: the client pairs entry i with the
        // container's 0x3C items read from the last one back (ClassicUO BuyList), so
        // the bot does the same. The vendor is named by the 0x24 (gump 0x30) that
        // opens the window.
        uint containerSerial = ReadUInt32BE(packet, 3);
        byte count = packet[7];
        var vendor = new BotVendorState
        {
            ContainerSerial = containerSerial, VendorSerial = _lastShopVendor, IsBuyList = true,
        };
        _world.LastContents.TryGetValue(containerSerial, out var stock);

        int offset = 8;
        for (int i = 0; i < count && offset + 5 <= packet.Length; i++)
        {
            int price = (int)ReadUInt32BE(packet, offset); offset += 4;
            byte nameLen = packet[offset++];
            string name = nameLen > 0 && offset + nameLen <= packet.Length
                ? System.Text.Encoding.ASCII.GetString(packet, offset, nameLen) : "";
            offset += nameLen;
            var entry = new BotVendorItem { Price = price, Name = name };
            if (stock != null && i < stock.Count)
            {
                var stocked = stock[stock.Count - 1 - i];
                entry.Serial = stocked.Serial;
                entry.ItemId = stocked.ItemId;
                entry.Amount = stocked.Amount;
            }
            vendor.Items.Add(entry);
        }

        _world.ActiveVendor = vendor;
    }

    private void HandleSellList(byte[] packet)
    {
        // 0x9E: [opcode][len:2][vendorSerial:4][count:2] then items
        uint vendorSerial = ReadUInt32BE(packet, 3);
        ushort count = ReadUInt16BE(packet, 7);
        var vendor = new BotVendorState { VendorSerial = vendorSerial, IsBuyList = false };

        int offset = 9;
        for (int i = 0; i < count && offset + 14 <= packet.Length; i++)
        {
            uint itemSerial = ReadUInt32BE(packet, offset); offset += 4;
            ushort itemId = ReadUInt16BE(packet, offset); offset += 2;
            ushort hue = ReadUInt16BE(packet, offset); offset += 2;
            ushort amount = ReadUInt16BE(packet, offset); offset += 2;
            int price = (int)ReadUInt16BE(packet, offset); offset += 2;
            ushort nameLen = ReadUInt16BE(packet, offset); offset += 2;
            string name = nameLen > 0 && offset + nameLen <= packet.Length
                ? System.Text.Encoding.ASCII.GetString(packet, offset, nameLen) : "";
            offset += nameLen;
            vendor.Items.Add(new BotVendorItem
            {
                Serial = itemSerial, ItemId = itemId, Amount = amount,
                Price = price, Name = name,
            });
        }

        _world.ActiveVendor = vendor;
    }

    private void HandleAsciiMessage(byte[] packet, long now)
    {
        // 0x1C: [opcode][len:2][serial:4][body:2][type:1][hue:2][font:2][name:30][text:variable]
        if (packet.Length < 45) return;
        uint serial = ReadUInt32BE(packet, 3);
        int nameEnd = 14;
        string name = System.Text.Encoding.ASCII.GetString(packet, nameEnd, 30).TrimEnd('\0');
        int textStart = 44;
        int textLen = packet.Length - textStart - 1;
        if (textLen <= 0) return;
        string text = System.Text.Encoding.ASCII.GetString(packet, textStart, textLen).TrimEnd('\0');

        _world.Journal.Push(new BotJournalEntry
        {
            TimestampMs = now, SpeakerSerial = serial,
            Speaker = name, Text = text,
        });
    }

    private void HandleUnicodeMessage(byte[] packet, long now)
    {
        // 0xAE: [opcode][len:2][serial:4][body:2][type:1][hue:2][font:2][lang:4][name:30][text:unicode]
        if (packet.Length < 50) return;
        uint serial = ReadUInt32BE(packet, 3);
        string name = System.Text.Encoding.ASCII.GetString(packet, 18, 30).TrimEnd('\0');
        int textStart = 48;
        int textBytes = packet.Length - textStart;
        if (textBytes <= 0) return;
        string text = System.Text.Encoding.BigEndianUnicode
            .GetString(packet, textStart, textBytes).TrimEnd('\0');

        _world.Journal.Push(new BotJournalEntry
        {
            TimestampMs = now, SpeakerSerial = serial,
            Speaker = name, Text = text,
        });
    }

    private void RecalcGold()
    {
        int total = 0;
        foreach (var item in _world.KnownItems.Values)
            if (item.ItemId == 0x0EED)
                total += item.Amount == 0 ? 1 : item.Amount;
        _world.Gold = total;
    }

    private async Task DoRandomWalkAsync(CancellationToken ct)
    {
        byte dir = (byte)_rng.Next(0, 8);
        _world.TotalMoveRequests++;
        await SendPacketAsync(BotPacketBuilder.BuildMoveRequest(dir, NextMoveSequence(dir)), ct);
        await Task.Delay(_rng.Next(300, 700), ct);
    }

    private async Task DoCombatAsync(CancellationToken ct)
    {
        // Enable war mode
        await SendPacketAsync(BotPacketBuilder.BuildWarMode(true), ct);
        await Task.Delay(200, ct);
        
        // Attack a random UID (NPC nearby - simulated)
        uint targetUid = (uint)(0x00000001 + _rng.Next(0, 100000));
        await SendPacketAsync(BotPacketBuilder.BuildAttackRequest(targetUid), ct);
        await Task.Delay(_rng.Next(500, 1000), ct);
    }

    private async Task DoFullSimulationAsync(CancellationToken ct)
    {
        int action = _rng.Next(0, 100);
        
        if (action < 40) // 40% walk
        {
            await DoRandomWalkAsync(ct);
        }
        else if (action < 70) // 30% combat
        {
            await DoCombatAsync(ct);
        }
        else if (action < 90) // 20% double click (loot attempt)
        {
            uint targetUid = (uint)(0x40000001 + _rng.Next(0, 10000));
            await SendPacketAsync(BotPacketBuilder.BuildDoubleClick(targetUid), ct);
            await Task.Delay(_rng.Next(200, 500), ct);
        }
        else // 10% skill use
        {
            ushort skillId = (ushort)_rng.Next(0, 50);
            await SendPacketAsync(BotPacketBuilder.BuildSkillUse(skillId), ct);
            await Task.Delay(_rng.Next(500, 1000), ct);
        }
    }

    private async Task DoSmartAIAsync(CancellationToken ct)
    {
        _ai ??= new BotAI(_world, _rng);
        var action = _ai.Tick();
        await ExecuteActionAsync(action, ct);
    }

    private async Task ExecuteActionAsync(BotAction action, CancellationToken ct)
    {
        switch (action.Type)
        {
            case BotAIActionType.Move:
                _world.TotalMoveRequests++;
                await SendPacketAsync(BotPacketBuilder.BuildMoveRequest(action.Direction, NextMoveSequence(action.Direction)), ct);
                bool isRunning = (action.Direction & 0x80) != 0;
                int moveDelay = isRunning ? _rng.Next(100, 250) : _rng.Next(200, 450);
                await Task.Delay(moveDelay, ct);
                break;
            case BotAIActionType.Attack:
                await SendPacketAsync(BotPacketBuilder.BuildAttackRequest(action.TargetSerial), ct);
                await Task.Delay(_rng.Next(500, 1200), ct);
                break;
            case BotAIActionType.DoubleClick:
                await SendPacketAsync(BotPacketBuilder.BuildDoubleClick(action.TargetSerial), ct);
                await Task.Delay(_rng.Next(300, 600), ct);
                break;
            case BotAIActionType.EnableWarMode:
                _world.IsWarMode = true;
                await SendPacketAsync(BotPacketBuilder.BuildWarMode(true), ct);
                await Task.Delay(200, ct);
                break;
            case BotAIActionType.DisableWarMode:
                _world.IsWarMode = false;
                await SendPacketAsync(BotPacketBuilder.BuildWarMode(false), ct);
                await Task.Delay(200, ct);
                break;
            case BotAIActionType.Speech:
                if (action.Text != null)
                    await SendPacketAsync(BotPacketBuilder.BuildSpeech(action.Text), ct);
                await Task.Delay(_rng.Next(300, 600), ct);
                break;
            case BotAIActionType.Wait:
                await Task.Delay(Math.Max(100, action.DelayMs), ct);
                break;
            case BotAIActionType.None:
                await Task.Delay(200, ct);
                break;
        }
    }

    private const byte BackpackLayer = 0x15;

    /// <summary>This bot's own 0x78: the equipment list after the 19-byte header,
    /// each [serial][item id][layer] plus a hue when the id carries 0x8000 (the form
    /// sent to a pre-7.0.33 client, which the bot announces), ended by a zero serial.
    /// It is where the bot learns its backpack: nothing set BackpackSerial before,
    /// so the looting and trading bots never had a pack to use.</summary>
    private void ReadOwnEquipment(byte[] packet, long now)
    {
        int pos = 19;
        while (pos + 4 <= packet.Length)
        {
            uint serial = ReadUInt32BE(packet, pos);
            pos += 4;
            if (serial == 0 || pos + 3 > packet.Length)
                break;
            ushort itemId = ReadUInt16BE(packet, pos);
            byte layer = packet[pos + 2];
            pos += 3;
            ushort hue = 0;
            if ((itemId & 0x8000) != 0)
            {
                if (pos + 2 > packet.Length)
                    break;
                hue = ReadUInt16BE(packet, pos);
                pos += 2;
                itemId &= 0x7FFF;
            }
            var item = new BotKnownItem { Serial = serial, ItemId = itemId, Layer = layer, Hue = hue, LastSeenMs = now };
            _world.Equipment[layer] = item;
            _world.KnownItems[serial] = item;
            if (layer == BackpackLayer)
                _world.BackpackSerial = serial;
        }
    }

    /// <summary>A trade window opened with this bot: accept it after a moment, as a
    /// player would once both offers are in. Every bot accepts, so the bot that
    /// started the trade completes it with whichever bot it dropped the item on.</summary>
    private void HandleSecureTrade(byte[] packet)
    {
        if (packet.Length < 4)
            return;
        byte action = packet[3];
        if (action == 0 && packet.Length >= 12)
        {
            uint myContainer = ReadUInt32BE(packet, 8);
            long openedMs = Environment.TickCount64;
            _world.TradeContainer = myContainer;
            _world.TradeOpenedMs = openedMs;
            _world.TradeMyAccepted = _world.TradeTheirAccepted = false;
            BotEngine.CountTradeOpened();
            _ = Task.Run(async () =>
            {
                // Check this side, and check it again whenever the server clears it:
                // any change to either offer drops both marks (Source-X
                // Trade_Status(false)), and a bot that accepted only once left the
                // trade waiting on it for good.
                for (int second = 0; second < 15; second++)
                {
                    await Task.Delay(1000);
                    bool accept;
                    lock (_world.SyncRoot)
                    {
                        if (_world.TradeContainer != myContainer)
                            return;
                        accept = !_world.TradeMyAccepted;
                    }
                    if (accept && State == BotState.Playing)
                        SendRawPacket(BotPacketBuilder.BuildTradeAccept(myContainer, accept: true));
                }

                // Still open with this side checked: the server refused to complete
                // (weight, pack full, a script). A player would close it; left open,
                // both bots stay "already trading" for good.
                string? reason;
                lock (_world.SyncRoot)
                {
                    if (_world.TradeContainer != myContainer)
                        return;
                    reason = $"(me {(_world.TradeMyAccepted ? 1 : 0)}, them {(_world.TradeTheirAccepted ? 1 : 0)}) " +
                             (_world.LastSystemMessage(openedMs) ?? "no message");
                }
                BotEngine.CountTradeOutcome("left open " + reason);
                if (State == BotState.Playing)
                    SendRawPacket(BotPacketBuilder.BuildTradeClose(myContainer));
            });
        }
        else if (action == 2 && packet.Length >= 16 && ReadUInt32BE(packet, 4) == _world.TradeContainer)
        {
            _world.TradeMyAccepted = ReadUInt32BE(packet, 8) != 0;
            _world.TradeTheirAccepted = ReadUInt32BE(packet, 12) != 0;
        }
        else if (action == 1)
        {
            if (packet.Length >= 8 && ReadUInt32BE(packet, 4) == _world.TradeContainer)
                _world.TradeContainer = 0;
            BotEngine.CountTradeClosed();
        }
    }

    /// <summary>Split already-decompressed server bytes into packets the way the
    /// receive loop does and handle each; returns how many came out. For tests that
    /// check the framing against packets the server really builds.</summary>
    internal int ProcessDecompressed(ReadOnlySpan<byte> bytes, uint charUid)
    {
        _charUid = charUid;
        _world.CharUid = charUid;
        foreach (byte b in bytes)
            _decompressedBuffer.Add(b);
        int count = 0;
        while (ExtractPacketFromBuffer() is { } packet)
        {
            ProcessIncomingPacket(packet);
            count++;
        }
        return count;
    }

    // Clients before 6.0.4.0 open with a bare 4-byte seed and say their version only
    // in the 0xBD answer; later ones send it in 0xEF.
    private byte[] VersionSeed(uint seed) => ClientVersion < ReferenceClientProtocol.Pack(6, 0, 4, 0)
        ? [(byte)(seed >> 24), (byte)(seed >> 16), (byte)(seed >> 8), (byte)seed]
        : BotPacketBuilder.BuildLoginSeed(seed,
            ClientVersion >> 24, (ClientVersion >> 16) & 0xFF, (ClientVersion >> 8) & 0xFF, ClientVersion & 0xFF);

    private string VersionText =>
        $"{ClientVersion >> 24}.{(ClientVersion >> 16) & 0xFF}.{(ClientVersion >> 8) & 0xFF}.{ClientVersion & 0xFF}";

    /// <summary>Send bytes as they are, for drills that build their own packets.</summary>
    public void SendRaw(byte[] packet) => SendRawPacket(packet);

    internal void SendRawPacket(byte[] packet)
    {
        if (_stream == null) return;
        if (!_tcp!.Connected)
        {
            // The server closed the connection: nothing sent from here on arrives.
            State = BotState.Disconnected;
            return;
        }
        _sendGate.Wait();
        try
        {
            _stream.Write(packet);
            PacketsSent++;
            BytesSent += packet.Length;
        }
        catch { State = BotState.Disconnected; }
        finally { _sendGate.Release(); }
    }

    /// <summary>Send one move request and return the sequence it went out with, so
    /// the caller can tell this move's answer from an earlier one's.</summary>
    internal byte SendMovePacket(byte dir)
    {
        byte seq = NextMoveSequence(dir);
        SendRawPacket(BotPacketBuilder.BuildMoveRequest(dir, seq));
        return seq;
    }

    /// <summary>Take the next walk sequence for a step in <paramref name="dir"/>. After
    /// 255 the sequence goes on at 1: 0 only ever opens a fresh run after a reset, and
    /// a 0 in mid-run is refused as out of order.</summary>
    private byte NextMoveSequence(byte dir)
    {
        byte seq = _moveSequence;
        _stepDirBySeq[seq] = dir;
        _moveSequence = seq == 255 ? (byte)1 : (byte)(seq + 1);
        return seq;
    }

    /// <summary>The server accepted the move with this sequence. 0x22 says nothing of
    /// where the character now stands - the client works it out the way the server
    /// did: a request facing another way is a turn in place, one along the current
    /// facing is a step. Without this the bot's position stayed where it logged in,
    /// so every path it walked was planned from there, and each reject's correction
    /// read as a jump across the map.</summary>
    private void ApplyAcceptedStep(byte seq)
    {
        byte dir = (byte)(_stepDirBySeq[seq] & 0x07);
        if (dir != (_world.Direction & 0x07))
        {
            _world.Direction = dir;
            return;
        }
        var (dx, dy) = BotWorldModel.StepDelta(dir);
        _world.X = _x = (short)(_x + dx);
        _world.Y = _y = (short)(_y + dy);
    }

    private async Task SendPacketAsync(byte[] packet, CancellationToken ct)
    {
        if (_stream == null || !_tcp!.Connected) return;

        try
        {
            await _sendGate.WaitAsync(ct);
        }
        catch (OperationCanceledException) { return; }
        try
        {
            await _stream.WriteAsync(packet, ct);
            PacketsSent++;
            BytesSent += packet.Length;
        }
        catch { State = BotState.Disconnected; }
        finally { _sendGate.Release(); }
    }

    private async Task<byte[]?> ReadPacketAsync(CancellationToken ct, int timeout = 5000)
    {
        if (_stream == null) 
        {
            _logger.LogDebug("[Bot{Id}] ReadPacket: stream is null", _botId);
            return null;
        }
        if (_tcp == null || !_tcp.Connected)
        {
            _logger.LogDebug("[Bot{Id}] ReadPacket: tcp is null or not connected", _botId);
            return null;
        }
        
        try
        {
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            var header = new byte[3];
            int read = await _stream.ReadAsync(header.AsMemory(0, 1), linkedCts.Token);
            if (read == 0) 
            {
                _logger.LogDebug("[Bot{Id}] ReadPacket: read returned 0 bytes (connection closed)", _botId);
                return null;
            }
            
            byte opcode = header[0];
            int length = GetPacketLength(opcode);
            bool isVariable = (length == -1);
            
            if (isVariable)
            {
                read = await _stream.ReadAsync(header.AsMemory(1, 2), linkedCts.Token);
                if (read < 2) return null;
                length = (header[1] << 8) | header[2];
            }
            
            if (length <= 0 || length > 65535) return null;
            
            var packet = new byte[length];
            packet[0] = opcode;
            
            // For variable packets, we already read opcode + 2 length bytes = 3 bytes
            // For fixed packets, we only read opcode = 1 byte
            int alreadyRead = isVariable ? 3 : 1;
            if (isVariable)
            {
                packet[1] = header[1];
                packet[2] = header[2];
            }
            
            int remaining = length - alreadyRead;
            int offset = alreadyRead;
            while (remaining > 0)
            {
                read = await _stream.ReadAsync(packet.AsMemory(offset, remaining), linkedCts.Token);
                if (read == 0) return null;
                offset += read;
                remaining -= read;
            }
            
            BytesReceived += length;
            return packet;
        }
        catch (OperationCanceledException)
        {
            // Timeout - this is expected
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("[Bot{Id}] ReadPacket exception: {Error}", _botId, ex.Message);
            return null;
        }
    }

    /// <summary>Server-to-client packet lengths for the client version the bot
    /// announces (<see cref="ClientVersion"/>); -1 = variable, a 2-byte length follows
    /// the opcode. The server picks some sizes by client version (0x24 is 7 bytes
    /// before 7.0.9, 9 after), so the table has to be the one for that version, and it
    /// has to cover every opcode: an unknown fixed-size packet read as variable took
    /// two payload bytes for a length and the stream never lined up again.</summary>
    private int GetPacketLength(byte opcode) => ReferenceClientProtocol.Length(opcode, ClientVersion);

    private byte _lastOpcode;

    private void NoteFramingError(byte opcode)
    {
        _world.FramingErrors++;
        int n = Math.Min(12, _decompressedBuffer.Count);
        var head = new System.Text.StringBuilder(n * 3);
        for (int i = 0; i < n; i++)
            head.Append(_decompressedBuffer[i].ToString("X2")).Append(' ');
        BotEngine.CountFramingError(
            $"0x{opcode:X2} after 0x{_lastOpcode:X2}: {head.ToString().TrimEnd()}");
    }

    private static uint ReadUInt32BE(byte[] buf, int offset)
    {
        return ((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16) |
               ((uint)buf[offset + 2] << 8) | buf[offset + 3];
    }

    private static ushort ReadUInt16BE(byte[] buf, int offset)
    {
        return (ushort)((buf[offset] << 8) | buf[offset + 1]);
    }

    /// <summary>
    /// Read a packet from the game server (Huffman compressed).
    /// Each packet is compressed separately with its own EOF marker.
    /// </summary>
    private async Task<byte[]?> ReadGamePacketAsync(CancellationToken ct, int timeout = 5000)
    {
        if (_stream == null || _tcp == null || !_tcp.Connected) return null;

        var timeoutTime = DateTime.UtcNow.AddMilliseconds(timeout);
        // The compressed buffer ends partway through a packet: wait for the rest
        // rather than decode it again.
        bool partial = false;

        while (DateTime.UtcNow < timeoutTime)
        {
            // Try to extract a packet from the decompressed buffer
            var packet = ExtractPacketFromBuffer();
            if (packet != null)
            {
                BytesReceived += packet.Length;
                // The client answers the server's version request with its version text.
                if (packet[0] == 0xBD)
                    SendRawPacket(BotPacketBuilder.BuildClientVersion(VersionText));
                return packet;
            }

            // Try to decompress more from the compressed buffer. Each packet carries
            // its own terminator; only a packet decoded up to it is taken. TCP hands
            // the bytes over in arbitrary pieces, and decoding a piece that stops
            // mid-packet threw away the bits of the code cut at its end - the rest of
            // the packet then decoded from the wrong bit and the stream lost its
            // packet boundaries (0x00/0x05 "packets", framing errors).
            if (_compressedBuffer.Count > 0 && !partial)
            {
                var compressedArray = _compressedBuffer.ToArray();
                var decompressed = HuffmanCompression.DecompressFromServer(
                    compressedArray, 0, compressedArray.Length, out int bytesConsumed, out bool complete);

                if (complete)
                {
                    if (decompressed.Length > 0)
                        ServerPacketObserver?.Invoke(decompressed);
                    _decompressedBuffer.AddRange(decompressed);
                    if (bytesConsumed > 0 && bytesConsumed <= _compressedBuffer.Count)
                        _compressedBuffer.RemoveRange(0, bytesConsumed);
                    continue; // Try to extract packet again
                }
                partial = true;
            }

            // Read more compressed data from network
            if (partial || _stream.DataAvailable || (_compressedBuffer.Count == 0 && _decompressedBuffer.Count == 0))
            {
                var rawBuffer = new byte[4096];
                using var readCts = new CancellationTokenSource(500);
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, readCts.Token);

                try
                {
                    int read = await _stream.ReadAsync(rawBuffer.AsMemory(), linked.Token);
                    if (read == 0) 
                    {
                        // Connection closed
                        return null;
                    }
                    
                    // Add to compressed buffer for decompression
                    for (int i = 0; i < read; i++)
                        _compressedBuffer.Add(rawBuffer[i]);
                    partial = false;
                }
                catch (OperationCanceledException)
                {
                    // Short read timeout, try to decompress what we have
                }
            }
            else
            {
                // No data available and nothing to process, wait a bit
                await Task.Delay(10, ct);
            }
        }

        return null;
    }

    /// <summary>
    /// Extract a complete packet from the decompressed buffer, or null if incomplete.
    /// </summary>
    private byte[]? ExtractPacketFromBuffer()
    {
        byte opcode;
        int length;
        while (true)
        {
            if (_decompressedBuffer.Count == 0) return null;

            opcode = _decompressedBuffer[0];
            length = GetPacketLength(opcode);
            bool isVariable = (length == -1);

            if (isVariable)
            {
                if (_decompressedBuffer.Count < 3) return null; // Need at least opcode + 2 length bytes
                length = (_decompressedBuffer[1] << 8) | _decompressedBuffer[2];
            }

            // A variable packet is at least its opcode and the length itself. Anything
            // shorter means the stream is no longer on a packet boundary: count it, so a
            // misaligned stream shows in the report instead of only in odd bot
            // behaviour, and look for the next boundary.
            if (length <= 0 || (isVariable && length < 3))
            {
                NoteFramingError(opcode);
                _decompressedBuffer.RemoveAt(0);
                continue;
            }
            break;
        }

        if (_decompressedBuffer.Count < length) return null; // Incomplete packet

        // Extract the packet
        var packet = _decompressedBuffer.GetRange(0, length).ToArray();
        _decompressedBuffer.RemoveRange(0, length);
        _lastOpcode = opcode;
        BotEngine.CountReceivedOpcode(opcode);
        return packet;
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _actionApi?.CompleteDisconnect();
        try { _behaviorTask?.Wait(1000); } catch { }
        try { _receiveTask?.Wait(1000); } catch { }
        _stream?.Close();
        _tcp?.Close();
        State = BotState.Disconnected;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Disconnect();
        _cts?.Dispose();
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}

public enum BotState
{
    Disconnected,
    Connecting,
    LoggingIn,
    Playing
}

public enum BotBehavior
{
    Idle,
    RandomWalk,
    Combat,
    FullSimulation,
    SmartAI,
    Walker,
    CombatRole,
    Vendor,
    Loot,
    Skill,
    Social,
    Chaos,
    Cluster,
    Mage,
}
