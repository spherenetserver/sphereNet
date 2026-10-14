using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Diagnostics.Behaviors;
using SphereNet.Game.Diagnostics.Scenarios;

namespace SphereNet.Tests;

/// <summary>
/// The soak scenario's judging half: the memory trend is a least-squares slope per
/// hour over the samples after the warm-up, and the run fails on growth over the
/// limit, on any audit anomaly and on any failed save. The monitor saves and audits
/// on its own schedule and leaves a CSV of what it saw.
/// </summary>
public sealed class SoakScenarioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spn_soak_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static SoakMonitor.Sample S(double minutes, double heapMb) => new(minutes, heapMb, heapMb * 2, 0);

    [Fact]
    public void TheSlopeIsMegabytesPerHour()
    {
        // 10 MB more every hour, sampled each minute, with saw-tooth noise.
        var samples = Enumerable.Range(0, 120)
            .Select(m => S(m, 200 + m / 6.0 + (m % 2 == 0 ? 3 : -3)))
            .ToList();

        Assert.InRange(SoakResult.Slope(samples, s => s.HeapMb), 9.0, 11.0);
    }

    [Fact]
    public void AFlatRunPassesAndAGrowingOneFails()
    {
        var flat = Enumerable.Range(0, 120).Select(m => S(m, 300)).ToList();
        var growing = Enumerable.Range(0, 120).Select(m => S(m, 300 + m * 2.0)).ToList();

        Assert.True(SoakResult.From(flat, [], 8, 0, 64, null).Passed);

        var failed = SoakResult.From(growing, [], 8, 0, 64, null);
        Assert.False(failed.Passed);
        Assert.Contains(failed.FailReasons, r => r.Contains("managed heap grows"));
    }

    [Fact]
    public void AnAnomalyOrAFailedSaveFailsTheRun()
    {
        var flat = Enumerable.Range(0, 60).Select(m => S(m, 300)).ToList();

        var anomalies = SoakResult.From(flat, [new(30, 2, "[OrphanContained] 0x40000001 ...")], 4, 0, 64, null);
        var saves = SoakResult.From(flat, [], 4, 1, 64, null);

        Assert.Contains(anomalies.FailReasons, r => r.Contains("2 anomalies") && r.Contains("OrphanContained"));
        Assert.Contains(saves.FailReasons, r => r.Contains("1 of 4 saves failed"));
    }

    [Fact]
    public void ATooShortRunSaysItIsNotMeasured()
    {
        var r = SoakResult.From([S(0, 300), S(1, 300)], [], 0, 0, 64, null);

        Assert.Contains(r.FailReasons, x => x.StartsWith("NOT MEASURED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMonitorSavesAuditsAndWritesItsCsv()
    {
        int saves = 0, audits = 0;
        var monitor = new SoakMonitor(NullLogger.Instance,
            saveInterval: TimeSpan.FromMilliseconds(150), sampleInterval: TimeSpan.FromMilliseconds(50),
            audit: () => { Interlocked.Increment(ref audits); return []; },
            requestSave: () => Interlocked.Increment(ref saves),
            saveStats: () => (saves, 0),
            settleAfterSave: TimeSpan.FromMilliseconds(10));
        using var cts = new CancellationTokenSource();

        monitor.Start(_dir, cts.Token);
        for (int i = 0; i < 100 && Volatile.Read(ref audits) < 2; i++)
            await Task.Delay(50);
        cts.Cancel();
        var result = monitor.Finish(64);

        Assert.True(saves >= 2, $"saves {saves}");
        Assert.True(result.Audits >= 2);
        Assert.Equal(0, result.TotalAnomalies);
        Assert.NotNull(monitor.CsvPath);
        string csv = File.ReadAllText(monitor.CsvPath!);
        Assert.StartsWith("minutes,kind,heapMb", csv);
        Assert.Contains(",audit,", csv);
        Assert.Contains(",summary,", csv);
    }

    [Fact]
    public void TheSoakScenarioUsesEveryBotAndTrades()
    {
        var scenario = new SoakScenario();
        var distribution = scenario.GetBotDistribution(100);

        Assert.Equal(100, distribution.Sum(d => d.count));
        Assert.Contains(distribution, d => d.behavior is TradeBot);
        Assert.Contains(distribution, d => d.behavior is MageBot);
        Assert.Contains(BotEngine.AvailableScenarios, s => s.Name == "Soak");
    }

    [Fact]
    public void ATradeAcceptIsWhatTheServerReads()
    {
        byte[] packet = BotPacketBuilder.BuildTradeAccept(0x40001234, accept: true);

        Assert.Equal(new byte[] { 0x6F, 0x00, 0x0C, 0x02, 0x40, 0x00, 0x12, 0x34, 0x00, 0x00, 0x00, 0x01 }, packet);
    }
}
