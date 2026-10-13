namespace SphereNet.Core.Configuration;

/// <summary>
/// Per-connection database settings. Loaded from [MYSQL name], [SQLITE name] and
/// [PGSQL name] INI sections or synthesized from legacy [SPHERE] MySQL* keys.
/// </summary>
public sealed class DbConnectionConfig
{
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "MySqlConnector";
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 3306;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Database { get; set; } = "";

    /// <summary>Keep the connection alive between queries (persistent).</summary>
    public bool KeepAlive { get; set; }

    /// <summary>Connection timeout in seconds.</summary>
    public int ConnectTimeout { get; set; } = 30;

    /// <summary>Read (command) timeout in seconds.</summary>
    public int ReadTimeout { get; set; } = 30;

    /// <summary>Write timeout in seconds (provider-dependent).</summary>
    public int WriteTimeout { get; set; } = 30;

    /// <summary>Run queries on a dedicated background thread.</summary>
    public bool UseThread { get; set; }

    /// <summary>Auto-connect on server startup.</summary>
    public bool AutoConnect { get; set; }

    /// <summary>Session character set sent as <c>SET NAMES</c> right after the
    /// connection opens (MySQL only). Empty keeps the provider's utf8mb4.
    ///
    /// MySqlConnector always talks utf8mb4 and ignores a CharSet connection-string
    /// option, so the server converts every stored value from its column's charset.
    /// Source-X passes its client library's default and hands the raw bytes to the
    /// script untouched, and classic shards' tables were written through latin1
    /// sessions: UTF-8 bytes stored in latin1 columns. Read over utf8mb4 those
    /// come back double-encoded ("ş" -> "ÅŸ"); <c>latin1</c> here makes the
    /// server pass the bytes through again, as upstream sees them.</summary>
    public string CharSet { get; set; } = "";

    /// <summary>A charset name safe to splice into <c>SET NAMES</c>: letters,
    /// digits and underscores only (latin1, utf8mb4, cp1250...).</summary>
    public static bool IsValidCharSetName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 32)
            return false;
        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }
        return true;
    }

    /// <summary>The ADO.NET invariant name a [PGSQL name] section connects with.</summary>
    public const string PostgreSqlProvider = "Npgsql";

    /// <summary>PostgreSQL's default port, the one a [PGSQL name] section starts from.</summary>
    public const int PostgreSqlDefaultPort = 5432;

    /// <summary>True if this is a SQLite provider.</summary>
    public bool IsSqlite => Provider.Contains("Sqlite", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>True if this is a PostgreSQL provider.</summary>
    public bool IsPostgreSql => IsPostgreSqlProvider(Provider);

    public static bool IsPostgreSqlProvider(string? provider) =>
        provider != null &&
        (provider.Contains("Npgsql", System.StringComparison.OrdinalIgnoreCase) ||
         provider.Contains("Postgre", System.StringComparison.OrdinalIgnoreCase));

    public string BuildConnectionString()
    {
        if (IsSqlite)
            return BuildSqliteConnectionString();
        if (IsPostgreSql)
            return BuildPostgreSqlConnectionString();

        return BuildMySqlConnectionString();
    }

    /// <summary>Npgsql keywords. Values go through the connection-string builder, which
    /// quotes a password or name holding ';' or '=' instead of letting it split the
    /// string. Npgsql caps Timeout at 1024 s.</summary>
    private string BuildPostgreSqlConnectionString()
    {
        var b = new System.Data.Common.DbConnectionStringBuilder
        {
            ["Host"] = Host,
            ["Port"] = Port,
            ["Username"] = User,
            ["Password"] = Password,
            ["Database"] = Database,
            ["Timeout"] = System.Math.Clamp(ConnectTimeout, 0, 1024),
            ["Command Timeout"] = System.Math.Max(0, ReadTimeout),
        };
        if (KeepAlive)
            b["Keepalive"] = 60;
        return b.ConnectionString;
    }

    private string BuildMySqlConnectionString()
    {
        var parts = new System.Text.StringBuilder(256);
        parts.Append($"Server={Host};");
        if (Port != 3306)
            parts.Append($"Port={Port};");
        parts.Append($"User ID={User};Password={Password};Database={Database};");
        parts.Append($"Connection Timeout={ConnectTimeout};");
        parts.Append($"Default Command Timeout={ReadTimeout};");
        if (KeepAlive)
            parts.Append("Keepalive=60;");
        return parts.ToString();
    }

    private string BuildSqliteConnectionString()
    {
        var parts = new System.Text.StringBuilder(128);
        parts.Append($"Data Source={Database};");
        if (!string.IsNullOrWhiteSpace(Password))
            parts.Append($"Password={Password};");
        return parts.ToString();
    }
}
