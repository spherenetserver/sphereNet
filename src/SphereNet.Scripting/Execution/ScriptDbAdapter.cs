using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Security;

namespace SphereNet.Scripting.Execution;

/// <summary>
/// Runtime DB bridge for script db.* verbs.
/// Supports multiple named connections with per-connection settings.
/// Each connection maintains its own rowset context for db.row access.
/// </summary>
public sealed class ScriptDbAdapter : IDisposable
{
    /// <summary>Invoked after every session connection opens. The host wires
    /// provider-specific legacy compatibility here (the server enables sqlite
    /// DQS so classic packs' double-quoted SQL string literals keep parsing,
    /// as Source-X's bundled sqlite does).</summary>
    public static Action<System.Data.Common.DbConnection>? OnConnectionOpened;

    private readonly ILogger<ScriptDbAdapter> _logger;
    private readonly ConcurrentDictionary<string, DbSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private string _activeSessionName = "default";

    public ScriptDbAdapter(ILogger<ScriptDbAdapter> logger)
    {
        _logger = logger;
    }

    /// <summary>Register a connection configuration. Does not connect yet.</summary>
    public void RegisterConnection(DbConnectionConfig config)
    {
        var session = new DbSession(config, _logger);
        _sessions[config.Name] = session;
        _logger.LogInformation("Registered DB connection '{Name}' -> {Host}/{Db}",
            config.Name, config.Host, config.Database);
    }

    /// <summary>Name of the currently active connection for DB.QUERY/EXECUTE/ROW.</summary>
    public string ActiveSessionName
    {
        get => _activeSessionName;
        set => _activeSessionName = value;
    }

    /// <summary>True if the active session is connected.</summary>
    public bool IsConnected => GetActiveSession()?.IsConnected ?? false;

    /// <summary>Check if a specific named connection is connected.</summary>
    public bool IsConnected_Named(string name)
    {
        return _sessions.TryGetValue(name, out var s) && s.IsConnected;
    }

    /// <summary>Get the list of registered connection names.</summary>
    public IEnumerable<string> ConnectionNames => _sessions.Keys;

    /// <summary>Connect the active session using its registered config.</summary>
    public bool Connect(out string error)
    {
        return Connect(_activeSessionName, out error);
    }

    /// <summary>Connect a named session using its registered config.</summary>
    public bool Connect(string name, out string error)
    {
        error = "";
        if (!_sessions.TryGetValue(name, out var session))
        {
            error = $"No DB connection registered with name '{name}'.";
            return false;
        }
        return session.Connect(out error);
    }

    /// <summary>Connect with explicit provider/connection string (legacy).</summary>
    public bool Connect(string providerInvariantName, string connectionString, out string error)
    {
        error = "";
        var session = GetOrCreateActiveSession();
        return session.Connect(providerInvariantName, connectionString, out error);
    }

    /// <summary>Connect a SQLite file directly (LDB.CONNECT &lt;filename&gt; style).</summary>
    public bool ConnectFile(string fileName, out string error)
    {
        error = "";
        string resolvedFileName = ResolveSafeDatabasePath(fileName, AppContext.BaseDirectory, out error);
        if (resolvedFileName.Length == 0)
            return false;

        var cfg = new DbConnectionConfig
        {
            Name = "default",
            Provider = "Microsoft.Data.Sqlite",
            Database = resolvedFileName
        };
        var session = GetOrCreateActiveSession();
        session.UpdateConfig(cfg);
        return session.Connect("Microsoft.Data.Sqlite", $"Data Source={resolvedFileName};", out error);
    }

    /// <summary>Connect a SQLite file under a trusted script/save root.</summary>
    public bool ConnectFile(string fileName, string basePath, out string error)
    {
        error = "";
        string resolvedFileName = ResolveSafeDatabasePath(fileName, basePath, out error);
        if (resolvedFileName.Length == 0)
            return false;

        var cfg = new DbConnectionConfig
        {
            Name = "default",
            Provider = "Microsoft.Data.Sqlite",
            Database = resolvedFileName
        };
        var session = GetOrCreateActiveSession();
        session.UpdateConfig(cfg);
        return session.Connect("Microsoft.Data.Sqlite", $"Data Source={resolvedFileName};", out error);
    }

