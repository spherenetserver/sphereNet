using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Server.Recording;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 12 / M2 — StateRecorder shared one SqliteConnection across the flush thread,
/// the ThreadPool cleanup, and caller-thread pin/query calls. Microsoft.Data.Sqlite
/// connections are not thread-safe, so concurrent use produced sporadic native
/// "misuse" errors. Now every operation off the flush thread uses its own
/// connection (WAL + busy_timeout coordinates them), so flush, cleanup, pins,
/// shares and queries can run at the same time without SQLite errors.
/// </summary>
public sealed class StateRecorderConcurrencyTests
{
    private static void CleanupDbFiles(string dbPath)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(dbPath + suffix); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task ConcurrentFlushCleanupPinQuery_ProduceNoSqliteErrors()
    {
        string dbPath = Path.Combine(Path.GetTempPath(), $"sphnet_rec_{Guid.NewGuid():N}.db");
        var rec = new StateRecorder(dbPath, NullLogger.Instance, playersOnly: false, moveScanMs: 1, snapshotMs: 1);
        rec.Initialize();
        try
        {
            var world = TestHarness.CreateWorld();
            var chars = new List<Character>();
            for (int i = 0; i < 20; i++)
            {
                var c = world.CreateCharacter();
                c.IsPlayer = true;
                c.Name = $"Rec{i}";
                world.PlaceCharacter(c, new Point3D((short)(1000 + i), 1000, 0, 0));
                chars.Add(c);
            }
            IEnumerable<Character> Provider() => chars;

            var errors = new ConcurrentBag<Exception>();
            var tasks = new List<Task>();

            // Flush driver: scans/snapshots fire the flush thread every tick, and
            // advancing the clock crosses the cleanup interval (ThreadPool cleanup).
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    long now = 0;
                    for (int i = 0; i < 300; i++)
                    {
                        now += 5000;
                        rec.Tick(now, Provider);
                    }
                }
                catch (Exception e) { errors.Add(e); }
            }));

            // Caller-thread DB operations, concurrent with the flush writer.
            for (int t = 0; t < 4; t++)
            {
                int tid = t;
                tasks.Add(Task.Run(() =>
                {
                    try
                    {
                        for (int i = 0; i < 200; i++)
                        {
                            rec.PinPeriod(i, i + 100, $"pin{tid}", "admin");
                            _ = rec.GetPinnedPeriods();
                            rec.ShareView((uint)(1000 + i), i, i + 100, $"sv{tid}", "admin");
                            _ = rec.GetSharedViews();
                            _ = rec.GetRecordedCharacters(limit: 10);
                            _ = rec.FindCharUidByName($"Rec{i % 20}");
                        }
                    }
                    catch (Exception e) { errors.Add(e); }
                }));
            }

            await Task.WhenAll(tasks);
            Assert.Empty(errors); // no SQLite "misuse"/transaction errors under concurrency

            // Sanity: the writes actually landed and are queryable.
            Assert.NotEmpty(rec.GetPinnedPeriods());
            Assert.NotEmpty(rec.GetRecordedCharacters(limit: 30));
        }
        finally
        {
            rec.Dispose();
            CleanupDbFiles(dbPath);
        }
    }
}
