using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SphereNet.Game.Diagnostics.Behaviors;
using SphereNet.Game.Diagnostics.Scenarios;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Manages multiple bot clients for stress testing.
/// Creates bots that connect via real TCP and simulate player behavior.
/// </summary>
public sealed class BotEngine : IDisposable
{
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, BotClient> _bots = new();
    private CancellationTokenSource? _globalCts;
    private int _nextBotId;
    private bool _disposed;
    private BotSpawnCity _spawnCity = BotSpawnCity.All;
    private BotBehavior _lastBehavior = BotBehavior.SmartAI;
    private int _lastCount;

    // Stats
    private long _lastStatsLogMs;
    private int _lastPacketsSent;
    private int _lastPacketsReceived;

    // Bot mode flag — used by network layer to bypass rate limiting for loopback
    public static bool BotModeActive { get; private set; }
    public static event Action<bool>? OnBotModeChanged;

    /// <summary>Characters the server is currently driving as load-test bots.
    /// Populated when a bot session starts and emptied when bots stop.</summary>
    private static readonly ConcurrentDictionary<string, byte> _liveBotChars =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="charName"/> belongs to a bot session this server is
    /// actually running. The gate for any diagnostic privilege: a name-prefix test
    /// alone handed the vendor payment exemption to any player who happened to pick
    /// a name starting with the bot prefix.
    /// </summary>
    public static bool IsLiveBotCharacter(string? charName) =>
        BotModeActive && !string.IsNullOrEmpty(charName) && _liveBotChars.ContainsKey(charName);

    internal static void RegisterBotCharacter(string charName)
    {
        if (!string.IsNullOrEmpty(charName)) _liveBotChars[charName] = 0;
    }

    internal static void ForgetAllBotCharacters()
    {
        _liveBotChars.Clear();
        _liveBotAccounts.Clear();
        _liveBotSerials.Clear();
        _traders.Clear();
    }

    /// <summary>Characters of the running bots, by serial: what a trading bot looks
    /// for, so it hands items to another bot and never to a player or an NPC.</summary>
    private static readonly ConcurrentDictionary<uint, byte> _liveBotSerials = new();

    internal static void RegisterBotSerial(uint serial)
    {
        if (serial != 0) _liveBotSerials[serial] = 0;
    }

    public static bool IsLiveBotSerial(uint serial) => _liveBotSerials.ContainsKey(serial);

    /// <summary>The bots running the trade behaviour, so a trader can walk to another
    /// trader instead of waiting for one to wander into view.</summary>
    private static readonly ConcurrentDictionary<int, BotClient> _traders = new();

    internal static void RegisterTrader(BotClient bot) => _traders[bot.BotId] = bot;
    internal static void ForgetTrader(BotClient bot) => _traders.TryRemove(bot.BotId, out _);

    /// <summary>The nearest other trader that is in the world.</summary>
    internal static BotClient? NearestTrader(BotClient self)
    {
        BotClient? best = null;
        int bestDist = int.MaxValue;
        foreach (var other in _traders.Values)
        {
            if (other == self || other.State != BotState.Playing || other.World.CharUid == 0)
                continue;
            int d = self.World.DistanceTo(other.World.X, other.World.Y);
            if (d < bestDist) { bestDist = d; best = other; }
        }
        return best;
    }

    private static long _tradesOffered, _tradesOpened, _tradesClosed;
    internal static void CountTradeOffered() => Interlocked.Increment(ref _tradesOffered);

    private static long _tradeNoPartner, _tradeNoBackpack, _tradeNoItem, _tradeTooFar, _tradeBusy;
    /// <summary>Why a trading bot's turn ended without an offer.</summary>
    internal static void CountTradeSkip(string reason)
    {
        switch (reason)
        {
            case "partner": Interlocked.Increment(ref _tradeNoPartner); break;
            case "backpack": Interlocked.Increment(ref _tradeNoBackpack); break;
            case "item": Interlocked.Increment(ref _tradeNoItem); break;
            case "busy": Interlocked.Increment(ref _tradeBusy); break;
            default: Interlocked.Increment(ref _tradeTooFar); break;
        }
    }

