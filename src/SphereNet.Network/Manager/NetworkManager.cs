using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Network.Encryption;
using SphereNet.Network.Packets;
using SphereNet.Network.Packets.Incoming;
using SphereNet.Network.State;

namespace SphereNet.Network.Manager;

/// <summary>
/// Central network manager. Maps to CNetworkManager in Source-X.
/// Manages listen socket, connection pool, accept, input/output processing.
/// </summary>
public sealed class NetworkManager : IDisposable
{
    private const int MaxPacketSize = 65535;
    private const int PartialPacketTimeoutMs = 15_000;
    private Socket? _listenSocket;
    private readonly NetState[] _states;
    private readonly PacketManager _packetManager;
    private readonly ILogger<NetworkManager> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private int _maxClients;
    private bool _isRunning;
    private ClientEra _defaultClientEra = ClientEra.Sphere56x;

    public PacketManager Packets => _packetManager;
    public int ActiveConnections => _states.Count(s => s.IsInUse);

    public CryptConfig? CryptConfig { get; set; }
    public bool UseCrypt { get; set; } = true;
    public bool UseNoCrypt { get; set; }
    public ClientEra DefaultClientEra
    {
        get => _defaultClientEra;
        set
        {
            _defaultClientEra = value;
            foreach (var state in _states)
                state.ClientEra = value;
        }
    }
    public bool DebugPackets { get; set; }
    public HashSet<byte>? DebugPacketOpcodeFilter { get; set; }
    public int MaxPacketsPerTick { get; set; } = 100;
    public int FloodDetectionCount { get; set; } = 5;
    public int FloodDetectionWindowMs { get; set; } = 10_000;
    public int ClientMaxIP { get; set; } = 16;
    public Func<NetState, byte, byte[], bool>? PacketScriptHook { get; set; }
    public Func<System.Net.IPAddress, bool>? ConnectionAcceptFilter { get; set; }

    /// <summary>Fired when a connection is about to be cleaned up (before Clear).</summary>
    public event Action<int>? OnConnectionClosed;
    public event Action<NetState>? OnConnectionAccepted;
    public event Action<NetState, byte, byte[]>? OnUnknownPacket;
    public event Action<NetState, int>? OnPacketQuotaExceeded;
    public event Action<NetState>? OnConnectionClosedState;

    public NetworkManager(int maxClients, ILoggerFactory loggerFactory)
    {
        _maxClients = maxClients;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<NetworkManager>();
        _packetManager = new PacketManager();
        _states = new NetState[maxClients];

        for (int i = 0; i < maxClients; i++)
        {
            _states[i] = new NetState(loggerFactory.CreateLogger<NetState>())
            {
                Id = i,
                ClientEra = _defaultClientEra
            };
        }

        RegisterStandardPackets();
    }

