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

    // Character actions and detail by serial (online or offline; the whole world
    // is in memory). An action answers with its outcome and the lines it produced;
    // detail is null for an unknown or deleted character.
    public Func<uint, PlayerActionRequest, PlayerActionResult>? PlayerAction { get; set; }
    public Func<uint, PlayerDetail?>? GetPlayerDetail { get; set; }

    // Staff-only message: sent to online clients at Counsel level or above.
    // Returns how many received it.
    public Func<string, int>? StaffMessage { get; set; }

    // Server-level script call: a [FUNCTION] or a SERV verb line run with the
    // server as its object and the panel as its source. Returns output lines.
    public Func<string, string, string[]>? ExecuteServerFunction { get; set; }

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

    // Character paperdoll by serial (online or offline: the whole world is in
    // memory). Info = name/title/equipment; PNG = the rendered paperdoll picture,
    // second argument true draws the paperdoll background frame. Null = unknown
    // or deleted character (PNG also null for a body without a paperdoll).
    public Func<uint, PaperdollInfo?>? GetPaperdoll { get; set; }
    public Func<uint, bool, byte[]?>? GetPaperdollPng { get; set; }

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

/// <summary>A character's paperdoll data. <see cref="PaperdollText"/> is the exact
/// name line the game client shows (0x88 OpenPaperdoll); the fields after
/// <see cref="Equipment"/> are its parts, so a page can style them separately:
/// <see cref="NotoTitle"/> the karma/fame rank (or Murderer / Criminal),
/// <see cref="FameTitle"/> Lord / Lady / a staff title (or TAG.NAME.PREFIX),
/// <see cref="FullName"/> rank + fame title + name + suffix, <see cref="GuildAbbrev"/>
/// and <see cref="GuildTitle"/> when the member shows the abbreviation, and
/// <see cref="TradeTitle"/> the part after the comma.</summary>
public record PaperdollInfo(
    uint Serial,
    string Name,
    string Title,
    string PaperdollText,
    int Body,
    bool IsFemale,
    bool IsPlayer,
    int PrivLevel,
    string AccountName,
    bool Online,
    bool HasPaperdoll,
    IReadOnlyList<PaperdollItemInfo> Equipment,
    string NotoTitle = "",
    string FameTitle = "",
    string NameSuffix = "",
    string FullName = "",
    string GuildAbbrev = "",
    string GuildTitle = "",
    string TradeTitle = ""
);

public record PaperdollItemInfo(
    int Layer,
    uint Serial,
    int DispId,
    int Hue,
    string Name
);

/// <summary>One panel action on a character. <see cref="Action"/> is one of
/// <see cref="PlayerActions.All"/>; the other fields are read by the actions that
/// take them (text: say/emote/message/verb, hue: message, x/y/z/map: teleport,
/// minutes: jail).</summary>
public record PlayerActionRequest(
    string Action,
    string? Text = null,
    int? Hue = null,
    int? X = null,
    int? Y = null,
    int? Z = null,
    int? Map = null,
    int? Minutes = null
);

/// <summary>What an action did. <see cref="NotFound"/> = no such character.</summary>
public record PlayerActionResult(
    bool Ok,
    IReadOnlyList<string> Lines,
    bool NotFound = false
)
{
    public static PlayerActionResult Missing(uint serial) =>
        new(false, [$"No character with serial 0x{serial:X8}."], NotFound: true);
    public static PlayerActionResult Fail(string line) => new(false, [line]);
    public static PlayerActionResult Done(params string[] lines) => new(true, lines);
}

/// <summary>The action names the panel accepts.</summary>
public static class PlayerActions
{
    public const string Say = "say";
    public const string Emote = "emote";
    public const string Message = "message";
    public const string Verb = "verb";
    public const string Heal = "heal";
    public const string Resurrect = "resurrect";
    public const string Kill = "kill";
    public const string Freeze = "freeze";
    public const string Unfreeze = "unfreeze";
    public const string Hide = "hide";
    public const string Unhide = "unhide";
    public const string Teleport = "teleport";
    public const string Jail = "jail";
    public const string Unjail = "unjail";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Say, Emote, Message, Verb, Heal, Resurrect, Kill, Freeze, Unfreeze,
        Hide, Unhide, Teleport, Jail, Unjail,
    };

    /// <summary>Actions that need a line of text.</summary>
    public static bool NeedsText(string action) =>
        action is Say or Emote or Message or Verb;
}

/// <summary>A character's state for the panel's detail view. Skill values are in
/// tenths (1000 = 100.0); only non-zero skills are listed. Tags are capped at
/// <see cref="MaxTags"/> entries with long values cut short.</summary>
public record PlayerDetail(
    uint Serial,
    string Name,
    string Title,
    string AccountName,
    int PrivLevel,
    bool Online,
    bool IsPlayer,
    int Body,
    int MapId,
    int X,
    int Y,
    int Z,
    int Str,
    int Dex,
    int Int,
    int Hits,
    int MaxHits,
    int Mana,
    int MaxMana,
    int Stam,
    int MaxStam,
    int Fame,
    int Karma,
    int Kills,
    int Notoriety,
    string NotorietyName,
    bool Dead,
    bool Frozen,
    bool Hidden,
    bool Poisoned,
    bool Jailed,
    IReadOnlyList<SkillValueInfo> Skills,
    IReadOnlyList<TagInfo> Tags,
    bool TagsTruncated = false
)
{
    public const int MaxTags = 100;
    public const int MaxTagValueLength = 200;
}

public record SkillValueInfo(int Id, string Name, int Value);

public record TagInfo(string Key, string Value);

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