    public static string TradeSkipSummary =>
        $"no partner {Interlocked.Read(ref _tradeNoPartner)}, no backpack {Interlocked.Read(ref _tradeNoBackpack)}, " +
        $"no item {Interlocked.Read(ref _tradeNoItem)}, too far {Interlocked.Read(ref _tradeTooFar)}, " +
        $"already trading {Interlocked.Read(ref _tradeBusy)}";
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _tradeOutcomes = new();
    /// <summary>An offer that opened no window, or a window that never closed, with
    /// the server's message where it gave one.</summary>
    internal static void CountTradeOutcome(string outcome) =>
        _tradeOutcomes.AddOrUpdate(outcome, 1, (_, n) => n + 1);
    public static string TradeOutcomeSummary =>
        _tradeOutcomes.IsEmpty ? "none" : string.Join(", ",
            _tradeOutcomes.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"));
    // Incoming stream health, over all bots: packets by opcode, and the times a
    // stream was found off a packet boundary (with the first few, as bytes).
    private static readonly long[] _receivedByOpcode = new long[256];
    private static long _framingErrors;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<string> _framingSamples = new();

    internal static void CountReceivedOpcode(byte opcode) => Interlocked.Increment(ref _receivedByOpcode[opcode]);

    internal static void CountFramingError(string sample)
    {
        if (Interlocked.Increment(ref _framingErrors) <= 5)
            _framingSamples.Enqueue(sample);
    }

    public static string StreamSummary
    {
        get
        {
            var ops = Enumerable.Range(0, 256)
                .Select(op => (op, n: Interlocked.Read(ref _receivedByOpcode[op])))
                .Where(x => x.n > 0)
                .OrderByDescending(x => x.n)
                .Select(x => $"0x{x.op:X2}:{x.n}");
            string samples = _framingSamples.IsEmpty ? "" : " e.g. [" + string.Join("] [", _framingSamples) + "]";
            return $"framing errors {Interlocked.Read(ref _framingErrors)}{samples}; opcodes {string.Join(" ", ops)}";
        }
    }

    internal static void CountTradeOpened() => Interlocked.Increment(ref _tradesOpened);
    internal static void CountTradeClosed() => Interlocked.Increment(ref _tradesClosed);
    /// <summary>Trade windows bots saw open and close (both sides count).</summary>
    public static (long Offered, long Opened, long Closed) TradeCounts =>
        (Interlocked.Read(ref _tradesOffered), Interlocked.Read(ref _tradesOpened), Interlocked.Read(ref _tradesClosed));

    // --- soak hooks, wired by the server ---

    /// <summary>Run the world consistency audit on the main loop; one line per anomaly.</summary>
    public Func<IReadOnlyList<string>>? WorldAudit { get; set; }

    /// <summary>Ask the server for a world save.</summary>
    public Action? RequestSave { get; set; }

    /// <summary>Saves finished and saves failed since the server started.</summary>
    public Func<(int Saves, int Failures)>? SaveStats { get; set; }

    /// <summary>Where the soak CSV goes (the server's log folder).</summary>
    public string? LogDirectory { get; set; }

    private SoakMonitor? _soak;
    public SoakResult? LastSoakResult { get; private set; }

    /// <summary>Accounts the server made for the bots it is running.</summary>
    private static readonly ConcurrentDictionary<string, byte> _liveBotAccounts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="accountName"/> is the account of a bot this
    /// server is running right now. The bot account prefix is a reserved section name
    /// no player can register through login auto-create; the login path makes the
    /// account through AccountManager.CreateInternalAccount only for a name this
    /// engine registered (AccountManager.InternalAccountGate), and the bot
    /// conveniences go to such engine-made accounts only.</summary>
    public static bool IsLiveBotAccount(string? accountName) =>
        BotModeActive && !string.IsNullOrEmpty(accountName) && _liveBotAccounts.ContainsKey(accountName);

    internal static void RegisterBotAccount(string accountName)
    {
        if (!string.IsNullOrEmpty(accountName)) _liveBotAccounts[accountName] = 0;
    }

    /// <summary>One password per engine, never a constant: a live bot account is
    /// given the bot conveniences, so its password must not be guessable.</summary>
    private readonly string _sessionPassword =
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8));

    // Anomaly tracking
    public readonly ConcurrentQueue<BotAnomaly> Anomalies = new();
    public int AnomalyCount => Anomalies.Count;

    // Scenario state
    private IBotScenario? _activeScenario;
    private CancellationTokenSource? _scenarioCts;
    private long _scenarioStartMs;
    private int _scenarioBotCount;
    public BotScenarioReport? LastScenarioReport { get; private set; }
    public IBotScenario? ActiveScenario => _activeScenario;

    /// <summary>Thresholds the finished scenario is judged against.</summary>
    public BotPerformanceGate ScenarioGate { get; set; } = new();

    // Server tick samples bounded to the scenario window: created at scenario start,
    // fed by RecordServerTickSample while it runs, frozen at finish. The server-wide
    // histogram covers the whole uptime and cannot answer "how did the ticks look
    // while this scenario ran".
    private readonly object _scenarioTickLock = new();
    private TickHistogram? _scenarioTicks;

    /// <summary>Record one server tick duration into the running scenario's window.
    /// A no-op when no scenario is measuring, so ticks before start and after finish
    /// never reach the gate. Called from the server loop when the bots run in-process;
    /// a scenario driven against a remote server records nothing and its tick checks
    /// report "not measured".</summary>
    public void RecordServerTickSample(int elapsedMs)
    {
        if (_scenarioTicks == null) return;
        lock (_scenarioTickLock)
            _scenarioTicks?.Record(elapsedMs);
    }

    /// <summary>Open the scenario measurement window.</summary>
    internal void BeginScenarioWindow(IBotScenario scenario, int botCount)
    {
        lock (_scenarioTickLock)
            _scenarioTicks = new TickHistogram(2000, 1);
        _activeScenario = scenario;
        _scenarioBotCount = botCount;
        _scenarioStartMs = Environment.TickCount64;
    }

    /// <summary>Close the window and take its samples; later ticks are ignored.</summary>
    private TickHistogram? EndScenarioWindow()
    {
        lock (_scenarioTickLock)
        {
            var ticks = _scenarioTicks;
            _scenarioTicks = null;
            return ticks;
        }
    }

    /// <summary>Move requests and server rejects summed over the scenario's bots.
    /// The scenario starts from a fresh bot set, so these are scenario totals.</summary>
    public (int Requests, int Rejects) GetMoveTotals()
    {
        int requests = 0, rejects = 0;
        foreach (var bot in _bots.Values)
        {
            requests += bot.World.TotalMoveRequests;
            rejects += bot.World.TotalMoveRejects;
        }
        return (requests, rejects);
    }
    public bool IsScenarioRunning => _activeScenario != null && _scenarioCts != null && !_scenarioCts.IsCancellationRequested;

    public static readonly IBotScenario[] AvailableScenarios =
    [
        new WalkTalkScenario(),
        new VendorCycleScenario(),
        new CombatSoakScenario(),
        new MixedLoadScenario(),
        new SoakScenario(),
        new LoginStormScenario(),
    ];

    /// <summary>City bounding boxes for bot spawning (minX, minY, maxX, maxY, defaultZ).</summary>
    public static readonly Dictionary<BotSpawnCity, (short MinX, short MinY, short MaxX, short MaxY, sbyte Z)> CityBounds = new()
    {
        [BotSpawnCity.Britain]  = (1400, 1520, 1540, 1760, 10),
        [BotSpawnCity.Trinsic]  = (1820, 2680, 2050, 2870,  0),
        [BotSpawnCity.Moonglow] = (4400, 1040, 4560, 1220,  0),
        [BotSpawnCity.Yew]      = ( 470,  810,  650, 1010,  0),
        [BotSpawnCity.Minoc]    = (2460,  380, 2580,  570, 15),
        [BotSpawnCity.Vesper]   = (2840,  670, 3010,  860,  0),
        [BotSpawnCity.Skara]    = ( 560, 2060,  670, 2210,  0),
        [BotSpawnCity.Jhelom]   = (1290, 3750, 1470, 3860,  0),
    };

    public int TotalBots => _bots.Count;
    public int ActiveBots => _bots.Values.Count(b => b.State == BotState.Playing);
    public int ConnectingBots => _bots.Values.Count(b => b.State == BotState.Connecting || b.State == BotState.LoggingIn);
    public int TotalPacketsSent => _bots.Values.Sum(b => b.PacketsSent);
    public int TotalPacketsReceived => _bots.Values.Sum(b => b.PacketsReceived);
    public long TotalBytesSent => _bots.Values.Sum(b => (long)b.BytesSent);
    public long TotalBytesReceived => _bots.Values.Sum(b => (long)b.BytesReceived);
    public BotSpawnCity SpawnCity => _spawnCity;

    public BotEngine(ILogger logger)
    {
        _logger = logger;
    }

    /// <summary>Set spawn city for new bots.</summary>
    public void SetSpawnCity(BotSpawnCity city)
    {
        _spawnCity = city;
        _logger.LogInformation("[BOT] Spawn city set to: {City}", city);
    }

    // When set, every bot spawns within ClusterSpawnRadius tiles of the chosen
    // city's centre instead of scattered across its full box — the cluster
    // worst-case test needs the whole fleet inside one screen (mutual view) so
    // each broadcast fans out to everyone.
    private bool _clusterSpawn;
    private const int ClusterSpawnRadius = 8;
    public void SetClusterSpawn(bool enabled)
    {
        _clusterSpawn = enabled;
        if (enabled) _logger.LogInformation("[BOT] Cluster spawn enabled (radius {R})", ClusterSpawnRadius);
    }

    /// <summary>Process-independent seed for a bot's spawn spot (FNV-1a over the
    /// upper-cased account name). <see cref="string.GetHashCode()"/> is randomized per
    /// process, so it placed the same bot differently on every server start.</summary>
    public static int StableSpawnSeed(string accountName)
    {
        uint hash = 2166136261;
        foreach (char ch in accountName)
        {
            hash ^= char.ToUpperInvariant(ch);
            hash *= 16777619;
        }
        return unchecked((int)hash);
    }

    /// <summary>Get a random spawn location within a city bounding box (or, in
    /// cluster mode, packed around the city centre).</summary>
    public (short X, short Y, sbyte Z) GetRandomSpawnLocation(Random rng)
    {
        var cities = _spawnCity == BotSpawnCity.All
            ? CityBounds.Keys.ToArray()
            : [_spawnCity];

        var city = cities[rng.Next(cities.Length)];
        var b = CityBounds[city];

        if (_clusterSpawn)
        {
            int cx = (b.MinX + b.MaxX) / 2;
            int cy = (b.MinY + b.MaxY) / 2;
            short jx = (short)(cx + rng.Next(-ClusterSpawnRadius, ClusterSpawnRadius + 1));
            short jy = (short)(cy + rng.Next(-ClusterSpawnRadius, ClusterSpawnRadius + 1));
            return (jx, jy, b.Z);
        }

        short x = (short)rng.Next(b.MinX, b.MaxX + 1);
        short y = (short)rng.Next(b.MinY, b.MaxY + 1);
        return (x, y, b.Z);
    }

    /// <summary>Get all bot account names for cleanup.</summary>
    public IEnumerable<string> GetBotAccountNames()
    {
        for (int i = 1; i <= _nextBotId; i++)
            yield return $"bot{i:D4}";
    }

    /// <summary>Restart previously stopped bots.</summary>
    public async Task RestartBotsAsync(string host, int port)
    {
        if (_lastCount <= 0)
        {
            _logger.LogWarning("[BOT] No previous bot session to restart. Use .bot <count> first.");
            return;
        }
        await SpawnBotsAsync(_lastCount, _lastBehavior, host, port);
    }

    /// <summary>
    /// Spawn bots that connect to the server via TCP.
    /// </summary>
    public async Task SpawnBotsAsync(int count, BotBehavior behavior, string host = "127.0.0.1", int port = 2593)
    {
        _globalCts?.Cancel();
        _globalCts = new CancellationTokenSource();
        var ct = _globalCts.Token;

        _lastCount = count;
        _lastBehavior = behavior;

        if (!BotModeActive)
        {
            BotModeActive = true;
            OnBotModeChanged?.Invoke(true);
        }

        string cityInfo = _spawnCity == BotSpawnCity.All ? "all cities" : _spawnCity.ToString();
        _logger.LogInformation("[BOT] Starting {Count} bots with {Behavior} behavior in {City}...", 
            count, behavior, cityInfo);
        
        long startMs = Environment.TickCount64;
        int connected = 0;
        int failed = 0;

        // Spawn in batches to avoid overwhelming the server
        const int batchSize = 50;
        const int batchDelayMs = 100;

        for (int i = 0; i < count && !ct.IsCancellationRequested; i += batchSize)
        {
            int batchCount = Math.Min(batchSize, count - i);
            var tasks = new List<Task<bool>>();

            for (int j = 0; j < batchCount; j++)
            {
                int botId = Interlocked.Increment(ref _nextBotId);
                var bot = new BotClient(botId, _logger, _sessionPassword);
                bot.SetAnomalySink(Anomalies);
                _bots[botId] = bot;
                RegisterBotCharacter(bot.CharName);
                RegisterBotAccount(bot.AccountName);
                
                int localBotId = botId;
                tasks.Add(Task.Run(async () =>
                {
                    bool success = await bot.ConnectAndLoginAsync(host, port, ct);
                    if (success)
                    {
                        var roleBehavior = CreateRoleBehavior(behavior, localBotId);
                        if (roleBehavior != null)
                            bot.StartBehavior(roleBehavior, ct);
                        else
                            bot.StartBehavior(behavior, ct);
                        return true;
                    }
                    return false;
                }, ct));
            }

            var results = await Task.WhenAll(tasks);
            connected += results.Count(r => r);
            failed += results.Count(r => !r);

            // Progress log every batch
            _logger.LogInformation("[BOT] Progress: {Connected}/{Total} connected, {Failed} failed",
                connected, i + batchCount, failed);

            if (i + batchSize < count)
                await Task.Delay(batchDelayMs, ct);
        }

        long elapsedMs = Environment.TickCount64 - startMs;
        
        // Log state breakdown
        int playing = _bots.Values.Count(b => b.State == BotState.Playing);
        int connecting = _bots.Values.Count(b => b.State == BotState.Connecting);
        int loggingIn = _bots.Values.Count(b => b.State == BotState.LoggingIn);
        int disconnected = _bots.Values.Count(b => b.State == BotState.Disconnected);
        _logger.LogInformation("[BOT] State breakdown: Playing={Playing}, Connecting={Connecting}, LoggingIn={LoggingIn}, Disconnected={Disconnected}",
            playing, connecting, loggingIn, disconnected);
        
        _logger.LogInformation("[BOT] Spawn complete in {Elapsed}ms. Connected: {Connected}, Failed: {Failed}",
            elapsedMs, connected, failed);
    }

    /// <summary>
    /// Stop all bots (disconnect TCP but keep account tracking for cleanup).
    /// </summary>
    public void StopAllBots()
    {
        _logger.LogInformation("[BOT] Stopping all {Count} bots...", _bots.Count);

        _globalCts?.Cancel();

        foreach (var bot in _bots.Values)
        {
            try { bot.Dispose(); } catch { }
        }
        _bots.Clear();
        ForgetAllBotCharacters();

        if (BotModeActive)
        {
            BotModeActive = false;
            OnBotModeChanged?.Invoke(false);
        }

        _logger.LogInformation("[BOT] All bots stopped. Use .bot clean to remove characters from world.");
    }

    /// <summary>
    /// Reset bot ID counter (call after cleaning characters).
    /// </summary>
    public void ResetBotCounter()
    {
        _nextBotId = 0;
        _lastCount = 0;
        _logger.LogInformation("[BOT] Bot counter reset.");
    }

    /// <summary>
    /// Get the highest bot ID created (for cleanup range).
    /// </summary>
    public int GetMaxBotId() => _nextBotId;

    /// <summary>
    /// Get statistics for logging.
    /// </summary>
    public BotStats GetStats()
    {
        long nowMs = Environment.TickCount64;
        long elapsedMs = nowMs - _lastStatsLogMs;
        if (elapsedMs <= 0) elapsedMs = 1;

        int currentSent = TotalPacketsSent;
        int currentReceived = TotalPacketsReceived;

        float packetsPerSecIn = (currentReceived - _lastPacketsReceived) * 1000f / elapsedMs;
        float packetsPerSecOut = (currentSent - _lastPacketsSent) * 1000f / elapsedMs;

        _lastStatsLogMs = nowMs;
        _lastPacketsSent = currentSent;
        _lastPacketsReceived = currentReceived;

        return new BotStats
        {
            TotalBots = TotalBots,
            ActiveBots = ActiveBots,
            ConnectingBots = ConnectingBots,
            TotalPacketsSent = currentSent,
            TotalPacketsReceived = currentReceived,
            TotalBytesSent = TotalBytesSent,
            TotalBytesReceived = TotalBytesReceived,
            PacketsPerSecIn = packetsPerSecIn,
            PacketsPerSecOut = packetsPerSecOut
        };
    }

    /// <summary>
    /// Log current stats.
    /// </summary>
    public void LogStats()
    {
        var stats = GetStats();
        _logger.LogInformation(
            "[bot_stats] bots={Active}/{Total} pkt_in={PktIn} pkt_out={PktOut} pps_in={PpsIn:F0} pps_out={PpsOut:F0} bytes_in={BytesIn}KB bytes_out={BytesOut}KB",
            stats.ActiveBots,
            stats.TotalBots,
            stats.TotalPacketsReceived,
            stats.TotalPacketsSent,
            stats.PacketsPerSecIn,
            stats.PacketsPerSecOut,
            stats.TotalBytesReceived / 1024,
            stats.TotalBytesSent / 1024);
    }

    /// <summary>
    /// Clean up disconnected bots.
    /// </summary>
    public int CleanupDisconnected()
    {
        var toRemove = _bots.Where(kv => kv.Value.State == BotState.Disconnected).Select(kv => kv.Key).ToList();
        foreach (var id in toRemove)
        {
            if (_bots.TryRemove(id, out var bot))
                bot.Dispose();
        }
        return toRemove.Count;
    }

    public async Task RunScenarioAsync(IBotScenario scenario, string host = "127.0.0.1",
        int port = 2593, int? botCountOverride = null, int? durationOverride = null)
    {
        if (IsScenarioRunning)
        {
            _logger.LogWarning("[BOT] A scenario is already running. Stop it first.");
            return;
        }

        StopAllBots();

        _scenarioCts = new CancellationTokenSource();
        int botCount = botCountOverride ?? scenario.DefaultBotCount;
        int durationMin = durationOverride ?? scenario.DefaultDurationMinutes;

        _spawnCity = scenario.SpawnCity;
        BeginScenarioWindow(scenario, botCount);

        // As SpawnBotsAsync: bot mode exempts the loopback connections from the
        // connection-flood filter, and the bots' accounts and characters must be
        // registered before they log in or the login refuses them. Scenarios skipped
        // both, so every scenario bot was turned away at the door.
        if (!BotModeActive)
        {
            BotModeActive = true;
            OnBotModeChanged?.Invoke(true);
        }

        _logger.LogInformation("[BOT] Starting scenario '{Name}': {Count} bots, {Duration} min, {City}",
            scenario.Name, botCount, durationMin, scenario.SpawnCity);

        var distribution = scenario.GetBotDistribution(botCount);
        var ct = _scenarioCts.Token;

        foreach (var (behavior, count) in distribution)
        {
            for (int i = 0; i < count && !ct.IsCancellationRequested; i++)
            {
                int botId = Interlocked.Increment(ref _nextBotId);
                var bot = new BotClient(botId, _logger, _sessionPassword);
                bot.SetAnomalySink(Anomalies);
                _bots[botId] = bot;
                RegisterBotCharacter(bot.CharName);
                RegisterBotAccount(bot.AccountName);

                var localBehavior = behavior;
                _ = Task.Run(async () =>
                {
                    bool success = await bot.ConnectAndLoginAsync(host, port, ct);
                    if (success)
                        bot.StartBehavior(localBehavior, ct);
                }, ct);

                if (i % 50 == 49)
                    await Task.Delay(100, ct);
            }
        }

        _logger.LogInformation("[BOT] Scenario '{Name}' spawning complete. Running for {Duration} min...",
            scenario.Name, durationMin);

        if (scenario is ISoakScenario soak)
        {
            _soak = new SoakMonitor(_logger, TimeSpan.FromMinutes(soak.SaveIntervalMinutes),
                TimeSpan.FromMinutes(1), WorldAudit, RequestSave, SaveStats);
            _soak.Start(LogDirectory, ct);
            _logger.LogInformation("[soak] saving every {Save} min, auditing after each save; trend in {Csv}",
                soak.SaveIntervalMinutes, _soak.CsvPath);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(durationMin), ct);
            }
            catch (OperationCanceledException) { }
            finally
            {
                FinishScenario();
            }
        });
    }

    public void StopScenario()
    {
        if (!IsScenarioRunning)
        {
            _logger.LogWarning("[BOT] No scenario is running.");
            return;
        }
        _scenarioCts?.Cancel();
        FinishScenario();
    }

    internal void FinishScenario()
    {
        // Both the duration timer and StopScenario reach here; only one may report.
        var scenario = Interlocked.Exchange(ref _activeScenario, null);
        if (scenario == null) return;

        var elapsed = TimeSpan.FromMilliseconds(Environment.TickCount64 - _scenarioStartMs);
        var ticks = EndScenarioWindow();
        LastScenarioReport = BotScenarioReport.Generate(
            scenario.Name, this, elapsed, _scenarioBotCount, ticks, ScenarioGate);

        _logger.LogInformation("[BOT] Scenario '{Name}' finished. Duration={Duration:F1}min Gate={Status} " +
            "Active={Active}/{Total} Disconnects={Disc} Anomalies={Anom} Moves={Rejects}/{Moves} Ticks={Ticks}",
            LastScenarioReport.ScenarioName, elapsed.TotalMinutes, LastScenarioReport.Status,
            LastScenarioReport.ActiveAtEnd, LastScenarioReport.TotalBots,
            LastScenarioReport.Disconnects, LastScenarioReport.AnomalyCount,
            LastScenarioReport.TotalMoveRejects, LastScenarioReport.TotalMoveRequests,
            LastScenarioReport.TickSamples);
        _logger.LogInformation("[BOT] Steps not sent into a tile already refused: {Skipped}",
            _bots.Values.Sum(b => b.World.SkippedBlockedSteps));

        if (LastScenarioReport.FailReasons.Count > 0)
        {
            foreach (var reason in LastScenarioReport.FailReasons)
                _logger.LogWarning("[BOT] GATE {Reason}", reason);
        }
        foreach (var group in LastScenarioReport.Anomalies.GroupBy(a => a.Type).OrderByDescending(g => g.Count()))
            _logger.LogWarning("[BOT] {Count} x {Type}, e.g. {Detail}", group.Count(), group.Key, group.First().Detail);

        var soakMonitor = Interlocked.Exchange(ref _soak, null);
        if (soakMonitor != null && scenario is ISoakScenario soakScenario)
        {
            var r = LastSoakResult = soakMonitor.Finish(soakScenario.MaxHeapGrowthMbPerHour);
            var (offered, opened, closed) = TradeCounts;
            // Warning, so the verdict reaches the file log as well as the console.
            _logger.LogWarning("[soak] {Verdict}: {Min:F0} min, heap {Start:F0}->{End:F0} MB ({Slope:+0.0;-0.0} MB/h), " +
                "working set {Ws:+0.0;-0.0} MB/h, saves {Saves} ({Failed} failed), audits {Audits} ({Anomalies} anomalies), " +
                "trades {Offered} offered, windows {Opened} opened / {Closed} closed, CSV {Csv}",
                r.Passed ? "PASSED" : "FAILED", r.DurationMinutes, r.HeapStartMb, r.HeapEndMb, r.HeapGrowthMbPerHour,
                r.WorkingSetGrowthMbPerHour, r.Saves, r.SaveFailures, r.Audits, r.TotalAnomalies,
                offered, opened, closed, r.CsvPath);
            _logger.LogWarning("[soak] trade turns without an offer: {Skips}", TradeSkipSummary);
            _logger.LogWarning("[soak] offers that opened no window / windows left open: {Outcomes}", TradeOutcomeSummary);
            _logger.LogWarning("[soak] incoming stream: {Stream}", StreamSummary);
            var playing = _bots.Values.Where(b => b.State == BotState.Playing).ToList();
            _logger.LogWarning("[soak] bots knowing their backpack {Packs}/{Bots}, worn items seen {Worn}",
                playing.Count(b => b.World.BackpackSerial != 0), playing.Count,
                playing.Sum(b => b.World.Equipment.Count));
            foreach (var reason in r.FailReasons)
                _logger.LogWarning("[soak] GATE {Reason}", reason);
        }
    }

    private static IBotBehavior? CreateRoleBehavior(BotBehavior behavior, int seed)
    {
        return behavior switch
        {
            BotBehavior.Walker => new WalkerBot(seed),
            BotBehavior.CombatRole => new Behaviors.CombatBot(seed),
            BotBehavior.Vendor => new VendorBot(seed),
            BotBehavior.Loot => new LootBot(seed),
            BotBehavior.Skill => new SkillBot(seed),
            BotBehavior.Social => new SocialBot(seed),
            BotBehavior.Chaos => new ChaosBot(seed),
            BotBehavior.Cluster => new Behaviors.ClusterBot(seed),
            BotBehavior.Mage => new Behaviors.MageBot(seed),
            _ => null,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAllBots();
        _globalCts?.Dispose();
    }
}

public struct BotStats
{
    public int TotalBots;
    public int ActiveBots;
    public int ConnectingBots;
    public int TotalPacketsSent;
    public int TotalPacketsReceived;
    public long TotalBytesSent;
    public long TotalBytesReceived;
    public float PacketsPerSecIn;
    public float PacketsPerSecOut;
}

public enum BotSpawnCity
{
    All,
    Britain,
    Trinsic,
    Moonglow,
    Yew,
    Minoc,
    Vesper,
    Skara,
    Jhelom
}
