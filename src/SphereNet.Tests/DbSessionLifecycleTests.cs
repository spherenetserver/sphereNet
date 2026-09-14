using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Scripting.Execution;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A script DB session that has been closed can be opened again (review finding B9).
///
/// With UseThread the session owns a worker thread draining a BlockingCollection.
/// Close called CompleteAdding on that collection - which is permanent, by design -
/// and the queue was readonly, so a later Connect started a fresh worker on a queue
/// that would never accept work again. The first connect and query succeed, the close
/// succeeds, the reconnect reports success, and only the next query fails:
/// "The collection has been marked as complete with regards to additions."
///
/// A script cannot tell that apart from a database problem, so the reconnect logic a
/// shard writes around a dropped connection - close, connect, retry - turns a
/// recoverable outage into a dead session for the rest of the process.
/// </summary>
public sealed class DbSessionLifecycleTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;

    public DbSessionLifecycleTests(ITestOutputHelper output)
    {
        _out = output;
        _dbPath = Path.Combine(Path.GetTempPath(), $"sphnet_db_{Guid.NewGuid():N}.sqlite");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private ScriptDbAdapter Adapter(bool useThread)
    {
        // The server registers this at startup (Program.NetworkHandlers.RegisterDbProviders);
        // a test process has to do it for itself or every Connect fails at provider
        // resolution rather than at the database.
        if (!System.Data.Common.DbProviderFactories.TryGetFactory("Microsoft.Data.Sqlite", out _))
            System.Data.Common.DbProviderFactories.RegisterFactory(
                "Microsoft.Data.Sqlite", Microsoft.Data.Sqlite.SqliteFactory.Instance);

        var adapter = new ScriptDbAdapter(LoggerFactory.Create(_ => { }).CreateLogger<ScriptDbAdapter>());
        adapter.RegisterConnection(new DbConnectionConfig
        {
            Name = "test",
            Provider = "Microsoft.Data.Sqlite",
            Database = _dbPath,
            UseThread = useThread,
        });
        Assert.True(adapter.Select("test", out string selErr), selErr);
        return adapter;
    }

    [Fact]
    public void AThreadedSessionSurvivesCloseAndReconnect()
    {
        var db = Adapter(useThread: true);

        Assert.True(db.Connect(out string e1), e1);
        Assert.True(db.Execute("CREATE TABLE t (marker INTEGER)", out _, out string e2), e2);
        Assert.True(db.Execute("INSERT INTO t (marker) VALUES (42)", out _, out string e3), e3);

        db.Close();

        Assert.True(db.Connect(out string e4), e4);
        bool queried = db.Query("SELECT marker FROM t", out _, out string e5);

        _out.WriteLine($"query after reconnect: ok={queried} error='{e5}'");

        // The failure was not a database error and did not look like one. A shard's
        // own reconnect handling - close, connect, retry - would have left the session
        // dead for the rest of the process while reporting a successful connect.
        Assert.True(queried, e5);
    }

    [Fact]
    public void TheReconnectedSessionStillRunsWork()
    {
        var db = Adapter(useThread: true);
        Assert.True(db.Connect(out _));
        Assert.True(db.Execute("CREATE TABLE t (marker INTEGER)", out _, out _));
        db.Close();

        Assert.True(db.Connect(out _));
        Assert.True(db.Execute("INSERT INTO t (marker) VALUES (7)", out int affected, out string err), err);

        _out.WriteLine($"rows affected after reconnect: {affected}");

        // Reporting success is not the same as doing the work: the queue has to accept
        // it AND the worker has to run it.
        Assert.Equal(1, affected);
        Assert.True(db.Query("SELECT marker FROM t", out _, out _));
    }

    [Fact]
    public void CloseAndConnectCanBeRepeated()
    {
        var db = Adapter(useThread: true);

        for (int cycle = 0; cycle < 4; cycle++)
        {
            Assert.True(db.Connect(out string err), $"cycle {cycle}: {err}");
            Assert.True(db.Execute(
                "CREATE TABLE IF NOT EXISTS t (marker INTEGER)", out _, out string e), $"cycle {cycle}: {e}");
            db.Close();
        }

        // Once is the bug; four times is the shape of a shard reconnecting after every
        // dropped connection for a day.
        Assert.True(db.Connect(out _));
        Assert.True(db.Query("SELECT COUNT(*) FROM t", out _, out _));
    }

    [Fact]
    public void AnUnthreadedSessionReconnectsToo()
    {
        var db = Adapter(useThread: false);

        Assert.True(db.Connect(out _));
        Assert.True(db.Execute("CREATE TABLE t (marker INTEGER)", out _, out _));
        db.Close();
        Assert.True(db.Connect(out _));

        // The control: the synchronous path never had a queue to close, so it must
        // keep working exactly as before.
        Assert.True(db.Query("SELECT marker FROM t", out _, out string err), err);
    }

    [Fact]
    public void TheDefaultProviderIsOneTheServerCanActuallyResolve()
    {
        // What a shard gets by writing nothing: DbConnectionConfig's own default.
        string shipped = new DbConnectionConfig().Provider;
        _out.WriteLine($"default provider: {shipped}");

        // The server registers it at startup, so drive the same registration the same
        // way the server does before asking.
        SphereNet.Server.Program.RegisterDbProvidersForTests();

        Assert.True(System.Data.Common.DbProviderFactories.TryGetFactory(shipped, out var factory),
            $"the shipped default provider '{shipped}' is not registered by the server");
        Assert.NotNull(factory);

        // The failure this replaces was not a network or password error - it happened
        // before a connection was opened at all, which is the hardest kind of problem
        // to diagnose from a shard's side.
    }

    [Fact]
    public void AnUnknownProviderSaysSoInsteadOfFailingLater()
    {
        var db = new ScriptDbAdapter(
            LoggerFactory.Create(_ => { }).CreateLogger<ScriptDbAdapter>());
        db.RegisterConnection(new DbConnectionConfig
        {
            Name = "nope",
            Provider = "NotARegisteredProvider",
            Database = ":memory:",
        });
        Assert.True(db.Select("nope", out _));

        bool ok = db.Connect(out string error);
        _out.WriteLine($"unknown provider: ok={ok} error='{error}'");

        // The connect has to fail with the provider named. This is the shape of B8:
        // the shipped DEFAULT provider is not registered anywhere, so a shard using
        // the defaults meets this message rather than a network or password error -
        // and the message is the only thing that says which of the two it is.
        Assert.False(ok);
        Assert.Contains("NotARegisteredProvider", error, StringComparison.OrdinalIgnoreCase);
    }
}
