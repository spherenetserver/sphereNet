using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Server.Recording;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What the retention sweep keeps and what it throws away, while recording carries on
/// (review work item D10).
///
/// The recorder holds three days of movement and snapshots and deletes the rest on a
/// timer. A pinned period is the exception — an incident somebody is still looking
/// into — and the whole value of pinning is that the sweep honours it. That it runs
/// concurrently with the flush thread has been tested for SQLite errors; what it does
/// to the DATA had not been tested at all, and a sweep that quietly took a pinned
/// incident away would be found out long after the evidence was gone.
///
/// Serialized with the rest of the engine-static tests: the world this uses resolves
/// through the process-wide ambient resolvers, which the shared reset hook clears
/// between tests in that collection. Run in parallel with them, the recorder loses the
/// world mid-test and records nothing - a failure that only appears in a full run.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StateRecorderRetentionTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dbPath;

    public StateRecorderRetentionTests(ITestOutputHelper output)
    {
        _out = output;
        _dbPath = Path.Combine(Path.GetTempPath(), $"sphnet_retention_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
        }
    }

    private StateRecorder NewRecorder()
    {
        var rec = new StateRecorder(_dbPath, NullLogger.Instance, playersOnly: false,
            moveScanMs: 1, snapshotMs: 1);
        rec.Initialize();
        return rec;
    }

    /// <summary>Write a movement row straight into the database at a chosen moment.
    /// The recorder stamps its own rows with the wall clock, so rows old enough to be
    /// swept can only be made this way.</summary>
    private void WriteMove(uint charUid, long ts)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "INSERT INTO char_moves(char_uid, ts, x, y, z, map, dir) VALUES(@u, @t, 1, 1, 0, 0, 0)";
        cmd.Parameters.AddWithValue("@u", charUid);
        cmd.Parameters.AddWithValue("@t", ts);
        cmd.ExecuteNonQuery();
    }

    private int MoveCount()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM char_moves";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private List<long> MoveTimestamps()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ts FROM char_moves ORDER BY ts";
        using var reader = cmd.ExecuteReader();
        var list = new List<long>();
        while (reader.Read()) list.Add(reader.GetInt64(0));
        return list;
    }

    /// <summary>The recorder's clock, advanced one whole cleanup interval per sweep.
    ///
    /// Tick stamps _lastCleanupTick BEFORE queueing the cleanup work, so calling it
    /// twice with the SAME nowMs sweeps once - the second call is no longer past the
    /// interval. A test that swept, changed something and swept again was therefore
    /// not exercising its second sweep at all: it passed only when the first sweep's
    /// background pass happened to still be running and picked the change up, and
    /// timed out when that pass had already finished. Giving every call its own
    /// interval makes each sweep really run.</summary>
    private long _sweepClock;

    /// <summary>Drive the sweep and wait for it: it runs on a pool thread, so the
    /// test waits for the row count to settle rather than for a fixed time.</summary>
    private void SweepAndWait(StateRecorder rec, int expectedCount)
    {
        var chars = new List<Character>();
        _sweepClock += 600_001;                   // past the cleanup interval, again
        rec.Tick(_sweepClock, () => chars);

        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10) && MoveCount() != expectedCount)
            Thread.Sleep(25);
    }

    private static long DaysAgo(int days) =>
        DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeMilliseconds();

    // ---- what the sweep takes --------------------------------------------

    [Fact]
    public void RowsPastTheRetentionWindowAreRemovedAndRecentOnesAreNot()
    {
        using var rec = NewRecorder();
        WriteMove(1, DaysAgo(10));
        WriteMove(1, DaysAgo(5));
        long keep = DaysAgo(1);
        WriteMove(1, keep);
        Assert.Equal(3, MoveCount());

        SweepAndWait(rec, expectedCount: 1);

        _out.WriteLine($"after the sweep: {MoveCount()} row(s) left");
        Assert.Equal([keep], MoveTimestamps());
    }

    [Fact]
    public void APinnedPeriodSurvivesTheSweep()
    {
        using var rec = NewRecorder();
        long incident = DaysAgo(10);
        WriteMove(1, incident);
        WriteMove(1, DaysAgo(9));                 // old, and nobody pinned it

        // Somebody is still looking into that hour, so it is pinned.
        rec.PinPeriod(incident - 60_000, incident + 60_000, "an incident", "gm");

        SweepAndWait(rec, expectedCount: 1);

        _out.WriteLine($"pinned incident kept: {MoveTimestamps().Contains(incident)}; " +
                       $"{MoveCount()} row(s) left");

        // The pin is the whole reason the sweep has an exception in it. Losing this is
        // losing the evidence, and quietly.
        Assert.Equal([incident], MoveTimestamps());
    }

    [Fact]
    public void UnpinningReleasesWhatThePinWasHolding()
    {
        using var rec = NewRecorder();
        long incident = DaysAgo(10);
        WriteMove(1, incident);
        rec.PinPeriod(incident - 60_000, incident + 60_000, "an incident", "gm");
        SweepAndWait(rec, expectedCount: 1);
        Assert.Equal(1, MoveCount());

        var pins = rec.GetPinnedPeriods();
        Assert.Single(pins);
        Assert.True(rec.UnpinPeriod(pins[0].Id));

        SweepAndWait(rec, expectedCount: 0);
        _out.WriteLine($"after unpinning and sweeping: {MoveCount()} row(s) left");
        Assert.Equal(0, MoveCount());
    }

    [Fact]
    public void TheEdgesOfAPinnedPeriodAreInsideIt()
    {
        using var rec = NewRecorder();
        long start = DaysAgo(10);
        long end = start + 60_000;
        WriteMove(1, start);                      // exactly the first moment
        WriteMove(1, end);                        // exactly the last
        WriteMove(1, end + 1);                    // one millisecond outside

        rec.PinPeriod(start, end, "an incident", "gm");
        SweepAndWait(rec, expectedCount: 2);

        // A pin that excluded its own boundary would clip the start and end of every
        // incident it was asked to keep.
        Assert.Equal([start, end], MoveTimestamps());
    }

    // ---- while the recorder is still writing ------------------------------

    [Fact]
    public void RecordsWrittenWhileTheSweepRunsAreNotLost()
    {
        using var rec = NewRecorder();
        for (int i = 0; i < 200; i++)
            WriteMove(1, DaysAgo(10));            // a backlog for the sweep to chew on

        var world = TestHarness.CreateWorld();
        var chars = new List<Character>();
        for (int i = 0; i < 10; i++)
        {
            var c = world.CreateCharacter();
            c.IsPlayer = true;
            c.Name = $"Rec{i}";
            world.PlaceCharacter(c, new Point3D((short)(1000 + i), 1000, 0, 0));
            chars.Add(c);
        }

        // The sweep starts on a pool thread; the recorder keeps recording through it.
        rec.Tick(600_001, () => chars);
        long now = 600_001;
        for (int step = 0; step < 50; step++)
        {
            foreach (var c in chars)
                world.MoveCharacter(c, new Point3D((short)(c.X + 1), c.Y, 0, 0));
            now += 10;
            rec.Tick(now, () => chars);
        }

        // The sweep runs on a pool thread, so wait for it to finish rather than
        // assuming it beat the rest of the test - the old rows are what it is working
        // through, and nothing else touches them.
        long cutoff = DaysAgo(3);
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(15) &&
               MoveTimestamps().Count(t => t < cutoff) > 0)
        {
            Thread.Sleep(25);
        }

        rec.Dispose();     // flushes what is queued

        var timestamps = MoveTimestamps();
        int fresh = timestamps.Count(t => t >= cutoff);
        _out.WriteLine($"after a sweep over 200 old rows while recording: " +
                       $"{timestamps.Count(t => t < cutoff)} old left, {fresh} recorded during it");

        // The old ones go and the new ones stay: a sweep that took the rows being
        // written with it would lose exactly the movement somebody is watching live.
        Assert.Equal(0, timestamps.Count(t => t < cutoff));
        Assert.True(fresh > 0, "nothing was recorded while the sweep ran");
    }
}