    private void RegisterStandardPackets()
    {
        _packetManager.Register(new PacketLoginRequest());
        _packetManager.Register(new PacketGameLogin());
        _packetManager.Register(new PacketCharSelect());
        _packetManager.Register(new PacketCreateCharacter());
        _packetManager.Register(new PacketCreateCharacterHS());
        _packetManager.Register(new PacketPing());
        _packetManager.Register(new PacketMoveRequest());
        _packetManager.Register(new PacketNewMovementRequest());
        _packetManager.Register(new PacketSpeechRequest());
        _packetManager.Register(new PacketAttackRequest());
        _packetManager.Register(new PacketWarMode());
        _packetManager.Register(new PacketTextCommand());
        _packetManager.Register(new PacketSkillLock());
        _packetManager.Register(new PacketResyncRequest());
        _packetManager.Register(new PacketLogoutRequest());
        _packetManager.Register(new PacketHelpRequest());
        _packetManager.Register(new PacketServerSelect());
        _packetManager.Register(new PacketDoubleClick());
        _packetManager.Register(new PacketSingleClick());
        _packetManager.Register(new PacketItemPickup());
        _packetManager.Register(new PacketItemDrop());
        _packetManager.Register(new PacketItemEquip());
        _packetManager.Register(new PacketStatusRequest());
        _packetManager.Register(new PacketTargetResponse());
        _packetManager.Register(new PacketSpeechUnicode());
        _packetManager.Register(new PacketGumpResponse());
        _packetManager.Register(new PacketClientVersion());
        _packetManager.Register(new PacketExtendedCommand(_packetManager));
        _packetManager.Register(new PacketEncodedCommand());
        _packetManager.Register(new PacketAOSTooltipReq());
        _packetManager.Register(new PacketVendorBuy());
        _packetManager.Register(new PacketVendorSell());
        _packetManager.Register(new PacketSecureTrade());
        _packetManager.Register(new PacketRename());
        _packetManager.Register(new PacketProfileRequest());
        _packetManager.Register(new PacketViewRange());

        // Phase 1: Critical Stability
        _packetManager.Register(new PacketDeathMenu());
        _packetManager.Register(new PacketCharDelete());
        _packetManager.Register(new PacketDyeResponse());
        _packetManager.Register(new PacketPromptResponse());
        _packetManager.Register(new PacketMenuChoice());

        // Phase 2: Content Features
        _packetManager.Register(new PacketBookPage());
        _packetManager.Register(new PacketBookHeader());
        _packetManager.Register(new PacketBulletinBoard());
        _packetManager.Register(new PacketMapDetail());
        _packetManager.Register(new PacketMapPinEdit());

        // Phase 3: Client Compatibility
        _packetManager.Register(new PacketHardwareInfo());
        _packetManager.Register(new PacketSystemInfo());
        _packetManager.Register(new PacketAssistVersion());
        _packetManager.Register(new PacketGumpTextEntry());
        _packetManager.Register(new PacketAllNamesReq());
        _packetManager.Register(new PacketChatText());
        _packetManager.Register(new PacketClientType());
        _packetManager.Register(new PacketKREncryption());

        // Faz 2: Packet Audit & Hardening
        _packetManager.Register(new PacketNewBookHeader());
        _packetManager.Register(new PacketCrashReport());
        _packetManager.Register(new PacketDisconnect());
    }

