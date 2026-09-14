using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Scripting.Execution;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What a script is told when a threaded query does not come back, and what it can
/// read afterwards (review finding B9, second half).
///
/// The first half - a session that could not be reopened - was repaired earlier.
/// This is the rest of the same finding: the wait result was ignored, so a timeout
/// was indistinguishable from a failure; the completion object was disposed when the
/// call returned, so a job that finished late touched a disposed object from the
/// worker thread; the results came back through captured locals, which a late job
/// would write after the caller had moved on; the queue was unbounded; and a failed
/// query left the PREVIOUS query's rows readable.
///
/// That last one is not a compatibility quirk. Upstream clears the result map before
/// it does anything else - before it even checks that it is connected
/// (CDataBase::query, CDataBase.cpp:99) - so a script that queries and reads the rows
/// without checking the return sees an empty set there and the last query's data
/// here.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DbQueryTimeoutTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;

    public DbQueryTimeoutTests(ITestOutputHelper output)
    {
        _out = output;
        _dbPath = Path.Combine(Path.GetTempPath(), $"sphnet_dbq_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch (IOException) { }
    }

    private ScriptDbAdapter Adapter(bool useThread)
    {
        // The server registers the provider at startup; a test process has to do it
        // for itself or every Connect fails at provider resolution.
        if (!System.Data.Common.DbProviderFactories.TryGetFactory("Microsoft.Data.Sqlite", out _))
            System.Data.Common.DbProviderFactories.RegisterFactory(
                "Microsoft.Data.Sqlite", Microsoft.Data.Sqlite.SqliteFactory.Instance);

        var adapter = new ScriptDbAdapter(
            Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { })
                .CreateLogger<ScriptDbAdapter>());
        adapter.RegisterConnection(new DbConnectionConfig
        {
            Name = "test",
            Provider = "Microsoft.Data.Sqlite",
            Database = _dbPath,
            UseThread = useThread,
            ReadTimeout = 2,        // the wait budget the timeout tests measure
        });
        Assert.True(adapter.Select("test", out string selErr), selErr);
        Assert.True(adapter.Connect(out string err), err);
        return adapter;
    }

    private static void Seed(ScriptDbAdapter db)
    {
        Assert.True(db.Execute("CREATE TABLE t(marker INTEGER)", out _, out string e1), e1);
        Assert.True(db.Execute("INSERT INTO t(marker) VALUES(42)", out _, out string e2), e2);
    }

    // ------------------------------------------------------------------

    [Fact]
    public void AFailedQueryLeavesNoRowsFromTheOneBeforeIt()
    {
        var db = Adapter(useThread: false);
        Seed(db);

        Assert.True(db.Query("SELECT marker FROM t", out int rows, out _));
        Assert.Equal(1, rows);
        Assert.True(db.TryResolveRowValue("db.row.0.marker", out string before));
        Assert.Equal("42", before);

        // A query that cannot run at all. Upstream empties the result map before it
        // even checks the connection, so what a script reads next is nothing - not
        // the last successful query's data.
        Assert.False(db.Query("SELECT * FROM no_such_table", out int badRows, out string err));
        Assert.Equal(0, badRows);
        Assert.NotEqual("", err);

        bool stale = db.TryResolveRowValue("db.row.0.marker", out string after);
        db.TryResolveRowValue("db.row.numrows", out string numrows);
        _out.WriteLine($"after a failed query: db.row.0.marker resolved={stale} value='{after}', numrows='{numrows}'");

        Assert.False(stale, $"a failed query must not leave the previous rows readable (got '{after}')");
        Assert.Equal("0", numrows);
    }

    [Fact]
    public void ATimeoutIsNotReportedAsAnOrdinaryFailure()
    {
        var db = Adapter(useThread: true);
        Seed(db);

        // Occupy the single worker with something slower than the wait budget. SQLite
        // has no SLEEP, so a recursive CTE burns the time.
        Assert.True(db.QueryAsync(
            "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x < 40000000) " +
            "SELECT COUNT(*) FROM c"));

        var sw = Stopwatch.StartNew();
        bool ok = db.Query("SELECT marker FROM t", out int rows, out string err);
        sw.Stop();

        _out.WriteLine($"queued behind a long job: ok={ok} rows={rows} err='{err}' after {sw.ElapsedMilliseconds} ms");

        // The call must not claim success, and it must say WHY it failed. Returning
        // false with an empty message - which is what ignoring the wait result did -
        // tells a script the query failed when it may still be running.
        Assert.False(ok);
        Assert.Contains("timeout", err, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, rows);
    }

    [Fact]
    public void ALateJobDoesNotTouchTheCallerAfterItHasGone()
    {
        var db = Adapter(useThread: true);
        Seed(db);

        db.QueryAsync("WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x < 40000000) " +
                      "SELECT COUNT(*) FROM c");

        // This one times out while its job is still queued. The job then runs and
        // completes - against an object the caller used to dispose on its way out,
        // and into locals the caller no longer owns.
        db.Query("SELECT marker FROM t", out _, out _);

        // Give the worker time to finish the long job and then the timed-out one.
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && db.PendingWorkCount > 0)
            System.Threading.Thread.Sleep(50);

        _out.WriteLine($"worker errors after the late completion: {db.WorkerFaultCount}");

        // A worker that died on a disposed wait handle would stop draining the queue,
        // so the session would look alive and answer nothing ever again.
        Assert.Equal(0, db.WorkerFaultCount);
        Assert.True(db.Query("SELECT marker FROM t", out int rows, out string err), err);
        Assert.Equal(1, rows);
    }

    [Fact]
    public void TheQueueRefusesWorkRatherThanGrowingWithoutEnd()
    {
        var db = Adapter(useThread: true);
        Seed(db);
        db.MaxPendingWork = 8;

        db.QueryAsync("WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x+1 FROM c WHERE x < 40000000) " +
                      "SELECT COUNT(*) FROM c");

        int accepted = 0, refused = 0;
        for (int i = 0; i < 200; i++)
        {
            if (db.QueryAsync("SELECT marker FROM t")) accepted++;
            else refused++;
        }

        _out.WriteLine($"cap 8: accepted={accepted} refused={refused} rejected counter={db.RejectedWorkCount}");

        // An unbounded queue turns a stalled database into an out-of-memory shard.
        // Refusing is a worse answer for one script line and a better one for the
        // process, and it is countable.
        Assert.True(refused > 0, "a bounded queue has to refuse something under this load");
        Assert.Equal(refused, db.RejectedWorkCount);
        Assert.True(db.PendingWorkCount <= 8 + 1, $"queue passed its cap: {db.PendingWorkCount}");
    }

    [Fact]
    public void AnUncontendedThreadedQueryStillAnswersNormally()
    {
        var db = Adapter(useThread: true);
        Seed(db);

        // The control: with the worker free, the threaded path behaves exactly like
        // the inline one. Without this the tests above would pass just as well if
        // threaded queries had stopped working altogether.
        Assert.True(db.Query("SELECT marker FROM t", out int rows, out string err), err);
        Assert.Equal(1, rows);
        Assert.True(db.TryResolveRowValue("db.row.0.marker", out string v));
        Assert.Equal("42", v);
        Assert.Equal(0, db.TimedOutWorkCount);
    }
}
