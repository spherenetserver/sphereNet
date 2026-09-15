using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Scripting.Execution;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Two databases, one adapter (review work item D02, the part that does not need a
/// real MySQL server).
///
/// Every existing DB test uses one session, so nothing asserted the property that
/// matters once a pack registers more than one connection: a statement must reach the
/// database the script selected and no other. Two SQLite files stand in for the two
/// MySQL databases the integration environment will use — the adapter code under test
/// is provider-independent, and the marker rows make it visible which file a write
/// actually landed in.
///
/// Three contracts are pinned here as well as tested, because a script author has no
/// other way to learn them: the active session is adapter-global (a nested call that
/// selects does not put it back), an async call runs inline when the session has no
/// worker, and a synchronous call still waits for the worker when it has one.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NamedDbSessionTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _alphaPath;
    private readonly string _betaPath;

    public NamedDbSessionTests(ITestOutputHelper output)
    {
        _out = output;
        string id = Guid.NewGuid().ToString("N");
        _alphaPath = Path.Combine(Path.GetTempPath(), $"sphnet_alpha_{id}.db");
        _betaPath = Path.Combine(Path.GetTempPath(), $"sphnet_beta_{id}.db");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string p in new[] { _alphaPath, _betaPath })
        {
            try { File.Delete(p); } catch (IOException) { }
        }
    }

    private static void EnsureProvider()
    {
        if (!System.Data.Common.DbProviderFactories.TryGetFactory("Microsoft.Data.Sqlite", out _))
            System.Data.Common.DbProviderFactories.RegisterFactory(
                "Microsoft.Data.Sqlite", Microsoft.Data.Sqlite.SqliteFactory.Instance);
    }

    private static ScriptDbAdapter NewAdapter()
    {
        EnsureProvider();
        return new ScriptDbAdapter(LoggerFactory.Create(_ => { }).CreateLogger<ScriptDbAdapter>());
    }

    private static DbConnectionConfig Config(string name, string path, bool useThread = false,
        bool keepAlive = false, int readTimeout = 2) => new()
        {
            Name = name,
            Provider = "Microsoft.Data.Sqlite",
            Database = path,
            UseThread = useThread,
            KeepAlive = keepAlive,
            ReadTimeout = readTimeout,
        };

    /// <summary>Both sessions registered, connected and holding an empty marker
    /// table. "alpha" is left active, as a pack's first connection would be.</summary>
    private ScriptDbAdapter TwoSessions(bool useThread = false)
    {
        var db = NewAdapter();
        db.RegisterConnection(Config("alpha", _alphaPath, useThread));
        db.RegisterConnection(Config("beta", _betaPath, useThread));
        Assert.True(db.Connect("alpha", out string e1), e1);
        Assert.True(db.Connect("beta", out string e2), e2);
        Assert.True(db.Execute("alpha", "CREATE TABLE marks(who TEXT)", out _, out string e3), e3);
        Assert.True(db.Execute("beta", "CREATE TABLE marks(who TEXT)", out _, out string e4), e4);
        Assert.True(db.Select("alpha", out string e5), e5);
        return db;
    }

    /// <summary>Everything the named database holds, read through a connection of its
    /// own so the assertion does not depend on the adapter it is checking.</summary>
    private static string Marks(string path)
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT who FROM marks ORDER BY who";
        using var reader = cmd.ExecuteReader();
        var rows = new System.Collections.Generic.List<string>();
        while (reader.Read()) rows.Add(reader.GetString(0));
        return string.Join(",", rows);
    }

    // ---- selection ------------------------------------------------------

    [Fact]
    public void AWriteLandsInTheSelectedDatabaseAndNoOther()
    {
        using var db = TwoSessions();

        Assert.True(db.Execute("INSERT INTO marks(who) VALUES('a')", out _, out string e1), e1);
        Assert.True(db.Select("beta", out string e2), e2);
        Assert.True(db.Execute("INSERT INTO marks(who) VALUES('b')", out _, out string e3), e3);

        _out.WriteLine($"alpha=[{Marks(_alphaPath)}] beta=[{Marks(_betaPath)}]");
        Assert.Equal("a", Marks(_alphaPath));
        Assert.Equal("b", Marks(_betaPath));
    }

    [Fact]
    public void ANamedWriteIgnoresWhichSessionIsActive()
    {
        using var db = TwoSessions();

        // DB.EXECUTE against a named session while another one is selected: the name
        // decides, not the selection.
        Assert.True(db.Execute("beta", "INSERT INTO marks(who) VALUES('b')", out _, out string e1), e1);

        Assert.Equal("", Marks(_alphaPath));
        Assert.Equal("b", Marks(_betaPath));
        Assert.Equal("alpha", db.ActiveSessionName);
    }

    [Fact]
    public void ASelectOfAnUnregisteredNameChangesNothing()
    {
        using var db = TwoSessions();

        Assert.False(db.Select("ghost", out string err));
        Assert.Contains("ghost", err);

        // The dangerous failure mode is a failed select that still moves the
        // selection, or leaves it undefined: the next write would land in whichever
        // database happened to follow.
        Assert.Equal("alpha", db.ActiveSessionName);
        Assert.True(db.Execute("INSERT INTO marks(who) VALUES('a')", out _, out string e1), e1);
        Assert.Equal("a", Marks(_alphaPath));
        Assert.Equal("", Marks(_betaPath));
    }

    [Fact]
    public void EachSessionKeepsItsOwnRows()
    {
        using var db = TwoSessions();
        Assert.True(db.Execute("alpha", "INSERT INTO marks(who) VALUES('a')", out _, out string e1), e1);
        Assert.True(db.Execute("beta", "INSERT INTO marks(who) VALUES('b1')", out _, out string e2), e2);
        Assert.True(db.Execute("beta", "INSERT INTO marks(who) VALUES('b2')", out _, out string e3), e3);

        Assert.True(db.Query("SELECT who FROM marks", out int alphaRows, out string e4), e4);
        Assert.Equal(1, alphaRows);

        Assert.True(db.Select("beta", out string e5), e5);
        Assert.True(db.Query("SELECT who FROM marks", out int betaRows, out string e6), e6);
        Assert.Equal(2, betaRows);
        Assert.True(db.TryResolveRowValue("db.row.0.who", out string betaFirst));
        Assert.Equal("b1", betaFirst);

        // Back to alpha: db.row.* must answer with alpha's last result, not with
        // whatever the other session read most recently.
        Assert.True(db.Select("alpha", out string e7), e7);
        Assert.True(db.TryResolveRowValue("db.row.numrows", out string alphaCount));
        Assert.Equal("1", alphaCount);
        Assert.True(db.TryResolveRowValue("db.row.0.who", out string alphaFirst));
        Assert.Equal("a", alphaFirst);
    }

    [Fact]
    public void TheActiveSessionIsGlobalAndOutlivesTheCallThatChangedIt()
    {
        // The contract, pinned: db.select is adapter state, not call state. A
        // function that selects and returns leaves its caller on the other database,
        // so a pack that switches inside a function has to switch back itself. This
        // is not obviously right or wrong - it is upstream's shape, where the DB
        // object is global - but a script author cannot find it out any other way.
        using var db = TwoSessions();

        void NestedCall()
        {
            Assert.True(db.Select("beta", out string e), e);
            Assert.True(db.Execute("INSERT INTO marks(who) VALUES('b')", out _, out string e2), e2);
        }

        NestedCall();
        Assert.Equal("beta", db.ActiveSessionName);
        Assert.True(db.Execute("INSERT INTO marks(who) VALUES('after')", out _, out string e3), e3);

        Assert.Equal("", Marks(_alphaPath));
        Assert.Equal("after,b", Marks(_betaPath));
    }

    // ---- queued work ----------------------------------------------------

    [Fact]
    public void AQueuedWriteRunsOnTheSessionItWasQueuedFor()
    {
        using var db = TwoSessions(useThread: true);

        // Queued while alpha is selected, and the selection changes immediately
        // afterwards - the window a pack opens whenever it fires an async write and
        // carries on with another connection.
        Assert.True(db.ExecuteAsync("INSERT INTO marks(who) VALUES('a')"));
        Assert.True(db.Select("beta", out string e1), e1);

        // Draining is observed through beta's own synchronous call: it is queued
        // behind nothing, and by the time alpha is asked again the work has been
        // taken. No sleeps - a synchronous call on each session is the barrier.
        Assert.True(db.Execute("INSERT INTO marks(who) VALUES('b')", out _, out string e2), e2);
        Assert.True(db.Select("alpha", out string e3), e3);
        Assert.True(db.Query("SELECT who FROM marks", out int rows, out string e4), e4);

        _out.WriteLine($"alpha=[{Marks(_alphaPath)}] beta=[{Marks(_betaPath)}] rows={rows}");
        Assert.Equal("a", Marks(_alphaPath));
        Assert.Equal("b", Marks(_betaPath));
    }

    [Fact]
    public void AnAsyncCallWithoutAWorkerRunsInline()
    {
        // The contract: with UseThread off there is no worker, so DB.AEXECUTE is a
        // DB.EXECUTE that throws its result away. A pack that relies on the write
        // having happened by the next line is right here and wrong with UseThread on.
        using var db = TwoSessions(useThread: false);

        Assert.True(db.ExecuteAsync("INSERT INTO marks(who) VALUES('a')"));
        Assert.Equal("a", Marks(_alphaPath));
    }

    [Fact]
    public void ASynchronousCallStillWaitsWhenAWorkerIsRunning()
    {
        // The other half of the same contract: UseThread moves the work to a thread
        // but DB.EXECUTE still blocks the caller until it comes back. The worker is
        // there to keep the connection on one thread, not to make the game loop
        // asynchronous.
        using var db = TwoSessions(useThread: true);

        Assert.True(db.Execute("INSERT INTO marks(who) VALUES('a')", out int affected, out string err), err);
        Assert.Equal(1, affected);
        Assert.Equal("a", Marks(_alphaPath));
    }

    // ---- the two defects -------------------------------------------------

    [Fact]
    public void AThreadedSessionThatIsNotConnectedSaysSoInsteadOfQueueingTheWrite()
    {
        EnsureProvider();
        using var db = NewAdapter();
        db.RegisterConnection(Config("alpha", _alphaPath, useThread: true, readTimeout: 1));
        Assert.True(db.Select("alpha", out string e1), e1);

        // Set the table up on the file itself: the session must stay unconnected.
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_alphaPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE marks(who TEXT)";
            cmd.ExecuteNonQuery();
        }

        // A script line that runs before DB.CONNECT. With UseThread off this answers
        // "DB is not connected" at once. With it on, the statement went into a queue
        // no worker was draining - the worker starts on connect - so the caller (the
        // game loop) waited out the whole read timeout and was told the statement
        // "may still be running".
        var sw = Stopwatch.StartNew();
        bool ok = db.Execute("INSERT INTO marks(who) VALUES('ghost')", out _, out string error);
        sw.Stop();
        _out.WriteLine($"ok={ok} after {sw.ElapsedMilliseconds}ms: {error}");

        Assert.False(ok);
        Assert.Contains("not connected", error, StringComparison.OrdinalIgnoreCase);
        Assert.True(sw.ElapsedMilliseconds < 900,
            $"the caller waited {sw.ElapsedMilliseconds}ms for a session that was never connected");

        // And the statement must not be sitting in a queue waiting for a connection:
        // connecting later would run a write the script was already told did not run.
        Assert.True(db.Connect("alpha", out string e2), e2);
        Assert.True(db.Execute("INSERT INTO marks(who) VALUES('real')", out _, out string e3), e3);
        Assert.Equal("real", Marks(_alphaPath));
    }

    [Fact]
    public void AReconnectGoesBackToTheDatabaseThatWasActuallyOpened()
    {
        EnsureProvider();
        using var db = NewAdapter();

        // The session is registered against beta - the [MYSQL alpha] section of the
        // ini, so to speak - but the script connects it explicitly to another
        // database, which DB.CONNECT <provider>|<connection string> allows.
        db.RegisterConnection(Config("session", _betaPath, keepAlive: true));
        Assert.True(db.Select("session", out string e1), e1);
        Assert.True(db.Connect("Microsoft.Data.Sqlite", $"Data Source={_alphaPath}", out string e2), e2);

        Assert.True(db.Execute("CREATE TABLE marks(who TEXT)", out _, out string e3), e3);
        Assert.True(db.Execute("INSERT INTO marks(who) VALUES('before')", out _, out string e4), e4);

        // The link drops. KeepAlive reopens it on the next statement - and it used to
        // reopen it from the registered config, which names a DIFFERENT database, so
        // every write after a dropped connection went somewhere the script never
        // asked for and nothing said a word.
        db.Close();
        bool ok = db.Execute("INSERT INTO marks(who) VALUES('after')", out _, out string err);
        _out.WriteLine($"after reconnect: ok={ok} err={err} alpha=[{Marks(_alphaPath)}]");

        Assert.True(ok, err);
        Assert.Equal("after,before", Marks(_alphaPath));
        Assert.False(File.Exists(_betaPath) && Marks(_betaPath).Length > 0,
            "the reconnect wrote into the registered database instead of the one that was open");
    }
}