    /// <summary>Initialize the listen socket.</summary>
    public bool Start(string ip, int port)
    {
        try
        {
            var addr = ip == "0.0.0.0" ? IPAddress.Any : IPAddress.Parse(ip);
            _listenSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            _listenSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listenSocket.Bind(new IPEndPoint(addr, port));
            _listenSocket.Listen(128);
            _listenSocket.Blocking = false;
            _isRunning = true;
            _logger.LogInformation("Listening on {IP}:{Port}", ip, port);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start listener on {IP}:{Port}", ip, port);
            return false;
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _listenSocket?.Close();
        _listenSocket = null;

        foreach (var state in _states)
        {
            if (state.IsInUse)
                state.Clear();
        }
    }

    /// <summary>
    /// Accept new connections. Called from main tick.
    /// </summary>
    public void CheckNewConnections()
    {
        if (_listenSocket == null || !_isRunning) return;

        try
        {
            while (_listenSocket.Poll(0, SelectMode.SelectRead))
            {
                Socket clientSocket = _listenSocket.Accept();

                if (ConnectionAcceptFilter != null &&
                    clientSocket.RemoteEndPoint is IPEndPoint filterEp &&
                    ConnectionAcceptFilter(filterEp.Address))
                {
                    _logger.LogWarning("Connection from {IP} rejected by accept filter", filterEp.Address);
                    clientSocket.Close();
                    continue;
                }

                if (ClientMaxIP > 0 && clientSocket.RemoteEndPoint is System.Net.IPEndPoint ep)
                {
                    int ipCount = 0;
                    foreach (var s in _states)
                    {
                        if (s.IsInUse && s.RemoteEndPoint is System.Net.IPEndPoint sEp &&
                            sEp.Address.Equals(ep.Address))
                            ipCount++;
                    }
                    if (ipCount >= ClientMaxIP)
                    {
                        _logger.LogWarning("IP limit ({Limit}) reached for {IP}, rejecting", ClientMaxIP, ep.Address);
                        clientSocket.Close();
                        continue;
                    }
                }

                var slot = FindFreeSlot();
                if (slot == null)
                {
                    _logger.LogWarning("No free slots, rejecting connection from {EP}", clientSocket.RemoteEndPoint);
                    clientSocket.Close();
                    continue;
                }

                slot.Init(clientSocket);
                slot.DebugPackets = DebugPackets;
                _logger.LogInformation("Connection #{Id} from {EP}", slot.Id, slot.RemoteEndPoint);
                OnConnectionAccepted?.Invoke(slot);
            }
        }
        catch (SocketException ex)
        {
            _logger.LogDebug(ex, "Accept poll interrupted");
        }
    }

    /// <summary>
    /// Process all incoming data. Called from main loop BEFORE World.OnTick/RunServerTick
    /// to ensure packets are handled promptly for low latency.
    /// </summary>
    public void ProcessAllInput()
    {
        foreach (var state in _states)
        {
            if (!state.CanReceive) continue;

            int read = state.Receive();
            if (read < 0)
            {
                _logger.LogInformation("Connection #{Id} lost", state.Id);
                state.MarkClosing();
                continue;
            }

            if (read == 0) continue;

            ProcessInput(state);
        }
    }

    private void ProcessInput(NetState state)
    {
        var data = state.ReceivedData;
        if (data.Length == 0) return;

        // First bytes of a new connection is the seed.
        // Classic clients send 4 raw bytes. 7.0+ clients send packet 0xEF (21 bytes).
        if (!state.IsSeeded)
        {
            if (data[0] == 0xEF && data.Length >= 21)
            {
                state.Seed = (uint)((data[1] << 24) | (data[2] << 16) | (data[3] << 8) | data[4]);
                uint major = (uint)((data[5] << 24) | (data[6] << 16) | (data[7] << 8) | data[8]);
                uint minor = (uint)((data[9] << 24) | (data[10] << 16) | (data[11] << 8) | data[12]);
                uint rev = (uint)((data[13] << 24) | (data[14] << 16) | (data[15] << 8) | data[16]);
                uint patch = (uint)((data[17] << 24) | (data[18] << 16) | (data[19] << 8) | data[20]);
                state.ClientVersionNumber = major * 10_000_000 + minor * 1_000_000 + rev * 1_000 + patch;
                state.IsSeeded = true;
                _logger.LogTrace("Seed 0xEF #{Id}: seed=0x{Seed:X8}, ver={Major}.{Minor}.{Rev}.{Patch}",
                    state.Id, state.Seed, major, minor, rev, patch);
                state.ConsumeReceived(21);
            }
            else if (data.Length >= 4 && data[0] != 0xEF)
            {
                state.Seed = (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
                state.IsSeeded = true;
                _logger.LogTrace("Seed classic #{Id}: seed=0x{Seed:X8}", state.Id, state.Seed);
                state.ConsumeReceived(4);
            }
            else
            {
                return;
            }

            data = state.ReceivedData;
            if (data.Length == 0) return;
        }

        // Encryption auto-detection on first real packet after seed
        if (!state.Crypto.IsInitialized && data.Length > 0)
        {
            if (!TryInitCrypto(state, data))
                return;
            data = state.ReceivedData;
            if (data.Length == 0) return;
        }

        // Decrypt any new data with the established cipher
        if (state.Crypto.IsInitialized && state.Crypto.EncType != EncryptionType.None)
        {
            int undecrypted = state.UndecryptedOffset;
            if (undecrypted < data.Length)
            {
                int decLen = data.Length - undecrypted;
                var toDecrypt = ArrayPool<byte>.Shared.Rent(decLen);
                try
                {
                    data[undecrypted..].CopyTo(toDecrypt);
                    state.Crypto.Decrypt(toDecrypt, 0, decLen);
                    state.ReplaceReceivedRange(undecrypted, toDecrypt, decLen);
                    state.UndecryptedOffset = data.Length;
                    data = state.ReceivedData;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(toDecrypt);
                }
            }
        }

        int consumed = 0;
        int packetsProcessed = 0;
        while (consumed < data.Length)
        {
            if (MaxPacketsPerTick > 0 && packetsProcessed >= MaxPacketsPerTick)
            {
                long now = Environment.TickCount64;
                if (now - state.PacketFloodWindowStart > FloodDetectionWindowMs)
                {
                    state.PacketFloodCount = 0;
                    state.PacketFloodWindowStart = now;
                }
                state.PacketFloodCount++;
                if (state.PacketFloodCount >= FloodDetectionCount)
                {
                    _logger.LogWarning("Packet flood detected for #{Id}, dropping connection", state.Id);
                    state.MarkClosing();
                    break;
                }
                OnPacketQuotaExceeded?.Invoke(state, packetsProcessed);
                break;
            }

            byte opcode = data[consumed];
            int definedLen = PacketDefinitions.GetPacketLength(opcode, state);
            bool hasLengthField = definedLen == 0;
            int packetLen = definedLen;

            if (hasLengthField)
            {
                if (data.Length - consumed < 3) break;
                packetLen = (data[consumed + 1] << 8) | data[consumed + 2];
            }

            if (packetLen <= 0 || packetLen > MaxPacketSize)
            {
                _logger.LogWarning("Invalid packet length {Len} from #{Id}, dropping connection", packetLen, state.Id);
                state.MarkClosing();
                break;
            }
                if (data.Length - consumed < packetLen)
                {
                    MarkOrDropPartialPacket(state, opcode, packetLen);
                    break;
                }

                state.ClearPendingPacket();

            var handler = _packetManager.GetHandler(opcode);
            if (handler != null)
            {
                if (PacketScriptHook != null || ShouldLogPacketDebug(opcode))
                {
                    var rawPacket = data.Slice(consumed, packetLen).ToArray();
                    if (InvokePacketScriptHook(state, opcode, rawPacket))
                    {
                        packetsProcessed++;
                        consumed += packetLen;
                        continue;
                    }
                    if (ShouldLogPacketDebug(opcode))
                    {
                        _logger.LogDebug("RECV #{Id} 0x{Op:X2} ({Name}) len={Len} data=[{Data}]",
                            state.Id, opcode, handler.GetType().Name,
                            packetLen, FormatHex(rawPacket, 32));
                    }
                }

                int payloadOffset = hasLengthField ? 3 : 1;
                int payloadLength = packetLen - payloadOffset;
                if (payloadLength < 0)
                {
                    _logger.LogWarning("Invalid packet length #{Id} 0x{Op:X2}: total={Total} payloadOffset={Offset}",
                        state.Id, opcode, packetLen, payloadOffset);
                    state.MarkClosing();
                    break;
                }

                var buffer = new PacketBuffer(data.Slice(consumed + payloadOffset, payloadLength).ToArray());
                handler.OnReceive(buffer, state);
                if (buffer.IsUnderrun)
                {
                    _logger.LogWarning("Malformed packet underrun #{Id} 0x{Op:X2}: payload={PayloadLen}",
                        state.Id, opcode, payloadLength);
                    state.MarkClosing();
                    break;
                }
            }
            else
            {
                var rawBytes = data.Slice(consumed, packetLen).ToArray();
                OnUnknownPacket?.Invoke(state, opcode, rawBytes);
                if (ShouldLogPacketDebug(opcode))
                {
                    _logger.LogDebug("RECV #{Id} 0x{Op:X2} (UNHANDLED) len={Len} data=[{Data}]",
                        state.Id, opcode, packetLen, FormatHex(rawBytes.AsSpan(0, Math.Min(rawBytes.Length, 32)), 32));
                }
                else
                {
                    _logger.LogTrace("Unhandled packet 0x{Op:X2} from #{Id}", opcode, state.Id);
                }
            }

            consumed += packetLen;
            packetsProcessed++;
        }

        if (consumed > 0)
            state.ConsumeReceived(consumed);
    }

    private void MarkOrDropPartialPacket(NetState state, byte opcode, int packetLen)
    {
        long now = Environment.TickCount64;
        state.MarkPendingPacket(opcode, packetLen, now);
        if (state.PendingPacketStartTick > 0 && now - state.PendingPacketStartTick > PartialPacketTimeoutMs)
        {
            _logger.LogWarning(
                "Partial packet timeout for #{Id}: opcode=0x{Op:X2}, expected={Len}, buffered={Buffered}",
                state.Id, opcode, packetLen, state.ReceivedData.Length);
            state.MarkClosing();
        }
    }

    /// <summary>
    /// Attempt to detect encryption and decrypt the first packet.
    /// Returns true if data was processed (or should be retried), false to wait for more data.
    /// </summary>
    private bool TryInitCrypto(NetState state, ReadOnlySpan<byte> data)
    {
        var config = CryptConfig ?? new CryptConfig();
        bool allowNoCrypt = UseNoCrypt || !UseCrypt || config.Keys.Count == 0;

        if (data.Length < 62)
        {
            MarkOrDropPartialPacket(state, 0x80, 62);
            return false;
        }

        // Universal compatibility mode: some clients coalesce seed+login in
        // one TCP read, some are no-crypt, and legacy clients require key
        // probing. Try plausible packet boundaries instead of assuming that
        // every 65+ byte first packet is a game-login.
        string attempts = "";
        string diagnostics = "";
        foreach (int offset in GetCryptoCandidateOffsets(data))
        {
            var slice = data[offset..];
            foreach (uint seedCandidate in GetCryptoSeedCandidates(state.Seed))
            {
                if (slice.Length >= 65)
                {
                    attempts += $" game@{offset}/0x{seedCandidate:X8}";
                    _logger.LogDebug("Game login detection for #{Id}: offset={Offset}, seed=0x{Seed:X8}, bytes=[{Data}], len={Len}",
                        state.Id, offset, seedCandidate, FormatHex(slice[..Math.Min(slice.Length, 16)], 16), slice.Length);

                    var result = state.Crypto.DetectAndDecryptGameLogin(
                        seedCandidate, slice[..65], config, UseCrypt, allowNoCrypt);
                    if (result != null)
                    {
                        state.Seed = seedCandidate;
                        ReplaceCryptoCandidateData(state, data, offset, 65, result);
                        state.ConnectionType = ConnectType.Game;
                        if (state.ClientVersionNumber == 0 && state.Crypto.RelayClientVersion > 0)
                            state.ClientVersionNumber = state.Crypto.RelayClientVersion;
                        _logger.LogDebug("Game login encryption detected for #{Id}: {Enc}, offset={Offset}, seed=0x{Seed:X8}",
                            state.Id, state.Crypto.EncType, offset, seedCandidate);
                        return true;
                    }
                    if (!string.IsNullOrEmpty(state.Crypto.LastDetectionDiagnostic))
                        diagnostics = state.Crypto.LastDetectionDiagnostic;
                    state.Crypto.Reset();
                }

                if (slice.Length >= 62)
                {
                    attempts += $" login@{offset}/0x{seedCandidate:X8}";
                    var result = state.Crypto.DetectAndDecryptLogin(
                        seedCandidate, slice[..62], config, UseCrypt, allowNoCrypt);
                    if (result != null)
                    {
                        state.Seed = seedCandidate;
                        ReplaceCryptoCandidateData(state, data, offset, 62, result);
                        state.ConnectionType = ConnectType.Login;
                        _logger.LogInformation("Login encryption detected for #{Id}: {Enc}, offset={Offset}, seed=0x{Seed:X8}",
                            state.Id, state.Crypto.EncType, offset, seedCandidate);
                        return true;
                    }
                    if (!string.IsNullOrEmpty(state.Crypto.LastDetectionDiagnostic))
                        diagnostics = state.Crypto.LastDetectionDiagnostic;
                    state.Crypto.Reset();
                }
            }
        }

        _logger.LogWarning(
            "Failed to detect encryption for #{Id}: seed=0x{Seed:X8}, first=0x{B:X2}, len={Len}, useCrypt={UseCrypt}, useNoCrypt={UseNoCrypt}, keys={Keys}, attempts='{Attempts}', diag=\"{Diag}\", raw=[{Raw}]",
            state.Id, state.Seed, data[0], data.Length, UseCrypt, allowNoCrypt, config.Keys.Count,
            attempts.Trim(), diagnostics, FormatHex(data[..Math.Min(data.Length, 96)], 96));
        state.MarkClosing();
        return false;
    }

    private static int[] GetCryptoCandidateOffsets(ReadOnlySpan<byte> data)
    {
        Span<int> offsets = stackalloc int[3];
        int count = 0;
        offsets[count++] = 0;

        if (data.Length >= 66 && (data[4] == 0x80 || data[4] == 0x91))
            offsets[count++] = 4;

        if (data.Length >= 83 && data[0] == 0xEF && (data[21] == 0x80 || data[21] == 0x91))
            offsets[count++] = 21;

        return offsets[..count].ToArray();
    }

    private static uint[] GetCryptoSeedCandidates(uint seed)
    {
        uint swapped = ((seed & 0x000000FF) << 24) |
                       ((seed & 0x0000FF00) << 8) |
                       ((seed & 0x00FF0000) >> 8) |
                       ((seed & 0xFF000000) >> 24);
        return swapped == seed ? [seed] : [seed, swapped];
    }

    private static void ReplaceCryptoCandidateData(NetState state, ReadOnlySpan<byte> original, int offset, int packetLength, byte[] decoded)
    {
        byte[] newData = new byte[decoded.Length + (original.Length - offset - packetLength)];
        decoded.CopyTo(newData, 0);
        if (original.Length > offset + packetLength)
            original[(offset + packetLength)..].CopyTo(newData.AsSpan(decoded.Length));
        ReplaceReceivedData(state, newData);
    }

    private static void ReplaceReceivedData(NetState state, byte[] newData)
    {
        state.ConsumeReceived(state.ReceivedData.Length);
        state.InjectReceived(newData);
        state.UndecryptedOffset = newData.Length;
    }

    /// <summary>
    /// Flush all outgoing data. Called from main tick.
    /// </summary>
    private const int FlushParallelThreshold = 128;

    public void ProcessAllOutput()
    {
        // Each connection's flush touches only its own state (send queue, crypto,
        // batch buffer, socket). The only cross-connection data is shared
        // broadcast packets, whose compressed payload is precomputed in
        // MarkShared (read-only here) and whose pool return is an interlocked
        // refcount — so flushes are independent and parallelize cleanly. Below a
        // threshold the parallel overhead isn't worth it.
        int active = 0;
        for (int i = 0; i < _states.Length; i++)
            if (_states[i].IsInUse) active++;

        if (active < FlushParallelThreshold)
        {
            foreach (var state in _states)
            {
                if (state.IsInUse)
                    state.FlushOutput();
            }
            return;
        }

        Parallel.ForEach(
            _states,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            state =>
            {
                if (state.IsInUse)
                    state.FlushOutput();
            });
    }

    /// <summary>Idle timeout: drop connections with no activity for this duration.</summary>
    private const long IdleTimeoutMs = 120_000; // 2 dakika
    private const long UnauthIdleTimeoutMs = 15_000; // 15 saniye — seed/login olmayan bağlantılar

    /// <summary>
    /// Cleanup closed connections and drop idle ones. Called from main tick.
    /// </summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        foreach (var state in _states)
        {
            if (!state.IsInUse) continue;

            // Idle timeout — unauthenticated bağlantılar 15s, normal 2 dakika
            long timeout = state.IsSeeded ? IdleTimeoutMs : UnauthIdleTimeoutMs;
            if (!state.IsClosing && state.LastActivityTick > 0 &&
                now - state.LastActivityTick > timeout)
            {
                _logger.LogInformation("Connection #{Id} idle timeout ({Ms}ms)", state.Id,
                    now - state.LastActivityTick);
                state.MarkClosing();
            }

            if (state.IsClosing)
            {
                _logger.LogInformation("Closing connection #{Id}", state.Id);
                OnConnectionClosedState?.Invoke(state);
                OnConnectionClosed?.Invoke(state.Id);
                state.Clear();
            }
        }
    }

    /// <summary>Get all active connections.</summary>
    public IEnumerable<NetState> GetActiveStates()
    {
        foreach (var state in _states)
        {
            if (state.IsInUse && !state.IsClosing)
                yield return state;
        }
    }

    public NetState? GetState(int id)
    {
        if (id < 0 || id >= _states.Length)
            return null;
        return _states[id];
    }

    public bool InvokePacketScriptHook(NetState state, byte opcode, byte[] packet) =>
        PacketScriptHook?.Invoke(state, opcode, packet) == true;

    /// <summary>Register event handlers on all states (for connecting game logic).</summary>
    public void SetHandlers(
        Action<NetState, string, string>? loginRequest = null,
        Action<NetState, string, string, uint>? gameLogin = null,
        Action<NetState, int, string>? charSelect = null,
        Action<NetState, byte, byte, uint>? moveRequest = null,
        Action<NetState, IReadOnlyList<MovementStep>>? movementBatch = null,
        Action<NetState, byte, ushort, ushort, string>? speech = null,
        Action<NetState, uint>? attackRequest = null,
        Action<NetState, bool>? warMode = null,
        Action<NetState, uint>? doubleClick = null,
        Action<NetState, uint>? singleClick = null,
        Action<NetState, uint, ushort>? itemPickup = null,
        Action<NetState, uint, short, short, sbyte, uint>? itemDrop = null,
        Action<NetState, uint, byte, uint>? itemEquip = null,
        Action<NetState, byte, uint>? statusRequest = null,
        Action<NetState, byte, uint, uint, short, short, sbyte, ushort>? targetResponse = null,
        Action<NetState, uint, uint, uint, uint[], (ushort Id, string Text)[]>? gumpResponse = null,
        Action<NetState, string>? clientVersion = null,
        Action<NetState, byte>? viewRange = null,
        Action<NetState, uint>? aosTooltip = null,
        Action<NetState, byte, string>? textCommand = null,
        Action<NetState, ushort, PacketBuffer>? extendedCommand = null,
        Action<NetState>? resyncRequest = null,
        Action<NetState>? logoutRequest = null,
        Action<NetState>? helpRequest = null,
        Action<NetState, ushort>? serverSelect = null,
        Action<NetState, Core.Types.CharCreateInfo>? charCreate = null,
        Action<NetState, uint, byte, List<Packets.Incoming.VendorBuyEntry>>? vendorBuy = null,
        Action<NetState, uint, List<Packets.Incoming.VendorSellEntry>>? vendorSell = null,
        Action<NetState, byte, uint, uint>? secureTrade = null,
        Action<NetState, uint, string>? rename = null,
        Action<NetState, byte, uint, string>? profileRequest = null,
        // Phase 1
        Action<NetState, byte>? deathMenu = null,
        Action<NetState, int, string>? charDelete = null,
        Action<NetState, uint, ushort>? dyeResponse = null,
        Action<NetState, uint, uint, uint, string>? promptResponse = null,
        Action<NetState, uint, ushort, ushort, ushort>? menuChoice = null,
        // Phase 2
        Action<NetState, uint, List<(ushort PageNum, string[] Lines)>>? bookPage = null,
        Action<NetState, uint, bool, string, string>? bookHeader = null,
        Action<NetState, uint>? bulletinBoardRequestList = null,
        Action<NetState, uint, uint>? bulletinBoardRequestMessage = null,
        Action<NetState, uint, uint, string, string[]>? bulletinBoardPost = null,
        Action<NetState, uint, uint>? bulletinBoardDelete = null,
        Action<NetState, uint>? mapDetail = null,
        Action<NetState, uint, byte, byte, ushort, ushort>? mapPinEdit = null,
        // Phase 3
        Action<NetState, uint, ushort, byte, string>? gumpTextEntry = null,
        Action<NetState, uint>? allNamesRequest = null)
    {
        foreach (var state in _states)
        {
            state.LoginRequestHandler = loginRequest;
            state.GameLoginHandler = gameLogin;
            state.CharSelectHandler = charSelect;
            state.CharCreateHandler = charCreate;
            state.MoveRequestHandler = moveRequest;
            state.MovementBatchHandler = movementBatch;
            state.SpeechHandler = speech;
            state.AttackRequestHandler = attackRequest;
            state.WarModeHandler = warMode;
            state.DoubleClickHandler = doubleClick;
            state.SingleClickHandler = singleClick;
            state.ItemPickupHandler = itemPickup;
            state.ItemDropHandler = itemDrop;
            state.ItemEquipHandler = itemEquip;
            state.StatusRequestHandler = statusRequest;
            state.TargetResponseHandler = targetResponse;
            state.GumpResponseHandler = gumpResponse;
            state.ClientVersionHandler = clientVersion;
            state.ViewRangeHandler = viewRange;
            state.AOSTooltipHandler = aosTooltip;
            state.TextCommandHandler = textCommand;
            state.ExtendedCommandHandler = extendedCommand;
            state.ResyncRequestHandler = resyncRequest;
            state.LogoutRequestHandler = logoutRequest;
            state.HelpRequestHandler = helpRequest;
            state.ServerSelectHandler = serverSelect;
            state.VendorBuyHandler = vendorBuy;
            state.VendorSellHandler = vendorSell;
            state.SecureTradeHandler = secureTrade;
            state.RenameHandler = rename;
            state.ProfileRequestHandler = profileRequest;

            // Phase 1
            state.DeathMenuHandler = deathMenu;
            state.CharDeleteHandler = charDelete;
            state.DyeResponseHandler = dyeResponse;
            state.PromptResponseHandler = promptResponse;
            state.MenuChoiceHandler = menuChoice;

            // Phase 2
            state.BookPageHandler = bookPage;
            state.BookHeaderHandler = bookHeader;
            state.BulletinBoardRequestListHandler = bulletinBoardRequestList;
            state.BulletinBoardRequestMessageHandler = bulletinBoardRequestMessage;
            state.BulletinBoardPostHandler = bulletinBoardPost;
            state.BulletinBoardDeleteHandler = bulletinBoardDelete;
            state.MapDetailHandler = mapDetail;
            state.MapPinEditHandler = mapPinEdit;

            // Phase 3
            state.GumpTextEntryHandler = gumpTextEntry;
            state.AllNamesRequestHandler = allNamesRequest;
        }
    }

    private NetState? FindFreeSlot()
    {
        foreach (var state in _states)
        {
            if (!state.IsInUse) return state;
        }
        return null;
    }

    public void Dispose() => Stop();

    private bool ShouldLogPacketDebug(byte opcode)
    {
        if (!DebugPackets)
            return false;

        // Skip ping spam by default.
        if (opcode == 0x73)
            return false;

        // Optional opcode whitelist. If empty/null => log all packets.
        var filter = DebugPacketOpcodeFilter;
        if (filter == null || filter.Count == 0)
            return true;

        return filter.Contains(opcode);
    }

    private static string FormatHex(ReadOnlySpan<byte> data, int maxBytes)
    {
        int len = Math.Min(data.Length, maxBytes);
        var sb = new System.Text.StringBuilder(len * 3);
        for (int i = 0; i < len; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        if (data.Length > maxBytes) sb.Append(" ...");
        return sb.ToString();
    }
}
