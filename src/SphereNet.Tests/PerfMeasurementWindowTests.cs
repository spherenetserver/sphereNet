using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Game.Accounts;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.Server.Admin;

namespace SphereNet.Tests;

/// <summary>
/// The load runner's measurement window (console PERF MARK / REPORT): every number in
/// a report covers the span since MARK and nothing before it, and an empty window
/// reports zeros rather than inventing a distribution.
/// </summary>
public sealed class PerfMeasurementWindowTests
{
    [Fact]
    public void TickPercentilesComeFromTheWindowsOwnSamples()
    {
        var window = new PerfMeasurementWindow(startTick: 100);
        for (int i = 1; i <= 100; i++)
            window.RecordTick(i * 1000L); // 1..100 ms

        var report = window.BuildReport(currentTick: 200, workerCount: 4, multicoreEnabled: true,
            playingClients: 3, chars: 10, items: 20);

        Assert.Equal(100, report.Ticks);
        Assert.Equal(100, report.Tick.Samples);
        Assert.Equal(50.0, report.Tick.P50Ms);
        Assert.Equal(95.0, report.Tick.P95Ms);
        Assert.Equal(99.0, report.Tick.P99Ms);
        Assert.Equal(100.0, report.Tick.MaxMs);
        Assert.Equal(50.5, report.Tick.AvgMs);
        Assert.Equal(4, report.WorkerCount);
        Assert.Equal(3, report.PlayingClients);
    }

    [Fact]
    public void AnEmptyWindowReportsNoSamplesNotAFastServer()
    {
        var report = new PerfMeasurementWindow(0).BuildReport(0, 1, true, 0, 0, 0);
        Assert.Equal(0, report.Tick.Samples);
        Assert.Equal(0, report.Tick.P99Ms);
        Assert.Equal(0, report.Save.Count);
        Assert.Equal(0, report.NpcBudget.Ticks);
    }

    [Fact]
    public void LoopSavesAndFallbacksAreKeptAsWorstCases()
    {
        var window = new PerfMeasurementWindow(0);
        window.RecordLoopIteration(2_000);
        window.RecordLoopIteration(60_000);
        window.RecordLoopIteration(150_000);
        window.RecordSave(captureMs: 20, mainThreadMs: 30);
        window.RecordSave(captureMs: 12, mainThreadMs: 45);
        window.RecordMulticoreFallback();

        var report = window.BuildReport(0, 1, false, 0, 0, 0);
        Assert.Equal(3, report.Loop.Iterations);
        Assert.Equal(150.0, report.Loop.MaxMs);
        Assert.Equal(2, report.Loop.Over50Ms);
        Assert.Equal(1, report.Loop.Over100Ms);
        Assert.Equal(2, report.Save.Count);
        Assert.Equal(20.0, report.Save.MaxCaptureMs);
        Assert.Equal(45.0, report.Save.MaxMainThreadMs);
        Assert.Equal(1, report.MulticoreFallbacks);
        Assert.False(report.MulticoreEnabled);
    }

    [Fact]
    public void TheNpcBudgetCountsOnlyWhatWasRecordedIntoTheWindow()
    {
        var window = new PerfMeasurementWindow(0);
        var due = new List<Character>();
        for (int i = 0; i < 5; i++)
            due.Add(new Character { NextNpcActionTime = 1000 });
        window.NpcBudget.RecordBatch(due, limit: 3, nowMs: 1250);

        var report = window.BuildReport(0, 1, true, 0, 0, 0);
        Assert.Equal(1, report.NpcBudget.Ticks);
        Assert.Equal(5, report.NpcBudget.Due);
        Assert.Equal(2, report.NpcBudget.Deferred);
        Assert.Equal(250, report.NpcBudget.OldestWaitMs);
        Assert.Equal(3, report.NpcBudget.LatenessSamples);
    }

    [Fact]
    public void ThePerfVerbIsHandedToTheServerWithItsArgument()
    {
        var processor = new AdminCommandProcessor(new GameWorld(NullLoggerFactory.Instance),
            new AccountManager(NullLoggerFactory.Instance), new SphereConfig(), () => 0,
            NullLoggerFactory.Instance);
        var lines = new List<string>();

        // Not wired (the telnet console): answered, not silently dropped.
        processor.ProcessCommand("PERF MARK", lines.Add);
        Assert.Contains(lines, l => l.Contains("only available", StringComparison.Ordinal));

        string? seen = null;
        processor.OnPerfRequested += (sub, _) => seen = sub;
        processor.ProcessCommand("perf report", lines.Add);
        Assert.Equal("report", seen);
    }

    [Fact]
    public void BotSpawnSeedIsStableAcrossProcessesAndCase()
    {
        // A fixed value: string.GetHashCode would differ between processes.
        Assert.Equal(BotEngine.StableSpawnSeed("spherenetBot0001"), BotEngine.StableSpawnSeed("SPHERENETBOT0001"));
        Assert.Equal(unchecked((int)2166136261u), BotEngine.StableSpawnSeed(""));
        Assert.NotEqual(BotEngine.StableSpawnSeed("spherenetBot0001"), BotEngine.StableSpawnSeed("spherenetBot0002"));
    }
}
