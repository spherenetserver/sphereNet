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

    /// <summary>DebugPacketCategories, handed to each connection.</summary>
    public HashSet<string>? DebugPacketCategoryFilter { get; set; }
    /// <summary>Warn ([slow_packet]) when a single inbound packet handler runs longer
    /// than this many milliseconds. 0 disables. Attributes net_in loop stalls to a
    /// concrete opcode instead of an opaque phase total.</summary>
    public int SlowPacketWarnMs { get; set; } = 20;
    // Per-ProcessAllInput-pass attribution, surfaced in the main loop's
    // [loop_stall] line so a slow net_in segment names its dominant packet.
    private int _passPacketCount;
    private byte _passSlowestOpcode;
    private long _passSlowestUs;
    public int LastInputPassPacketCount => _passPacketCount;
    public byte LastInputPassSlowestOpcode => _passSlowestOpcode;
    public double LastInputPassSlowestMs => _passSlowestUs / 1000.0;

    /// <summary>Source-X kiStateDataCheckPeriodMilli (CNetworkThread.cpp:135).</summary>
    public const int ByteQuotaCheckPeriodMs = 10_000;

    /// <summary>MAXSIZECLIENTOUT: bytes sent to one client per check period; 0 = off.</summary>
    public long MaxSizeClientOut { get; set; }

    /// <summary>MAXSIZECLIENTIN: bytes received from one client per check period; 0 = off.</summary>
    public long MaxSizeClientIn { get; set; }

    /// <summary>A connection exceeded its byte quota: (state, type 1 = output /
    /// 2 = input, bytes, quota). Returns whether to log it (Source-X
    /// CClient::Event_ExceededNetworkQuota), or null when no client is attached, in
    /// which case the connection is closed.</summary>
    public Func<NetState, byte, long, long, bool?>? ByteQuotaExceeded { get; set; }
    private long _lastByteQuotaCheck;
    public int ClientMaxIP { get; set; } = 16;
    /// <summary>Incoming script packet filter (Source-X CClient::xPacketFilter): handed
    /// every framed packet, registered opcode or not; returning true consumes it with no
    /// further handling.</summary>
    public Func<NetState, byte, byte[], bool>? PacketScriptHook { get; set; }

    /// <summary>Which opcodes have a filter function at all (PACKETx= in sphere.ini).
    /// Null means every opcode. Checked before the packet is copied, so a shard that
    /// hooks nothing pays nothing per packet.</summary>
    public Func<byte, bool>? PacketScriptHookGate { get; set; }

    /// <summary>Outgoing script packet filter (Source-X CClient::xOutPacketFilter),
    /// installed on every connection.</summary>
    public Func<NetState, byte[], bool>? OutPacketScriptHook
    {
        get => _outPacketScriptHook;
        set
        {
            _outPacketScriptHook = value;
            foreach (var state in _states)
                state.OutPacketScriptHook = value;
        }
    }
    private Func<NetState, byte[], bool>? _outPacketScriptHook;

    /// <summary>Which opcodes have an outgoing filter function (OUTPACKETx=).</summary>
    public Func<byte, bool>? OutPacketScriptHookGate
    {
        get => _outPacketScriptHookGate;
        set
        {
            _outPacketScriptHookGate = value;
            foreach (var state in _states)
                state.OutPacketScriptHookGate = value;
        }
    }
    private Func<byte, bool>? _outPacketScriptHookGate;
    public Func<System.Net.IPAddress, bool>? ConnectionAcceptFilter { get; set; }

    /// <summary>Fired when a connection is about to be cleaned up (before Clear).</summary>
    public event Action<int>? OnConnectionClosed;
    public event Action<NetState>? OnConnectionAccepted;
    public event Action<NetState, byte, byte[]>? OnUnknownPacket;
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
        _packetManager.Register(new PacketCreateCharacterEnhanced());
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
        _packetManager.Register(new PacketMailMessage());
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
        _packetManager.Register(new PacketPromptResponseUnicode());
        _packetManager.Register(new PacketEquipMacro());
        _packetManager.Register(new PacketUnequipMacro());
        _packetManager.Register(new PacketPublicHouseContent());
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
        // Three standard registrations upstream has and this server did not, so the
        // client asked and reached the unknown path: the pre-AOS tooltip request
        // (0xB6 -> the same tooltip route as 0xD6), the in-game bug report (0xE0 ->
        // the same place the crash report goes) and the time sync question
        // (0xF1 -> answered with 0xF2).
        _packetManager.Register(new PacketOldToolTipReq());
        _packetManager.Register(new PacketBugReport());
        _packetManager.Register(new PacketTimeSyncRequest());
        _packetManager.Register(new PacketDisconnect());
        _packetManager.Register(new PacketUltimaStoreButton());
        _packetManager.Register(new PacketChatOpen());
        _packetManager.Register(new PacketChatAction());
        // Tip window paging (0xA7, Source-X PacketTipReq) and the 7.0.62.2+ global
        // chat request (0xF9, Source-X PacketGlobalChatReq).
        _packetManager.Register(new PacketTipRequest());
        _packetManager.Register(new PacketGlobalChatRequest());
        // The rest of upstream's standard registry: the KR toolbar (0xEB ->
        // Event_UseToolbar) and five opcodes it registers only to consume them
        // (PacketUnknown / field-skipping handlers), so they never reach the unknown path.
        _packetManager.Register(new PacketUseHotbar());
        _packetManager.Register(new PacketConsumedNoOp(0x3F, 0));  // UltimaLive static update
        _packetManager.Register(new PacketConsumedNoOp(0x69, 0));  // options
        _packetManager.Register(new PacketConsumedNoOp(0xA6, 5));  // scroll closed
        _packetManager.Register(new PacketConsumedNoOp(0xD0, 0));  // config file
        _packetManager.Register(new PacketConsumedNoOp(0xE8, 13)); // remove UI highlight
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
            {
                OnStateCleared(state.RemoteEndPoint?.Address);
                state.Clear();
            }
        }
    }

    /// <summary>E6: cap accepts per main-loop pass. An accept flood (SYN spam,
    /// mass reconnect after a blip) used to hold the loop in the unbounded
    /// accept-while; the surplus stays in the kernel backlog for next pass.</summary>
    public int MaxAcceptsPerPass { get; set; } = 32;

    // E6: counter-based per-IP connection tally maintained on Init/Clear —
    // replaces the full 1100-slot scan that ran on EVERY accept.
    private readonly Dictionary<System.Net.IPAddress, int> _ipTally = [];

    // --- Source-X IP history (CIPHistoryManager / CNetworkManager::acceptNewConnection) ---

    /// <summary>sphere.ini CONNECTINGMAXIP (m_iConnectingMaxIP, default 8): an IP with
    /// more than this many connections not yet in the game is refused.</summary>
    public int ConnectingMaxIP { get; set; } = 8;
    /// <summary>sphere.ini MAXCONNECTREQUESTSPERIP (_iMaxConnectRequestsPerIP, default
    /// 50): connection attempts an IP may make while its history lives; it does not
    /// decay and is forgotten when the history expires (NETTTL after the last
    /// connection closed) or when the IP logs in successfully
    /// (<see cref="ResetConnectRequests"/>). 0 disables.</summary>
    public int MaxConnectRequestsPerIP { get; set; } = 50;
    /// <summary>sphere.ini MAXPINGS (m_iNetMaxPings, default 15): connection attempts
    /// counted like pings, decaying one per max(30, NETTTL/5) seconds.</summary>
    public int MaxPings { get; set; } = 15;
    /// <summary>sphere.ini NETTTL (m_iNetHistoryTTLSeconds, default 300).</summary>
    public int NetHistoryTtlSeconds { get; set; } = 300;
    /// <summary>sphere.ini TIMEOUTINCOMPLETECONN (_iTimeoutIncompleteConnectionMs,
    /// default 5000): a connection still unidentified after this long is closed
    /// (CNetworkManager.cpp:431-448). 0 disables.</summary>
    public int TimeoutIncompleteConnMs { get; set; } = 5000;
    /// <summary>sphere.ini CUOSTATUS / UOGSTATUS (default 1): answer ConnectUO /
    /// UOGateway status pings (CClientLog.cpp:618-671).</summary>
    public bool CUOStatus { get; set; } = true;
    public bool UOGStatus { get; set; } = true;
    /// <summary>CServer::GetStatusString: 0x22 = the UOG line, 0x25 = the ConnectUO line.</summary>
    public Func<byte, string>? StatusStringProvider { get; set; }

    private sealed class IpHistory
    {
        public int Pings;
        public int PingDecay = 30;
        public int ConnectionRequests;
        public int TtlSeconds;
    }

    private readonly Dictionary<IPAddress, IpHistory> _ipHistory = [];
    private long _lastIpHistoryDecayMs;

    /// <summary>The accept-time IP gate (CNetworkManager.cpp:136-179): counts the
    /// attempt, then refuses on MAXPINGS, CONNECTINGMAXIP or MAXCONNECTREQUESTSPERIP.
    /// Loopback is exempt - the host's own panel and bot harness connect from it, the
    /// same exemption the login-tries counter has (CAccount.cpp:846).</summary>
    public bool RejectByIpHistory(IPAddress address, out string reason)
    {
        reason = "";
        if (IPAddress.IsLoopback(address))
            return false;
        if (!_ipHistory.TryGetValue(address, out var h))
            _ipHistory[address] = h = new IpHistory();
        if (h.TtlSeconds < NetHistoryTtlSeconds)
            h.TtlSeconds = NetHistoryTtlSeconds;
        h.ConnectionRequests++;

        bool pingReject = h.Pings++ >= MaxPings;   // HistoryIP::checkPing
        int pending = CountPendingConnections(address);
        if (pingReject)
            reason = $"MAXPINGS reached {h.Pings - 1}/{MaxPings}";
        else if (ConnectingMaxIP > 0 && pending > ConnectingMaxIP)
            reason = $"CONNECTINGMAXIP reached {pending}/{ConnectingMaxIP}";
        else if (MaxConnectRequestsPerIP > 0 && h.ConnectionRequests >= MaxConnectRequestsPerIP)
            reason = $"MaxConnectRequestsPerIP reached {h.ConnectionRequests}/{MaxConnectRequestsPerIP}";
        return reason.Length > 0;
    }

    /// <summary>A successful account login from this address clears its
    /// MAXCONNECTREQUESTSPERIP count. Source-X keeps counting until the history
    /// expires, so a player who logged in a few times within NETTTL - or kept retrying
    /// while refused, which restarts the TTL - stayed locked out until a restart; here
    /// only attempts that never get in accumulate.</summary>
    public void ResetConnectRequests(IPAddress? address)
    {
        if (address != null && _ipHistory.TryGetValue(address, out var h))
            h.ConnectionRequests = 0;
    }

    /// <summary>Connections from this address that have not reached the game server
    /// yet (Source-X m_iPendingConnectionRequests).</summary>
    private int CountPendingConnections(IPAddress address)
    {
        int n = 0;
        foreach (var s in _states)
            if (s.IsInUse && s.ConnectionType != ConnectType.Game &&
                s.RemoteEndPoint?.Address is { } a && a.Equals(address))
                n++;
        return n;
    }

    /// <summary>IPHistoryManager::tick (CIPHistoryManager.cpp:70-107), once a second:
    /// an address with nothing connected counts its TTL down and is forgotten below
    /// zero; pings decay one step every max(30, NETTTL/5) seconds.</summary>
    public void DecayIpHistory(long nowMs, bool force = false)
    {
        if (!force && nowMs - _lastIpHistoryDecayMs < 1000)
            return;
        _lastIpHistoryDecayMs = nowMs;
        List<IPAddress>? expired = null;
        foreach (var (ip, h) in _ipHistory)
        {
            if (_ipTally.GetValueOrDefault(ip) == 0 && --h.TtlSeconds < 0)
                (expired ??= []).Add(ip);
            if (h.Pings > 0 && --h.PingDecay < 0)
            {
                h.Pings--;
                h.PingDecay = Math.Max(30, NetHistoryTtlSeconds / 5);
            }
        }
        if (expired != null)
            foreach (var ip in expired)
                _ipHistory.Remove(ip);
    }

    /// <summary>ConnectUO / UOGateway status pings on a fresh connection
    /// (CClient::OnRxPing, CClientLog.cpp:618-671). Answers (or, when the setting is
    /// off, just refuses) and closes; true when the data was such a ping.</summary>
    public bool TryAnswerStatusPing(NetState state, ReadOnlySpan<byte> data)
    {
        bool uog = data.Length == 1 && data[0] is 0xFF or 0x7F or 0x22;
        bool cuo = data.Length == 4 && data[0] == 0xF1 && ((data[1] << 8) | data[2]) == 4 && data[3] == 0xFF;
        if (!uog && !cuo)
            return false;
        bool allowed = uog ? UOGStatus : CUOStatus;
        if (allowed && StatusStringProvider != null)
        {
            string text = StatusStringProvider(uog ? (byte)0x22 : (byte)0x25);
            state.SendRaw(System.Text.Encoding.ASCII.GetBytes(text));
        }
        _logger.LogInformation("{Kind} status request from {EP}{Rejected}", uog ? "UOG" : "CUO",
            state.RemoteEndPoint, allowed ? "" : " has been rejected");
        state.ConsumeReceived(data.Length);
        state.MarkClosing();
        return true;
    }

    internal void OnStateInit(System.Net.IPAddress? address)
    {
        if (address == null) return;
        _ipTally[address] = _ipTally.GetValueOrDefault(address) + 1;
    }

    internal void OnStateCleared(System.Net.IPAddress? address)
    {
        if (address == null) return;
        if (_ipTally.TryGetValue(address, out int n))
        {
            if (n <= 1) _ipTally.Remove(address);
            else _ipTally[address] = n - 1;
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
            int accepted = 0;
            while (_listenSocket.Poll(0, SelectMode.SelectRead))
            {
                if (MaxAcceptsPerPass > 0 && accepted >= MaxAcceptsPerPass)
                    break; // rest of the backlog waits for the next pass
                accepted++;
                Socket clientSocket = _listenSocket.Accept();

                if (ConnectionAcceptFilter != null &&
                    clientSocket.RemoteEndPoint is IPEndPoint filterEp &&
                    ConnectionAcceptFilter(filterEp.Address))
                {
                    _logger.LogWarning("Connection from {IP} rejected by accept filter", filterEp.Address);
                    clientSocket.Close();
                    continue;
                }

                if (clientSocket.RemoteEndPoint is IPEndPoint histEp &&
                    RejectByIpHistory(histEp.Address, out string histReason))
                {
                    _logger.LogWarning("Connection from {IP} rejected: {Reason}", histEp.Address, histReason);
                    clientSocket.Close();
                    continue;
                }

                if (ClientMaxIP > 0 && clientSocket.RemoteEndPoint is System.Net.IPEndPoint ep &&
                    _ipTally.GetValueOrDefault(ep.Address) >= ClientMaxIP)
                {
                    _logger.LogWarning("IP limit ({Limit}) reached for {IP}, rejecting", ClientMaxIP, ep.Address);
                    clientSocket.Close();
                    continue;
                }

                var slot = FindFreeSlot();
                if (slot == null)
                {
                    _logger.LogWarning("No free slots, rejecting connection from {EP}", clientSocket.RemoteEndPoint);
                    clientSocket.Close();
                    continue;
                }

                slot.Init(clientSocket);
                OnStateInit(slot.RemoteEndPoint?.Address);
                slot.DebugPackets = DebugPackets;
                slot.DebugPacketOpcodeFilter = DebugPacketOpcodeFilter;
                slot.DebugPacketCategoryFilter = DebugPacketCategoryFilter;
                _logger.LogInformation("Connection #{Id} from {EP}", slot.Id, slot.RemoteEndPoint);
                try
                {
                    OnConnectionAccepted?.Invoke(slot);
                }
                catch (Exception ex)
                {
                    // A handler wiring up the new client must not take down the
                    // accept pass (or the main loop) — drop this connection only.
                    _logger.LogError(ex,
                        "OnConnectionAccepted threw for #{Id} from {EP}; closing connection",
                        slot.Id, slot.RemoteEndPoint);
                    slot.MarkClosing();
                }
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
        _passPacketCount = 0;
        _passSlowestOpcode = 0;
        _passSlowestUs = 0;
        foreach (var state in _states)
        {
            if (!state.CanReceive) continue;

            // Per-connection containment: a throw while receiving, decrypting or
            // dispatching this connection's data must never escape into the main
            // loop (that would kill the whole server and skip the shutdown save).
            // Isolate it, log with the remote endpoint, and drop only this one.
            try
            {
                int read = state.Receive();
                if (read < 0)
                {
                    _logger.LogInformation("Connection #{Id} lost; {Traffic}", state.Id, state.DescribeRecentTraffic());
                    state.MarkClosing();
                    continue;
                }

                // Nothing new from the socket is not the same as nothing to do: the
                // previous pass may have stopped after a faulting handler with whole
                // packets still buffered. Those are processed now, as Source-X keeps
                // processing an existing raw buffer when no new raw packet arrived
                // (CNetworkInput.cpp:156-176). A buffer that ends in a partial
                // packet, or a connection still waiting to complete its seed or
                // encryption detection, does need new bytes; its deadline is Tick's.
                if (read == 0 && !HasBufferedPackets(state)) continue;

                ProcessInput(state);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Input processing threw for #{Id} from {EP}; closing connection",
                    state.Id, state.RemoteEndPoint);
                state.MarkClosing();
            }
        }
    }

    /// <summary>Whether the connection holds received bytes that can be processed
    /// without new socket data: whole packets left behind by a faulting handler. A connection waiting on a partial packet, its seed or its
    /// encryption detection cannot make progress without more bytes.</summary>
    private static bool HasBufferedPackets(NetState state) =>
        state.ReceivedData.Length > 0 &&
        state.IsSeeded &&
        state.Crypto.IsInitialized &&
        state.PendingPacketStartTick == 0;

    private void ProcessInput(NetState state)
    {
        var data = state.ReceivedData;
        if (data.Length == 0) return;

        // First bytes of a new connection is the seed.
        // Classic clients send 4 raw bytes. 7.0+ clients send packet 0xEF (21 bytes).
        if (!state.IsSeeded)
        {
            if (TryAnswerStatusPing(state, data))
                return;
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

        // Every whole packet received is processed in this pass, as Source-X does
        // (CNetworkInput::processData, CNetworkInput.cpp:211-224): there is no
        // per-pass packet cap and no packet-count disconnect. A client that sends
        // too much is caught by the MAXSIZECLIENTIN byte quota (CheckByteQuotas),
        // and malformed data closes the connection below. Source-X's
        // MAXPACKETSPERTICK limits output (packet.h NETWORK_MAXPACKETS,
        // CNetworkOutput.cpp:214), not input. Processing stops once the connection
        // is closing, as upstream's loop checks isReadClosed before each packet.
        int consumed = 0;
        while (consumed < data.Length && !state.IsClosing)
        {
            byte opcode = data[consumed];
            int definedLen = PacketDefinitions.GetPacketLength(opcode, state);
            bool hasLengthField = definedLen == 0;
            bool legacyAssistPacket = false;
            int packetLen = definedLen;

            if (hasLengthField)
            {
                if (data.Length - consumed < 3)
                {
                    // The length field itself has not arrived: still a partial packet,
                    // and its deadline starts now, not when the header completes.
                    MarkOrDropPartialPacket(state, opcode, 0);
                    break;
                }
                packetLen = (data[consumed + 1] << 8) | data[consumed + 2];
                if (opcode == 0xBE && TryGetLegacyAssistPacketLength(data[consumed..], state, packetLen, out int legacyLen))
                {
                    packetLen = legacyLen;
                    hasLengthField = false;
                    legacyAssistPacket = true;
                }
            }

            // A length-prefixed frame can never be shorter than its own 3-byte header.
            // Accepting 1 or 2 "consumed" part of the header and parsed the rest of it as
            // the next packet. Upstream's checkLength treats it as incomplete and waits
            // forever (packet.cpp:1175); the frame is rejected here instead.
            if (hasLengthField && packetLen is > 0 and < 3)
                packetLen = 0;

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

            bool scriptFiltered = PacketScriptHook != null &&
                (PacketScriptHookGate == null || PacketScriptHookGate(opcode));

            if (opcode == 0x73 && packetLen == 2 && !scriptFiltered)
            {
                state.OnPingReceived(data[consumed + 1]);
                consumed += packetLen;
                continue;
            }

            // Source-X runs the script filter for every framed packet, whether a handler
            // is registered for it or not (CNetworkInput.cpp:367 and :387); RETURN 1 skips
            // it with no core handling. Upstream skips the whole rest of the buffer for an
            // unregistered opcode since it cannot size it; the frame is known here, so only
            // the packet itself is consumed.
            if (scriptFiltered &&
                InvokePacketScriptHook(state, opcode, data.Slice(consumed, packetLen).ToArray()))
            {
                consumed += packetLen;
                continue;
            }

            var handler = _packetManager.GetHandler(opcode);
            if (handler != null)
            {
                if (ShouldLogPacketDebug(opcode))
                {
                    _logger.LogDebug("RECV #{Id} 0x{Op:X2} ({Name}) len={Len} data=[{Data}]",
                        state.Id, opcode, handler.GetType().Name,
                        packetLen, FormatHex(data.Slice(consumed, packetLen), 32));
                }

                int payloadOffset = hasLengthField && !legacyAssistPacket ? 3 : 1;
                int payloadLength = packetLen - payloadOffset;
                if (payloadLength < 0)
                {
                    _logger.LogWarning("Invalid packet length #{Id} 0x{Op:X2}: total={Total} payloadOffset={Offset}",
                        state.Id, opcode, packetLen, payloadOffset);
                    state.MarkClosing();
                    break;
                }

                var buffer = new PacketBuffer(data.Slice(consumed + payloadOffset, payloadLength).ToArray());
                long handlerStart = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    handler.OnReceive(buffer, state);
                }
                catch (Exception ex)
                {
                    // The handler surface (game logic + synchronous .scp triggers)
                    // is the largest attacker-reachable code path. A throw here must
                    // never propagate out of the input phase and terminate the server -
                    // but it must not end the player's session either. Upstream tallies
                    // the faults per connection and kicks only past ten
                    // (m_packetExceptions, CNetworkInput.cpp:420); a single one is logged
                    // and the client keeps playing. Closing on the first throw meant one
                    // bug in a use handler read, to the player, as the client freezing
                    // with nothing to do but restart it.
                    state.PacketExceptionCount++;
                    bool overExceptionBudget = state.PacketExceptionCount > MaxPacketExceptions;
                    _logger.LogError(ex,
                        "Packet handler threw for #{Id} 0x{Op:X2} ({Name}) from {EP} (fault {Count} of {Max}){Action}",
                        state.Id, opcode, handler.GetType().Name, state.RemoteEndPoint,
                        state.PacketExceptionCount, MaxPacketExceptions,
                        overExceptionBudget ? "; closing connection" : "");
                    if (overExceptionBudget)
                        state.MarkClosing();
                    // The failing packet is consumed either way: leaving it buffered
                    // would re-parse and re-throw it on every pass, and burn through the
                    // budget on one bad packet. The rest of this read is left for the
                    // next pass, as upstream abandons the rest of its own.
                    consumed += packetLen;
                    break;
                }
                long handlerUs = (System.Diagnostics.Stopwatch.GetTimestamp() - handlerStart)
                    * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
                _passPacketCount++;
                if (handlerUs > _passSlowestUs)
                {
                    _passSlowestUs = handlerUs;
                    _passSlowestOpcode = opcode;
                }
                if (SlowPacketWarnMs > 0 && handlerUs > SlowPacketWarnMs * 1000L)
                {
                    _logger.LogWarning("[slow_packet] #{Id} op=0x{Op:X2} ({Name}) len={Len} took={Ms}ms",
                        state.Id, opcode, handler.GetType().Name, packetLen,
                        (handlerUs / 1000.0).ToString("F1"));
                }
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
                else if (state.ShouldLogUnknownOpcode(opcode))
                {
                    // First time this opcode is seen on the connection: surface it once
                    // so unsupported modern packets are visible. Repeats fall to Trace.
                    _logger.LogDebug("Ignoring unsupported packet 0x{Op:X2} (len={Len}) from #{Id}; further occurrences suppressed",
                        opcode, packetLen, state.Id);
                }
                else
                {
                    _logger.LogTrace("Unhandled packet 0x{Op:X2} from #{Id}", opcode, state.Id);
                }
            }

            consumed += packetLen;
        }

        if (consumed > 0)
            state.ConsumeReceived(consumed);
    }

    /// <summary>How many handler faults one connection may cause before it is dropped.
    /// Upstream's threshold (m_packetExceptions > 10, CNetworkInput.cpp:421): enough that
    /// a single gameplay bug does not end a session, few enough that a client
    /// deliberately feeding the server faults still gets dropped.</summary>
    public const int MaxPacketExceptions = 10;

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

    private static bool TryGetLegacyAssistPacketLength(
        ReadOnlySpan<byte> data,
        NetState state,
        int declaredLength,
        out int packetLen)
    {
        packetLen = 0;

        // 0xBE has two forms in the wild:
        // - Modern/Razor CE: 0xBE + uint16 length + ASCII version text.
        // - Legacy assistant clients: 0xBE + uint32 version, with no length field.
        //
        // The legacy uint can start with values like 00 FB, which look like a
        // 251-byte variable packet and cause following movement packets to sit
        // behind a phantom partial 0xBE. Treat only implausibly large/invalid
        // declared lengths as the legacy 5-byte form, and prefer a real packet
        // boundary immediately after it when more bytes are already buffered.
        if (data.Length < 5)
            return false;

        if (declaredLength is >= 3 and <= 128)
            return false;

        if (data.Length == 5 || LooksLikePacketBoundary(data[5..], state))
        {
            packetLen = 5;
            return true;
        }

        return false;
    }

    private static bool LooksLikePacketBoundary(ReadOnlySpan<byte> data, NetState state)
    {
        if (data.IsEmpty)
            return false;

        int len = PacketDefinitions.GetPacketLength(data[0], state);
        if (len > 0)
            return true;

        if (data.Length < 3)
            return false;

        int declaredLength = (data[1] << 8) | data[2];
        return declaredLength is >= 3 and <= MaxPacketSize;
    }

    /// <summary>
    /// Attempt to detect encryption and decrypt the first packet.
    /// Returns true if data was processed (or should be retried), false to wait for more data.
    /// </summary>
    private bool TryInitCrypto(NetState state, ReadOnlySpan<byte> data)
    {
        var config = CryptConfig ?? new CryptConfig();
        // Source-X CClient::xCanEncLogin (CClientLog.cpp:1015): an unencrypted client is
        // accepted only by USENOCRYPT. An empty or missing key list does not widen that;
        // it is reported at startup (GetCryptPolicyWarnings) instead.
        bool allowNoCrypt = UseNoCrypt;

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

        // 62-64 bytes can be the start of a 65-byte game login, which no candidate
        // above could test yet. That is not a failed detection: wait for the rest
        // (the partial-packet deadline and TIMEOUTINCOMPLETECONN still bound it).
        if (data.Length < 65 && CouldBeGameLoginStart(state, data, config, allowNoCrypt))
        {
            MarkOrDropPartialPacket(state, 0x80, 65);
            return false;
        }

        // The dump is masked where a password would sit: these bytes may be plaintext,
        // and if they are not, the login cipher is keyed by public keys plus the seed
        // printed on the same line.
        _logger.LogWarning(
            "Failed to detect encryption for #{Id}: seed=0x{Seed:X8}, first=0x{B:X2}, len={Len}, useCrypt={UseCrypt}, useNoCrypt={UseNoCrypt}, keys={Keys}, attempts='{Attempts}', diag=\"{Diag}\", raw=[{Raw}]",
            state.Id, state.Seed, data[0], data.Length, UseCrypt, allowNoCrypt, config.Keys.Count,
            attempts.Trim(), diagnostics,
            PacketLogRedaction.FormatLoginCandidate(data, 96, GetCryptoCandidateOffsets(data)));
        state.MarkClosing();
        return false;
    }

    /// <summary>Whether a 62-64 byte first chunk that no login candidate accepted can
    /// still be the beginning of a game login (0x91): plaintext when unencrypted
    /// clients are allowed, or the prefix of a game-login cipher stream.</summary>
    private bool CouldBeGameLoginStart(NetState state, ReadOnlySpan<byte> data, CryptConfig config, bool allowNoCrypt)
    {
        if (allowNoCrypt && data[0] == 0x91)
            return true;
        if (!UseCrypt)
            return false;
        foreach (uint seedCandidate in GetCryptoSeedCandidates(state.Seed))
            if (CryptoState.IsGameLoginPrefix(seedCandidate, data, config))
                return true;
        return false;
    }

    /// <summary>Startup warnings for an encryption policy under which fewer clients
    /// can log in than the settings suggest, or none at all.</summary>
    public IReadOnlyList<string> GetCryptPolicyWarnings()
    {
        var warnings = new List<string>();
        int keys = CryptConfig?.Keys.Count ?? 0;
        if (UseCrypt && keys == 0)
            warnings.Add(UseNoCrypt
                ? "UseCrypt=1 but no encryption keys are loaded (sphereCrypt.ini missing, empty or unparsable): clients that need a key cannot log in; unencrypted clients are accepted because UseNoCrypt=1"
                : "UseCrypt=1 but no encryption keys are loaded (sphereCrypt.ini missing, empty or unparsable) and UseNoCrypt=0: clients that need a key cannot log in and unencrypted clients are refused");
        if (!UseCrypt && !UseNoCrypt)
            warnings.Add("UseCrypt=0 and UseNoCrypt=0: no client can log in");
        return warnings;
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
        ReplaceReceivedData(state, newData, decoded.Length);
    }

    /// <summary>Put the detected first packet (already decrypted) back in front of
    /// whatever arrived with it. Only the first <paramref name="decryptedLength"/>
    /// bytes are plaintext: the rest is still ciphertext and is decrypted next by the
    /// cipher that just decoded the first packet, exactly as if it had arrived in a
    /// later read.</summary>
    private static void ReplaceReceivedData(NetState state, byte[] newData, int decryptedLength)
    {
        state.ConsumeReceived(state.ReceivedData.Length);
        state.InjectReceived(newData);
        state.UndecryptedOffset = decryptedLength;
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
                    FlushOne(state);
            }
            return;
        }

        Parallel.ForEach(
            _states,
            new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
            state =>
            {
                if (state.IsInUse)
                    FlushOne(state);
            });
    }

    /// <summary>Flush one connection with containment. FlushOutput already
    /// swallows SocketException internally; this catches anything else (a throw
    /// in outbound compression/encryption of a broadcast) so one bad flush can
    /// neither stop the other connections nor escape into the main loop.</summary>
    private void FlushOne(NetState state)
    {
        try
        {
            state.FlushOutput();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "FlushOutput threw for #{Id} from {EP}; closing connection",
                state.Id, state.RemoteEndPoint);
            state.MarkClosing();
        }
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
        CheckByteQuotas(now);
        DecayIpHistory(now);
        CryptoState.PurgeExpiredRelayKeys(now);
        foreach (var state in _states)
        {
            if (!state.IsInUse) continue;

            // TIMEOUTINCOMPLETECONN: still unidentified (CONNECT_UNK) this long after
            // connecting - closed (CNetworkManager.cpp:436-447).
            if (!state.IsClosing && TimeoutIncompleteConnMs > 0 &&
                state.ConnectionType == ConnectType.Unknown &&
                (DateTime.UtcNow - state.ConnectTime).TotalMilliseconds > TimeoutIncompleteConnMs)
            {
                _logger.LogWarning("Force closing connection #{Id} from {EP}: timed out before completing login",
                    state.Id, state.RemoteEndPoint);
                state.MarkClosing();
            }

            // A partial packet that has not completed within its deadline. Checked
            // here, not only when bytes arrive, so a client that sends part of a
            // packet and then goes silent is still dropped.
            if (!state.IsClosing && state.PendingPacketStartTick > 0 &&
                now - state.PendingPacketStartTick > PartialPacketTimeoutMs)
            {
                _logger.LogWarning(
                    "Partial packet timeout for #{Id}: opcode=0x{Op:X2}, expected={Len}, buffered={Buffered}",
                    state.Id, state.PendingPacketOpcode, state.PendingPacketLength, state.ReceivedData.Length);
                state.MarkClosing();
            }

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
                OnStateCleared(state.RemoteEndPoint?.Address);
                state.Clear();
            }
        }
    }

    /// <summary>Source-X CNetworkThread::tick's legitimacy check (CNetworkThread.cpp:
    /// 135-189): once the check period has passed, every connection whose out-byte
    /// counter is over <see cref="MaxSizeClientOut"/> (type 1) - or else whose in-byte
    /// counter is over <see cref="MaxSizeClientIn"/> (type 2) - is handed to
    /// <see cref="ByteQuotaExceeded"/>, and every connection's counters restart from
    /// zero. A connection with no client behind it (the handler returns null, or no
    /// handler is installed) is closed. As in Source-X, this byte quota is the only
    /// input-volume limit: packet processing itself has no per-pass cap.</summary>
    public void CheckByteQuotas(long now)
    {
        if (now - _lastByteQuotaCheck <= ByteQuotaCheckPeriodMs)
            return;
        _lastByteQuotaCheck = now;

        long maxOut = MaxSizeClientOut, maxIn = MaxSizeClientIn;
        foreach (var state in _states)
        {
            if (!state.IsInUse)
                continue;
            if (!state.IsClosing && (maxOut != 0 || maxIn != 0))
            {
                byte type = 0;
                long bytes = 0, quota = 0;
                long outBytes = state.OutByteCounter, inBytes = state.InByteCounter;
                if (maxOut != 0 && outBytes > maxOut)
                {
                    type = 1;
                    bytes = outBytes;
                    quota = maxOut;
                }
                else if (maxIn != 0 && inBytes > maxIn)
                {
                    type = 2;
                    bytes = inBytes;
                    quota = maxIn;
                }

                if (type != 0)
                {
                    bool? log = null;
                    try
                    {
                        log = ByteQuotaExceeded?.Invoke(state, type, bytes, quota);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Network quota handler threw for #{Id}", state.Id);
                        log = true;
                    }
                    if (log == null)
                        state.MarkClosing();

                    if (log != false)
                    {
                        string account = string.IsNullOrEmpty(state.AccountName) ? "NA" : state.AccountName;
                        _logger.LogWarning(
                            "NetState id {Id} (IP: {IP}, Account: {Account}) exceeded its {Dir} quota ({Bytes}/{Quota}).",
                            state.Id, state.RemoteEndPoint?.Address, account,
                            type == 2 ? "input" : "output", bytes, quota);
                    }
                }
            }
            state.ResetByteCounters();
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
        Action<NetState, uint, uint>? mailMessage = null,
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
        Action<NetState, uint, uint>? bulletinBoardRequestHead = null,
        Action<NetState, uint, uint>? bulletinBoardRequestMessage = null,
        Action<NetState, uint, uint, string, string[]>? bulletinBoardPost = null,
        Action<NetState, uint, uint>? bulletinBoardDelete = null,
        Action<NetState, uint>? mapDetail = null,
        Action<NetState, uint, byte, byte, ushort, ushort>? mapPinEdit = null,
        // Phase 3
        Action<NetState, uint, ushort, byte, string>? gumpTextEntry = null,
        Action<NetState, uint>? allNamesRequest = null,
        Action<NetState, ushort, uint, PacketBuffer>? encodedCommand = null,
        Action<NetState>? crashReport = null,
        Action<NetState, byte>? clientUiButton = null,
        Action<NetState, ushort, string>? chatAction = null,
        Action<NetState, IReadOnlyList<uint>>? equipMacro = null,
        Action<NetState, IReadOnlyList<ushort>>? unequipMacro = null,
        Action<NetState, bool>? publicHouseContent = null,
        Action<NetState, ushort, byte>? skillLock = null,
        Action<NetState, uint, uint, uint>? secureTradeGold = null,
        Action<NetState, ushort>? tipRequest = null,
        Action<NetState, byte, string>? globalChat = null,
        Action<NetState, byte, uint>? useToolbar = null)
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
            state.MailMessageHandler = mailMessage;
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
            state.SkillLockHandler = skillLock;
            state.ExtendedCommandHandler = extendedCommand;
            state.EncodedCommandHandler = encodedCommand;
            state.CrashReportHandler = crashReport;
            state.ClientUiButtonHandler = clientUiButton;
            state.ChatActionHandler = chatAction;
            state.TipRequestHandler = tipRequest;
            state.GlobalChatHandler = globalChat;
            state.UseToolbarHandler = useToolbar;
            state.ResyncRequestHandler = resyncRequest;
            state.LogoutRequestHandler = logoutRequest;
            state.HelpRequestHandler = helpRequest;
            state.ServerSelectHandler = serverSelect;
            state.VendorBuyHandler = vendorBuy;
            state.VendorSellHandler = vendorSell;
            state.SecureTradeHandler = secureTrade;
            state.SecureTradeGoldHandler = secureTradeGold;
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
            state.BulletinBoardRequestHeadHandler = bulletinBoardRequestHead;
            state.BulletinBoardRequestMessageHandler = bulletinBoardRequestMessage;
            state.BulletinBoardPostHandler = bulletinBoardPost;
            state.BulletinBoardDeleteHandler = bulletinBoardDelete;
            state.MapDetailHandler = mapDetail;
            state.MapPinEditHandler = mapPinEdit;

            // Phase 3
            state.GumpTextEntryHandler = gumpTextEntry;
            state.AllNamesRequestHandler = allNamesRequest;
            state.EquipMacroHandler = equipMacro;
            state.UnequipMacroHandler = unequipMacro;
            state.PublicHouseContentHandler = publicHouseContent;
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

    private bool ShouldLogPacketDebug(byte opcode) =>
        State.PacketDebugFilter.ShouldLog(DebugPackets, DebugPacketOpcodeFilter, opcode);

    /// <summary>Hex of a packet for the log, with the password field of a login or
    /// character-delete packet masked.</summary>
    private static string FormatHex(ReadOnlySpan<byte> data, int maxBytes) =>
        PacketLogRedaction.FormatPacket(data, maxBytes);
}
