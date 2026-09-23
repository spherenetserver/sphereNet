namespace SphereNet.Panel;

/// <summary>
/// Bridge between the game server (Program.cs) and the panel web host.
/// Program.cs fills in all delegates; Panel code only calls them.
/// </summary>
public sealed class PanelContext
{
    // Server identity
    public string ServerName { get; set; } = "SphereNet";
    public DateTime StartTime { get; set; } = DateTime.UtcNow;
    public string AdminPassword { get; set; } = "";

    // Paths — set by Program.cs at startup
    public string? IniPath { get; set; }
    public string? ScriptsPath { get; set; }

    // Live stats — called on each request / stats push
    public Func<ServerStats>? GetStats { get; set; }

    // Online players snapshot
    public Func<IReadOnlyList<PlayerInfo>>? GetOnlinePlayers { get; set; }

    // Account CRUD
    public Func<IReadOnlyList<AccountInfo>>? GetAllAccounts { get; set; }
    public Func<string, AccountInfo?>? GetAccount { get; set; }
    public Func<string, string, bool>? CreateAccount { get; set; }
    public Func<string, bool>? DeleteAccount { get; set; }
    public Func<string, bool, bool>? SetAccountBanned { get; set; }
    public Func<string, string, bool>? SetAccountPassword { get; set; }
    public Func<string, int, bool>? SetAccountPrivLevel { get; set; }

    // Online player actions (by character serial)
    public Func<uint, bool>? DisconnectPlayer { get; set; }
    public Func<uint, string, bool>? MessagePlayer { get; set; }

    // IP block list (runtime; the server keeps it in memory)
    public Func<IReadOnlyList<string>>? GetIpBlocks { get; set; }
    public Func<string, bool>? AddIpBlock { get; set; }
    public Func<string, bool>? RemoveIpBlock { get; set; }

    // Server commands
    public Func<bool>? OnSave { get; set; }
    public Func<bool>? OnShutdown { get; set; }
    public Func<bool>? OnResync { get; set; }
    public Func<bool>? OnGc { get; set; }
    public Func<bool>? OnRespawn { get; set; }
    public Func<bool>? OnRestock { get; set; }
    public Func<string, bool>? OnBroadcast { get; set; }
    public Func<bool>? OnRestart { get; set; }
    public Func<bool>? StartServer { get; set; }

    // Raw command — returns response lines
    public Func<string, string[]>? ExecuteCommand { get; set; }
    public Action<string>? AuditLog { get; set; }

    // Server lifecycle state
    public Func<bool>? IsServerRunning { get; set; }

    // Debug toggles
    public Func<DebugState>? GetDebugState { get; set; }
    public Func<bool, bool>? SetPacketDebug { get; set; }
    public Func<bool, bool>? SetScriptDebug { get; set; }

    // Dialog designer bridge — gump art arrives pre-encoded as PNG so the
    // panel stays free of imaging/MUL dependencies. Null = endpoints 404.
    public Func<int, byte[]?>? GetGumpPng { get; set; }
    public Func<IReadOnlyList<string>>? ListDialogNames { get; set; }
    public Func<string, string?>? GetDialogSource { get; set; }

    // App update — read from sphere.ini by the Host. Null = /api/update/* 404s
    // (e.g. a build with no updater configured).
    public Updates.UpdateSettings? UpdateSettings { get; set; }

    /// <summary>
    /// Stops the game server and exits the Host process. Only the Host can
    /// supply this: applying an update replaces SphereNet.Host.exe, which the
    /// running Host holds a file lock on, so the process must exit before the
    /// swap and be relaunched by the external updater afterwards.
    /// Null in standalone mode — /api/update/apply then reports CanApply=false.
    /// </summary>
    public Func<bool>? OnHostExit { get; set; }

    /// <summary>How long the Host waits for the server's shutdown save
    /// (HostShutdownTimeoutMs). The update script waits longer than this for the
    /// Host to exit, so it never kills a Host that is still waiting on a save.</summary>
    public int HostShutdownTimeoutMs { get; set; } = 180_000;
}

// ---------------------------------------------------------------------------
// DTOs
// ---------------------------------------------------------------------------

public record ServerStats(
    string ServerName,
    string Uptime,
    int UptimeSeconds,
    int OnlinePlayers,
    int TotalChars,
    int TotalItems,
    int TotalSectors,
    long TickCount,
    long MemoryMB,
    int Accounts,
    double CpuPercent = 0,
    int ThreadCount = 0,
    double AvgTickMs = 0,
    double MaxTickMs = 0,
    double P50TickMs = 0,
    double P95TickMs = 0,
    double P99TickMs = 0,
    bool MulticoreEnabled = false,
    IReadOnlyList<MapStats>? Maps = null,
    DateTime? LastSaveUtc = null,
    double LastSaveSeconds = 0,
    bool SaveInProgress = false,
    bool? LastSaveOk = null,
    int SaveCount = 0
);

public record MapStats(
    int MapId,
    int Chars,
    int Items,
    int Sectors,
    int ActiveSectors,
    int OnlinePlayers
);

public record PlayerInfo(
    string CharName,
    string AccountName,
    int MapId,
    int X,
    int Y,
    string Ip,
    uint Serial = 0,
    int PrivLevel = 0,
    string ClientVersion = "",
    int SessionSeconds = 0
);

public record AccountInfo(
    string Name,
    int PrivLevel,
    bool IsBanned,
    string LastIp,
    DateTime LastLogin,
    DateTime CreateDate,
    int CharCount
);

public record LogEntry(
    DateTime Timestamp,
    string Level,
    string Message,
    string Source
);

public record DebugState(bool PacketDebug, bool ScriptDebug);

public record ScriptFileInfo(
    string Name,
    string RelativePath,
    long SizeBytes,
    DateTime LastModified
);

public record SetupConfig(
    string ServerName,
    int ServPort,
    string AdminPassword,
    int AdminPanelPort,
    // Advanced
    int TickSleepMode = 2,
    bool DebugPackets = false,
    bool ScriptDebug = false
);