    private static string ResolveSafeDatabasePath(string fileName, string basePath, out string error)
    {
        try
        {
            string normalized = fileName.Trim().Trim('"');
            if (!SafePath.TryResolveUnderRoot(basePath, normalized, out string full, out string? pathError))
            {
                error = pathError ?? "Database path is outside the allowed script root.";
                return "";
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full) ?? Path.GetFullPath(basePath));
            error = "";
            return full;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return "";
        }
    }

    /// <summary>Connect the default session (backward compat).</summary>
    public bool ConnectDefault(out string error)
    {
        return Connect("default", out error);
    }

    /// <summary>Escape a string for safe SQL use (MySQL-specific, returns input as-is for non-MySQL).</summary>
    public string EscapeData(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";
        return input
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\"", "\\\"")
            .Replace("\0", "\\0")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\x1a", "\\Z");
    }

    /// <summary>Close the active session.</summary>
    public void Close()
    {
        Close(_activeSessionName);
    }

    /// <summary>Close a named session.</summary>
    public void Close(string name)
    {
        if (_sessions.TryGetValue(name, out var session))
            session.Close();
    }

    /// <summary>Close all sessions.</summary>
    public void CloseAll()
    {
        foreach (var session in _sessions.Values)
            session.Close();
    }

    /// <summary>Switch the active connection to the named one.</summary>
    public bool Select(string name, out string error)
    {
        error = "";
        if (!_sessions.ContainsKey(name))
        {
            error = $"No DB connection registered with name '{name}'.";
            return false;
        }
        _activeSessionName = name;
        return true;
    }

    /// <summary>Execute a non-query SQL on the active session.</summary>
    public bool Execute(string sql, out int affectedRows, out string error)
    {
        return Execute(_activeSessionName, sql, out affectedRows, out error);
    }

    /// <summary>Execute a non-query SQL on a named session.</summary>
    public bool Execute(string name, string sql, out int affectedRows, out string error)
    {
        affectedRows = 0;
        error = "";
        if (!_sessions.TryGetValue(name, out var session))
        {
            error = $"No DB connection registered with name '{name}'.";
            return false;
        }
        return session.Execute(sql, out affectedRows, out error);
    }

    /// <summary>Execute a query on the active session.</summary>
    public bool Query(string sql, out int rowCount, out string error)
    {
        return Query(_activeSessionName, sql, out rowCount, out error);
    }

    /// <summary>Fire-and-forget query on the active session (Source-X DBO AQUERY):
    /// enqueues onto the DB worker thread when one is running, else runs it inline.
    /// The rowset lands whenever the worker finishes.</summary>
    public bool QueryAsync(string sql) => GetActiveSession()?.EnqueueQuery(sql) ?? false;

    /// <summary>Fire-and-forget non-query on the active session (Source-X DBO
    /// AEXECUTE).</summary>
    public bool ExecuteAsync(string sql) => GetActiveSession()?.EnqueueExecute(sql) ?? false;

    /// <summary>Column count of the active session's last result set (Source-X
    /// DBO NUMCOLS).</summary>
    public int NumCols => GetActiveSession()?.NumCols ?? 0;

    /// <summary>Execute a query on a named session.</summary>
    public bool Query(string name, string sql, out int rowCount, out string error)
    {
        rowCount = 0;
        error = "";
        if (!_sessions.TryGetValue(name, out var session))
        {
            error = $"No DB connection registered with name '{name}'.";
            return false;
        }
        return session.Query(sql, out rowCount, out error);
    }

    /// <summary>Resolve db.row.* variables from the active session's last query result.</summary>
    public bool TryResolveRowValue(string key, out string value)
    {
        return TryResolveRowValue(_activeSessionName, key, out value);
    }

    /// <summary>Resolve db.row.* variables from a named session's last query result.</summary>
    public bool TryResolveRowValue(string name, string key, out string value)
    {
        value = "";
        if (!_sessions.TryGetValue(name, out var session))
            return false;
        return session.TryResolveRowValue(key, out value);
    }

    // Legacy compat properties for default session
    public string DefaultProvider
    {
        get => _sessions.TryGetValue("default", out var s) ? s.Config?.Provider ?? "" : "";
        set
        {
            var session = GetOrCreateDefaultSession();
            if (session.Config != null) session.Config.Provider = value;
        }
    }

    public string DefaultConnectionString
    {
        get => _sessions.TryGetValue("default", out var s) ? s.Config?.BuildConnectionString() ?? "" : "";
        set
        {
            // Legacy: store raw connection string
            var session = GetOrCreateDefaultSession();
            session.LegacyConnectionString = value;
        }
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Close();
        _sessions.Clear();
    }

    private DbSession? GetActiveSession()
    {
        _sessions.TryGetValue(_activeSessionName, out var session);
        return session;
    }

    private DbSession GetOrCreateActiveSession()
    {
        if (_sessions.TryGetValue(_activeSessionName, out var session))
            return session;
        session = new DbSession(new DbConnectionConfig { Name = _activeSessionName }, _logger);
        _sessions[_activeSessionName] = session;
        return session;
    }

    private DbSession GetOrCreateDefaultSession()
    {
        if (_sessions.TryGetValue("default", out var session))
            return session;
        session = new DbSession(new DbConnectionConfig { Name = "default" }, _logger);
        _sessions["default"] = session;
        return session;
    }

    /// <summary>
    /// Per-connection session. Holds its own DbConnection, lock, and rowset.
    /// </summary>
    private sealed class DbSession
    {
        private readonly object _sync = new();
        private readonly ILogger _logger;
        private DbConnection? _connection;
        private DataTable? _rowTable;
        private Thread? _workerThread;
        private readonly BlockingCollection<Action>? _workQueue;

        public DbConnectionConfig? Config { get; private set; }
        public string? LegacyConnectionString { get; set; }

        public void UpdateConfig(DbConnectionConfig config) => Config = config;

        public bool IsConnected
        {
            get
            {
                lock (_sync)
                {
                    return _connection != null && _connection.State == ConnectionState.Open;
                }
            }
        }

        /// <summary>Column count of the last query's result set (0 when none).</summary>
        public int NumCols
        {
            get { lock (_sync) { return _rowTable?.Columns.Count ?? 0; } }
        }

        /// <summary>Queue a query on the worker thread without blocking; runs inline
        /// when no worker is available.</summary>
        public bool EnqueueQuery(string sql)
        {
            if (_workQueue != null && _workerThread != null)
            {
                _workQueue.Add(() => QueryInternal(sql, out _, out _));
                return true;
            }
            return QueryInternal(sql, out _, out _);
        }

        /// <summary>Queue a non-query on the worker thread without blocking; runs
        /// inline when no worker is available.</summary>
        public bool EnqueueExecute(string sql)
        {
            if (_workQueue != null && _workerThread != null)
            {
                _workQueue.Add(() => ExecuteInternal(sql, out _, out _));
                return true;
            }
            return ExecuteInternal(sql, out _, out _);
        }

        public DbSession(DbConnectionConfig config, ILogger logger)
        {
            Config = config;
            _logger = logger;
            if (config.UseThread)
                _workQueue = new BlockingCollection<Action>();
        }

        public bool Connect(out string error)
        {
            error = "";
            if (Config == null)
            {
                error = "No configuration available.";
                return false;
            }

            string provider = Config.Provider;
            string connStr = LegacyConnectionString ?? Config.BuildConnectionString();

            if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(connStr))
            {
                error = "DB provider or connection string is not configured.";
                return false;
            }

            return Connect(provider, connStr, out error);
        }

        public bool Connect(string providerInvariantName, string connectionString, out string error)
        {
            error = "";
            lock (_sync)
            {
                try
                {
                    CloseInternal();

                    var factory = DbProviderFactories.GetFactory(providerInvariantName);
                    var connection = factory.CreateConnection();
                    if (connection == null)
                    {
                        error = $"Provider '{providerInvariantName}' did not create a connection instance.";
                        return false;
                    }

                    connection.ConnectionString = connectionString;
                    connection.Open();
                    // Host hook — the server enables provider-specific legacy
                    // compatibility here (sqlite DQS for classic double-quoted
                    // string literals in pack scripts).
                    ScriptDbAdapter.OnConnectionOpened?.Invoke(connection);
                    _connection = connection;
                    _logger.LogInformation("DB session '{Name}' connected with provider {Provider}",
                        Config?.Name ?? "?", providerInvariantName);

                    if (Config?.UseThread == true && _workerThread == null)
                        StartWorkerThread();

                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _logger.LogWarning(ex, "DB session '{Name}' connect failed", Config?.Name ?? "?");
                    return false;
                }
            }
        }

        public void Close()
        {
            lock (_sync)
            {
                CloseInternal();
            }
            StopWorkerThread();
        }

        public bool Execute(string sql, out int affectedRows, out string error)
        {
            affectedRows = 0;
            error = "";

            if (Config?.UseThread == true && _workQueue != null)
            {
                int result = 0;
                string? err = null;
                bool ok = false;
                using var done = new ManualResetEventSlim(false);
                _workQueue.Add(() =>
                {
                    ok = ExecuteInternal(sql, out result, out err);
                    done.Set();
                });
                done.Wait(TimeSpan.FromSeconds(Config.ReadTimeout > 0 ? Config.ReadTimeout : 30));
                affectedRows = result;
                error = err ?? "";
                return ok;
            }

            return ExecuteInternal(sql, out affectedRows, out error);
        }

        public bool Query(string sql, out int rowCount, out string error)
        {
            rowCount = 0;
            error = "";

            if (Config?.UseThread == true && _workQueue != null)
            {
                int result = 0;
                string? err = null;
                bool ok = false;
                using var done = new ManualResetEventSlim(false);
                _workQueue.Add(() =>
                {
                    ok = QueryInternal(sql, out result, out err);
                    done.Set();
                });
                done.Wait(TimeSpan.FromSeconds(Config.ReadTimeout > 0 ? Config.ReadTimeout : 30));
                rowCount = result;
                error = err ?? "";
                return ok;
            }

            return QueryInternal(sql, out rowCount, out error);
        }

        public bool TryResolveRowValue(string key, out string value)
        {
            value = "";
            lock (_sync)
            {
                if (_rowTable == null)
                    return false;

                if (key.Equals("db.row.numrows", StringComparison.OrdinalIgnoreCase))
                {
                    value = _rowTable.Rows.Count.ToString();
                    return true;
                }

                if (key.Equals("db.row.numcols", StringComparison.OrdinalIgnoreCase))
                {
                    value = _rowTable.Columns.Count.ToString();
                    return true;
                }

                if (!key.StartsWith("db.row.", StringComparison.OrdinalIgnoreCase))
                    return false;

                string[] parts = key.Split('.', 4, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 4)
                    return false;

                if (!int.TryParse(parts[2], out int rowIndex))
                    return false;
                if (rowIndex < 0 || rowIndex >= _rowTable.Rows.Count)
                    return false;

                string colKey = parts[3];
                object? cell = null;
                if (int.TryParse(colKey, out int colIndex))
                {
                    if (colIndex < 0 || colIndex >= _rowTable.Columns.Count)
                        return false;
                    cell = _rowTable.Rows[rowIndex][colIndex];
                }
                else if (_rowTable.Columns.Contains(colKey))
                {
                    cell = _rowTable.Rows[rowIndex][colKey];
                }
                else
                {
                    return false;
                }

                value = cell?.ToString() ?? "";
                return true;
            }
        }

        private bool ExecuteInternal(string sql, out int affectedRows, out string error)
        {
            affectedRows = 0;
            error = "";
            lock (_sync)
            {
                if (!EnsureConnection(out error))
                    return false;

                try
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = sql;
                    if (Config?.ReadTimeout > 0)
                        cmd.CommandTimeout = Config.ReadTimeout;
                    affectedRows = cmd.ExecuteNonQuery();
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _logger.LogWarning(ex, "DB session '{Name}' execute failed", Config?.Name ?? "?");
                    return false;
                }
            }
        }

        private bool QueryInternal(string sql, out int rowCount, out string error)
        {
            rowCount = 0;
            error = "";
            lock (_sync)
            {
                if (!EnsureConnection(out error))
                    return false;

                try
                {
                    using var cmd = _connection!.CreateCommand();
                    cmd.CommandText = sql;
                    if (Config?.ReadTimeout > 0)
                        cmd.CommandTimeout = Config.ReadTimeout;
                    using var reader = cmd.ExecuteReader();
                    var table = new DataTable();
                    table.Load(reader);
                    _rowTable = table;
                    rowCount = table.Rows.Count;
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    _logger.LogWarning(ex, "DB session '{Name}' query failed", Config?.Name ?? "?");
                    return false;
                }
            }
        }

        private bool EnsureConnection(out string error)
        {
            error = "";
            if (_connection == null || _connection.State != ConnectionState.Open)
            {
                if (Config?.KeepAlive == true && Config.Host.Length > 0)
                {
                    return Connect(out error);
                }
                error = "DB is not connected.";
                return false;
            }
            return true;
        }

        private void CloseInternal()
        {
            _rowTable = null;
            if (_connection == null) return;

            try
            {
                _connection.Close();
                _connection.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "DB session '{Name}' close failed", Config?.Name ?? "?");
            }
            _connection = null;
        }

        private void StartWorkerThread()
        {
            if (_workQueue == null) return;
            _workerThread = new Thread(() =>
            {
                foreach (var action in _workQueue.GetConsumingEnumerable())
                {
                    try { action(); }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "DB worker thread error for '{Name}'", Config?.Name ?? "?");
                    }
                }
            })
            {
                IsBackground = true,
                Name = $"DB-Worker-{Config?.Name ?? "?"}"
            };
            _workerThread.Start();
        }

        private void StopWorkerThread()
        {
            _workQueue?.CompleteAdding();
            _workerThread?.Join(TimeSpan.FromSeconds(5));
            _workerThread = null;
        }
    }
}
