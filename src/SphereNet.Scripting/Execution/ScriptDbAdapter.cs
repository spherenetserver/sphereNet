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

    /// <summary>Threaded work waiting on the active session.</summary>
    public int PendingWorkCount => GetActiveSession()?.PendingWorkCount ?? 0;

    /// <summary>Work refused because the queue was at its cap.</summary>
    public long RejectedWorkCount => GetActiveSession()?.RejectedWorkCount ?? 0;

    /// <summary>Calls that gave up waiting; the statement may still have run.</summary>
    public long TimedOutWorkCount => GetActiveSession()?.TimedOutWorkCount ?? 0;

    /// <summary>Of those, the ones still waiting behind other work (a starved
    /// worker) rather than running against the database (a slow one).</summary>
    public long TimedOutQueuedCount => GetActiveSession()?.TimedOutQueuedCount ?? 0;

    /// <summary>Of those, the ones whose statement had already started.</summary>
    public long TimedOutRunningCount => GetActiveSession()?.TimedOutRunningCount ?? 0;

    /// <summary>Exceptions that escaped a job on the worker thread.</summary>
    public long WorkerFaultCount => GetActiveSession()?.WorkerFaultCount ?? 0;

    /// <summary>How much threaded work the active session may hold.</summary>
    public int MaxPendingWork
    {
        get => GetActiveSession()?.MaxPendingWork ?? 0;
        set { var s = GetActiveSession(); if (s != null) s.MaxPendingWork = value; }
    }

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
        /// <summary>The worker's inbox while UseThread is set. NOT readonly: closing a
        /// session calls CompleteAdding, which is permanent by design, so reopening one
        /// has to hand the new worker a NEW queue. It used to start a fresh worker on
        /// the completed queue instead - connect reported success and the next query
        /// died with "the collection has been marked as complete with regards to
        /// additions", which a script cannot tell apart from a database problem
        /// (review finding B9).</summary>
        private BlockingCollection<Action>? _workQueue;

        /// <summary>One piece of threaded work and the result the caller is waiting
        /// for.
        ///
        /// The results live HERE rather than in locals captured from the caller's
        /// stack: a job that finishes after its caller gave up would otherwise write
        /// into variables whose owner has moved on. The wait handle is reference
        /// counted for the same reason - the caller used to dispose it on the way
        /// out, so a late job called Set on a disposed object and took the worker
        /// thread down with it, after which the session looked alive and answered
        /// nothing ever again.</summary>
        private sealed class DbJob
        {
            public readonly ManualResetEventSlim Done = new(false);

            /// <summary>Set by the worker the moment it takes this job. A caller that
            /// gives up waiting needs to know which of the two happened: the statement
            /// was still behind other work, or it had already reached the database.
            /// Neither may be retried by the engine, but they are different problems -
            /// the first is a starved worker, the second a slow database - and a shard
            /// that cannot tell them apart cannot fix either.</summary>
            public volatile bool Started;
            public bool Ok;
            public int Count;
            public string Error = "";
            private int _refs = 2;          // the caller and the worker

            /// <summary>Whoever lets go last closes the door.</summary>
            public void Release()
            {
                if (Interlocked.Decrement(ref _refs) == 0)
                    Done.Dispose();
            }
        }

        private int _pendingWork;
        private long _rejectedWork;
        private long _timedOutWork;
        private long _timedOutQueued;
        private long _timedOutRunning;
        private long _workerFaults;

        /// <summary>Queued work not yet taken by the worker.</summary>
        public int PendingWorkCount => Volatile.Read(ref _pendingWork);

        /// <summary>Work refused because the queue was at its cap.</summary>
        public long RejectedWorkCount => Interlocked.Read(ref _rejectedWork);

        /// <summary>Calls that gave up waiting. The work may still run.</summary>
        public long TimedOutWorkCount => Interlocked.Read(ref _timedOutWork);

        /// <summary>Of those, the ones whose statement had not started: the worker was
        /// still busy with earlier work.</summary>
        public long TimedOutQueuedCount => Interlocked.Read(ref _timedOutQueued);

        /// <summary>Of those, the ones whose statement was already running against the
        /// database.</summary>
        public long TimedOutRunningCount => Interlocked.Read(ref _timedOutRunning);

        /// <summary>Exceptions that escaped a job on the worker thread.</summary>
        public long WorkerFaultCount => Interlocked.Read(ref _workerFaults);

        /// <summary>How much work may wait. A stalled database used to grow this
        /// queue without end, which turns a database problem into an out-of-memory
        /// shard; refusing is a worse answer for one script line and a better one for
        /// the process. Not a BlockingCollection capacity on purpose: that BLOCKS the
        /// caller when full, and the caller here is the game loop.</summary>
        public int MaxPendingWork { get; set; } = 1000;

        /// <summary>Queue work, or refuse it and say so.</summary>
        private bool TryEnqueue(Action work)
        {
            var queue = _workQueue;
            if (queue == null || queue.IsAddingCompleted)
                return false;
            if (Volatile.Read(ref _pendingWork) >= MaxPendingWork)
            {
                Interlocked.Increment(ref _rejectedWork);
                return false;
            }
            Interlocked.Increment(ref _pendingWork);
            try
            {
                queue.Add(work);
                return true;
            }
            catch (InvalidOperationException)
            {
                Interlocked.Decrement(ref _pendingWork);   // closed under us
                return false;
            }
        }

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

        /// <summary>Is there a thread actually taking work off the queue?
        ///
        /// The queue exists from the moment a UseThread session is registered; the
        /// worker only starts when the session connects. Work posted in between is
        /// posted to nobody: the caller - the game loop - waits out the whole read
        /// timeout, is told the statement "may still be running", and then the
        /// statement really does run, minutes later, the moment somebody connects.
        /// With UseThread off the same script line answers "DB is not connected"
        /// immediately, which is the answer both should give (review work item
        /// D02).</summary>
        private bool HasRunningWorker =>
            Config?.UseThread == true && _workQueue != null && _workerThread != null;

        /// <summary>Queue a query on the worker thread without blocking; runs inline
        /// when no worker is available.</summary>
        public bool EnqueueQuery(string sql)
        {
            if (HasRunningWorker)
                return TryEnqueue(() => QueryInternal(sql, out _, out _));
            return QueryInternal(sql, out _, out _);
        }

        /// <summary>Queue a non-query on the worker thread without blocking; runs
        /// inline when no worker is available.</summary>
        public bool EnqueueExecute(string sql)
        {
            if (HasRunningWorker)
                return TryEnqueue(() => ExecuteInternal(sql, out _, out _));
            return ExecuteInternal(sql, out _, out _);
        }

        public DbSession(DbConnectionConfig config, ILogger logger)
        {
            Config = config;
            _logger = logger;
            if (config.UseThread)
                _workQueue = new BlockingCollection<Action>();
        }

        /// <summary>The provider and connection string a script opened this session
        /// with explicitly, when it did. Null when the live connection came from the
        /// registered config.
        ///
        /// A session can be pointed somewhere other than its ini section:
        /// <c>DB.CONNECT &lt;provider&gt;|&lt;connection string&gt;</c>. When the link
        /// then dropped, KeepAlive reopened it from the CONFIG - a different database -
        /// and every statement after that landed there with nothing said. A reconnect
        /// has to come back to the database the script is holding (review work item
        /// D02).</summary>
        private string? _explicitProvider;
        private string? _explicitConnectionString;

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

            return ConnectCore(provider, connStr, fromConfig: true, out error);
        }

        public bool Connect(string providerInvariantName, string connectionString, out string error)
            => ConnectCore(providerInvariantName, connectionString, fromConfig: false, out error);

        private bool ConnectCore(string providerInvariantName, string connectionString,
            bool fromConfig, out string error)
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
                    // Remember an explicit target so a reconnect returns to it; forget
                    // one when the session is (re)opened from its config, so a config
                    // the operator has since edited is honoured rather than a stale
                    // string kept from an earlier connect.
                    _explicitProvider = fromConfig ? null : providerInvariantName;
                    _explicitConnectionString = fromConfig ? null : connectionString;
                    _logger.LogInformation("DB session '{Name}' connected with provider {Provider}",
                        Config?.Name ?? "?", providerInvariantName);

                    if (Config?.UseThread == true && _workerThread == null)
                    {
                        // A reopened session needs a fresh inbox: the previous Close
                        // completed the old one for good.
                        if (_workQueue == null || _workQueue.IsAddingCompleted)
                            _workQueue = new BlockingCollection<Action>();
                        StartWorkerThread(_workQueue);
                    }

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

            if (HasRunningWorker)
            {
                var job = new DbJob();
                if (!TryEnqueue(() =>
                    {
                        job.Started = true;
                        job.Ok = ExecuteInternal(sql, out int n, out string e);
                        job.Count = n;
                        job.Error = e;
                        job.Done.Set();
                        job.Release();
                    }))
                {
                    job.Release(); job.Release();
                    error = "DB work queue is full; the statement was not run.";
                    return false;
                }

                if (!WaitForJob(job, out affectedRows, out error))
                    return false;
                return job.Ok;
            }

            return ExecuteInternal(sql, out affectedRows, out error);
        }

        public bool Query(string sql, out int rowCount, out string error)
        {
            rowCount = 0;
            error = "";

            if (HasRunningWorker)
            {
                var job = new DbJob();
                if (!TryEnqueue(() =>
                    {
                        job.Started = true;
                        job.Ok = QueryInternal(sql, out int n, out string e);
                        job.Count = n;
                        job.Error = e;
                        job.Done.Set();
                        job.Release();
                    }))
                {
                    job.Release(); job.Release();
                    error = "DB work queue is full; the query was not run.";
                    return false;
                }

                if (!WaitForJob(job, out rowCount, out error))
                    return false;
                return job.Ok;
            }

            return QueryInternal(sql, out rowCount, out error);
        }

        /// <summary>Wait for a job and answer honestly.
        ///
        /// The wait result used to be thrown away, so a call that timed out returned
        /// the job's default - false with an empty message - which a script cannot
        /// tell from "the database said no". It is a different outcome: the work may
        /// still be queued, still running, or already committed, and the one thing
        /// the engine must not do is retry a write on its own behalf.</summary>
        private bool WaitForJob(DbJob job, out int count, out string error)
        {
            count = 0;
            error = "";
            int seconds = Config?.ReadTimeout > 0 ? Config.ReadTimeout : 30;

            if (!job.Done.Wait(TimeSpan.FromSeconds(seconds)))
            {
                // The worker still owns the job; it releases the handle when it is
                // done with it.
                Interlocked.Increment(ref _timedOutWork);
                bool started = job.Started;
                Interlocked.Increment(ref started ? ref _timedOutRunning : ref _timedOutQueued);
                job.Release();
                error = started
                    ? $"DB timeout after {seconds}s; the statement had started and may still be running."
                    : $"DB timeout after {seconds}s; the statement was still queued behind other work.";
                _logger.LogWarning("DB session '{Name}' timed out after {Seconds}s ({Where})",
                    Config?.Name ?? "?", seconds, started ? "running" : "queued");
                return false;
            }

            count = job.Count;
            error = job.Error;
            job.Release();
            return true;
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
                // Empty the result set FIRST, before the connection check and before
                // the query runs - upstream does exactly this (CDataBase::query,
                // CDataBase.cpp:99, Clear() then NUMROWS=0). Keeping the old rows
                // through a failure means a script that queries and reads db.row.*
                // without checking the return gets the PREVIOUS query's data, which
                // is the worst answer available: not an error, not empty, but
                // somebody else's row.
                //
                // An EMPTY table rather than none: upstream clears the map and then
                // sets NUMROWS to 0 in the same breath, so db.row.numrows answers "0"
                // after a failed query instead of refusing to resolve. The difference
                // matters to a script that branches on the count.
                _rowTable = new DataTable();

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
                if (Config?.KeepAlive == true)
                {
                    // Back to whatever this session was last opened against - the
                    // explicit target if a script named one, the config otherwise.
                    if (_explicitConnectionString != null && _explicitProvider != null)
                        return ConnectCore(_explicitProvider, _explicitConnectionString,
                            fromConfig: false, out error);
                    if (Config.Host.Length > 0)
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

        /// <summary>Start a worker bound to the queue it was given, rather than to
        /// whatever the field holds when the thread gets around to running: a close and
        /// reopen between those two moments would otherwise leave the worker draining a
        /// queue nobody posts to.</summary>
        private void StartWorkerThread(BlockingCollection<Action> queue)
        {
            _workerThread = new Thread(() =>
            {
                foreach (var action in queue.GetConsumingEnumerable())
                {
                    // Taken off the queue: it is no longer pending, however it ends.
                    Interlocked.Decrement(ref _pendingWork);
                    try { action(); }
                    catch (Exception ex)
                    {
                        // Counted, not just logged. A worker that keeps faulting is a
                        // session that looks alive and answers nothing, and the only
                        // way anyone finds out today is by noticing the silence.
                        Interlocked.Increment(ref _workerFaults);
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
            var queue = _workQueue;
            queue?.CompleteAdding();
            _workerThread?.Join(TimeSpan.FromSeconds(5));
            _workerThread = null;
            // Drop the completed queue so a later Connect mints a usable one. Keeping
            // it would only preserve the state that made the reopen useless.
            if (queue != null && ReferenceEquals(queue, _workQueue))
                _workQueue = null;
        }
    }
}
