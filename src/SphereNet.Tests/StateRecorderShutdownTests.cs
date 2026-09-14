using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Server.Recording;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// StateRecorder shutdown and backpressure (review finding B6).
///
/// The recorder is diagnostics, so its failure mode matters more than its throughput:
/// it must not take the shard down by growing a queue without bound, and it must not
/// close its own connection underneath a writer that is still inside a transaction.
/// When it does lose records it has to say so — a recorder that silently stops
/// recording is worse than one that is switched off, because nobody knows to look.
/// </summary>
public sealed class StateRecorderShutdownTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;
    private readonly string _dbPath;
    private readonly List<StateRecorder> _recorders = [];
    private readonly List<IDisposable> _lockHandles = [];

    public StateRecorderShutdownTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "spherenet-staterec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "state.db");
    }

    public void Dispose()
    {
        // Release the write lock FIRST, then let every writer thread out of SQLite
        // before this class goes away. A test that leaves a thread parked inside the
        // native library is a crash waiting for process exit, and it would land on
        // whichever test happened to be running.
        foreach (var d in _lockHandles)
        {
            try { d.Dispose(); } catch { /* the writer may have rolled it back */ }
        }
        foreach (var rec in _recorders)
        {
            try
            {
                rec.Dispose();
                Assert.True(rec.WaitForWriterExit(15_000), "a recorder's writer thread never exited");
            }
            catch (ObjectDisposedException) { /* already torn down by the test */ }
        }
        try { Directory.Delete(_dir, recursive: true); } catch { /* nothing to salvage */ }
    }

    // ------------------------------------------------------------------

    private (GameWorld World, List<Character> Chars) Roster(int count)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;

        var chars = new List<Character>();
        for (int i = 0; i < count; i++)
        {
            var ch = world.CreateCharacter();
            ch.IsPlayer = true;
            ch.Name = "player" + i;
            ch.MaxHits = 100; ch.Hits = 100;
            world.PlaceCharacter(ch, new Point3D((short)(100 + i), 100, 0, 0));
            chars.Add(ch);
        }
        return (world, chars);
    }

    private StateRecorder NewRecorder(int maxPending = 500_000, int joinMs = 5_000)
    {
        var rec = new StateRecorder(_dbPath, NullLogger.Instance, playersOnly: true,
            moveScanMs: 100, snapshotMs: 100_000)
        {
            MaxPendingRecords = maxPending,
            ShutdownJoinMs = joinMs,
        };
        rec.Initialize();
        _recorders.Add(rec);
        return rec;
    }

    /// <summary>Drive one movement scan: every character steps one tile, so the scan
    /// has something to enqueue.</summary>
    private static void StepAndScan(StateRecorder rec, GameWorld world, List<Character> chars, long nowMs)
    {
        foreach (var ch in chars)
            world.PlaceCharacter(ch, new Point3D((short)(ch.X + 1), ch.Y, 0, 0));
        rec.Tick(nowMs, world.GetAllCharactersSnapshot);
    }

    private SqliteConnection HoldTheWriteLock()
    {
        // One writer at a time, even under WAL. An open IMMEDIATE transaction on a
        // second connection makes every recorder flush wait out its busy_timeout and
        // then fail — a stand-in for the slow disk / locked database the review asks
        // the shutdown path to survive.
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO char_moves(char_uid,ts,x,y,z,map,dir) VALUES(1,1,1,1,1,0,0)";
        cmd.ExecuteNonQuery();
        _lockHandles.Add(tx);
        _lockHandles.Add(conn);
        return conn;
    }

    private long RowCount()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM char_moves";
        return (long)cmd.ExecuteScalar()!;
    }

    // ------------------------------------------------------------------

    [Fact]
    public void AShutdownThatFinishesSaysWhatItWrote()
    {
        var (world, chars) = Roster(4);
        var rec = NewRecorder();

        StepAndScan(rec, world, chars, 5_000);
        rec.Dispose();

        var report = rec.LastShutdownReport;
        Assert.NotNull(report);
        _out.WriteLine($"stopped={report!.Value.WriterStopped} written={report.Value.Written} " +
                       $"remaining={report.Value.Remaining} dropped={report.Value.Dropped}");

        // The writer stopped inside its budget, so the counts are a complete account
        // of the run: nothing left in the queues, and the rows are on disk.
        Assert.True(report.Value.WriterStopped);
        Assert.Equal(0, report.Value.Remaining);
        Assert.Equal(0, report.Value.Dropped);
        Assert.Equal(4, report.Value.Written);
        Assert.Equal(4, RowCount());
    }

    [Fact]
    public void ATickAfterShutdownIsIgnoredRatherThanSignallingADisposedHandle()
    {
        var (world, chars) = Roster(3);
        var rec = NewRecorder();

        StepAndScan(rec, world, chars, 5_000);
        rec.Dispose();

        // The producer is stopped FIRST. Before this, Tick only checked that the
        // connection field was non-null — which Dispose never cleared — so a tick
        // arriving during shutdown enqueued records nobody would drain and called
        // Set() on the disposed flush handle, throwing ObjectDisposedException out
        // of the server's own tick.
        var ex = Record.Exception(() => StepAndScan(rec, world, chars, 10_000));
        Assert.Null(ex);
        Assert.Equal(0, rec.GetStats().PendingMoves);
    }

    [Fact]
    public void ALockedDatabaseAtShutdownIsReportedInsteadOfClosingUnderTheWriter()
    {
        var (world, chars) = Roster(5);
        var rec = NewRecorder(joinMs: 200);
        HoldTheWriteLock();

        StepAndScan(rec, world, chars, 5_000);
        rec.Dispose();

        var report = rec.LastShutdownReport;
        Assert.NotNull(report);
        _out.WriteLine($"stopped={report!.Value.WriterStopped} remaining={report.Value.Remaining}");

        // The writer is still inside its final flush, waiting out the lock. Dispose
        // used to ignore the Join result and close the connection and the prepared
        // commands anyway, so the flush met its own disposed connection mid
        // transaction and the records it carried vanished behind a clean-looking
        // shutdown. Now the budget overrun is a reported outcome.
        Assert.False(report.Value.WriterStopped);
        Assert.True(report.Value.Remaining > 0,
            $"the unwritten records should be counted, got {report.Value.Remaining}");
        Assert.Equal(0, report.Value.Written);
    }

    [Fact]
    public void TheQueueCapIsEnforcedWhereRecordsEnter()
    {
        var (world, chars) = Roster(20);
        var rec = NewRecorder(maxPending: 40, joinMs: 200);
        HoldTheWriteLock();

        for (int i = 1; i <= 10; i++)
            StepAndScan(rec, world, chars, 5_000 + i * 1_000);   // 200 records offered

        var stats = rec.GetStats();
        _out.WriteLine($"pending={stats.PendingMoves} inflight={stats.InFlight} " +
                       $"dropped={stats.Dropped} written={stats.Written} oldest={stats.OldestPendingAgeMs}ms");

        // Nothing can commit while the lock is held, so all 200 records want to stay.
        // The old cap lived in the flush catch block: it compared the queue count at
        // that instant and then re-added the whole drained batch regardless, so it
        // was a hint, not a bound. Now the cap is checked at the enqueue - by the
        // producer AND by the re-queue - so it holds.
        Assert.True(stats.PendingMoves <= 40,
            $"queue passed its cap: {stats.PendingMoves} > 40");

        // And the loss is counted rather than quiet: a recorder that stops recording
        // without saying so is worse than one that was never switched on.
        Assert.True(stats.Dropped > 0, "records over the cap must be counted as dropped");
        Assert.True(stats.OldestPendingAgeMs >= 0);

        rec.Dispose();
    }

    [Fact]
    public void PositionsForCharactersThatAreGoneAreDropped()
    {
        var (world, chars) = Roster(6);
        var rec = NewRecorder();

        StepAndScan(rec, world, chars, 5_000);
        Assert.Equal(6, rec.TrackedPositionCount);

        // Four characters leave the world (deleted, or simply no longer in the
        // roster). Their last positions used to stay in the map for the lifetime of
        // the process - the scan skipped deleted characters, which meant it never
        // reached the line that would have cleaned them up.
        foreach (var ch in chars.Take(4))
            world.DeleteObject(ch);

        StepAndScan(rec, world, chars.Skip(4).ToList(), 10_000);

        _out.WriteLine($"tracked positions after 4 of 6 left: {rec.TrackedPositionCount}");
        Assert.Equal(2, rec.TrackedPositionCount);

        rec.Dispose();
    }
}
