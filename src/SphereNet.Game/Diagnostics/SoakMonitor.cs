using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// The long-run half of a soak scenario: while the bots play, it samples memory every
/// minute, saves the world on a fixed interval and audits the world's consistency
/// (<see cref="WorldInvariantAuditor"/>) after each save. At the end it fits a line
/// through the memory samples, so steady growth shows as megabytes per hour, and fails
/// the run on growth above the limit, on any consistency anomaly and on any failed save.
///
/// Samples and checkpoints go to logs/soak-*.csv as they are taken, so a run that is
/// stopped or crashes still leaves its trend behind.
/// </summary>
public sealed class SoakMonitor
{
    public sealed record Sample(double Minutes, double HeapMb, double WorkingSetMb, int Gen2);
    public sealed record Checkpoint(double Minutes, int Anomalies, string FirstAnomaly);

    private readonly ILogger _logger;
    private readonly TimeSpan _saveInterval;
    private readonly TimeSpan _sampleInterval;
    private readonly Func<IReadOnlyList<string>>? _audit;
    private readonly Action? _requestSave;
    private readonly Func<(int Saves, int Failures)>? _saveStats;
    private readonly TimeSpan _settleAfterSave;
    private readonly List<Sample> _samples = [];
    private readonly List<Checkpoint> _checkpoints = [];
    private readonly Stopwatch _clock = new();
    private (int Saves, int Failures) _savesAtStart;
    private StreamWriter? _csv;
    private Task? _loop;
    private CancellationTokenSource? _cts;

    public string? CsvPath { get; private set; }

    public SoakMonitor(ILogger logger, TimeSpan saveInterval, TimeSpan sampleInterval,
        Func<IReadOnlyList<string>>? audit, Action? requestSave, Func<(int Saves, int Failures)>? saveStats,
        TimeSpan? settleAfterSave = null)
    {
        _settleAfterSave = settleAfterSave ?? TimeSpan.FromSeconds(10);
        _logger = logger;
        _saveInterval = saveInterval;
        _sampleInterval = sampleInterval;
        _audit = audit;
        _requestSave = requestSave;
        _saveStats = saveStats;
    }

    public void Start(string? logDirectory, CancellationToken ct)
    {
        _savesAtStart = _saveStats?.Invoke() ?? (0, 0);
        _clock.Start();
        try
        {
            string dir = logDirectory ?? Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            CsvPath = Path.Combine(dir, $"soak-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            _csv = new StreamWriter(CsvPath) { AutoFlush = true };
            _csv.WriteLine("minutes,kind,heapMb,workingSetMb,gen2,anomalies,detail");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[soak] could not open the CSV log");
        }
        TakeSample();
        // Its own token as well: a scenario that runs its full length ends without
        // cancelling the scenario token, and the monitor must stop with it.
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _loop = Task.Run(() => LoopAsync(token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var nextSave = _saveInterval;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(_sampleInterval, ct);
                TakeSample();
                if (_saveInterval > TimeSpan.Zero && _clock.Elapsed >= nextSave)
                {
                    nextSave += _saveInterval;
                    _requestSave?.Invoke();
                    // Let the save land before looking at the world it wrote.
                    await Task.Delay(_settleAfterSave, ct);
                    RunAudit();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[soak] monitor failed");
        }
    }

    private void TakeSample()
    {
        using var process = Process.GetCurrentProcess();
        var s = new Sample(
            _clock.Elapsed.TotalMinutes,
            // The heap as the last collection left it: steadier than the live size,
            // which saw-tooths between collections.
            GC.GetGCMemoryInfo().HeapSizeBytes / (1024.0 * 1024.0),
            process.WorkingSet64 / (1024.0 * 1024.0),
            GC.CollectionCount(2));
        lock (_samples)
            _samples.Add(s);
        Write($"{F(s.Minutes)},sample,{F(s.HeapMb)},{F(s.WorkingSetMb)},{s.Gen2},,");
        _logger.LogInformation("[soak] t={Min:F0}min heap={Heap:F0}MB ws={Ws:F0}MB gen2={Gen2}",
            s.Minutes, s.HeapMb, s.WorkingSetMb, s.Gen2);
    }

    private void RunAudit()
    {
        if (_audit == null)
            return;
        IReadOnlyList<string> anomalies;
        try { anomalies = _audit(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[soak] world audit failed");
            return;
        }
        var cp = new Checkpoint(_clock.Elapsed.TotalMinutes, anomalies.Count,
            anomalies.Count > 0 ? anomalies[0] : "");
        lock (_checkpoints)
            _checkpoints.Add(cp);
        Write($"{F(cp.Minutes)},audit,,,,{cp.Anomalies},\"{cp.FirstAnomaly.Replace("\"", "'")}\"");
        if (anomalies.Count == 0)
        {
            _logger.LogInformation("[soak] t={Min:F0}min save + audit: clean", cp.Minutes);
            return;
        }
        _logger.LogWarning("[soak] t={Min:F0}min audit found {Count} anomalies, first: {First}",
            cp.Minutes, anomalies.Count, cp.FirstAnomaly);
        // The same defect on many objects reads as one line per kind: drop the uid.
        foreach (var group in anomalies
                     .GroupBy(a => System.Text.RegularExpressions.Regex.Replace(a, @"0x[0-9A-F]{8}\s*", ""))
                     .OrderByDescending(g => g.Count()).Take(5))
            _logger.LogWarning("[soak]   {Count} x {Anomaly}", group.Count(), group.Key);
    }

    /// <summary>Stop sampling, take a last sample and audit, and judge the run.</summary>
    public SoakResult Finish(double maxHeapGrowthMbPerHour)
    {
        _clock.Stop();
        try { _cts?.Cancel(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _cts?.Dispose();
        TakeSample();
        RunAudit();
        var saves = _saveStats?.Invoke() ?? (0, 0);

        Sample[] samples;
        Checkpoint[] checkpoints;
        lock (_samples) samples = [.. _samples];
        lock (_checkpoints) checkpoints = [.. _checkpoints];

        var result = SoakResult.From(samples, checkpoints,
            saves.Saves - _savesAtStart.Saves, saves.Failures - _savesAtStart.Failures,
            maxHeapGrowthMbPerHour, CsvPath);
        Write($"{F(_clock.Elapsed.TotalMinutes)},summary,,,,{result.TotalAnomalies},\"{string.Join("; ", result.FailReasons)}\"");
        _csv?.Dispose();
        _csv = null;
        return result;
    }

    private void Write(string line)
    {
        try { lock (_samples) _csv?.WriteLine(line); }
        catch { }
    }

    private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
}

/// <summary>What a soak run measured and whether it passed.</summary>
public sealed class SoakResult
{
    public double DurationMinutes { get; init; }
    public int Samples { get; init; }
    /// <summary>Managed heap growth from a least-squares line through the samples taken
    /// after the warm-up (the first fifth of the run, at least 5 minutes).</summary>
    public double HeapGrowthMbPerHour { get; init; }
    public double WorkingSetGrowthMbPerHour { get; init; }
    public double HeapStartMb { get; init; }
    public double HeapEndMb { get; init; }
    public int Audits { get; init; }
    public int TotalAnomalies { get; init; }
    public int Saves { get; init; }
    public int SaveFailures { get; init; }
    public string? CsvPath { get; init; }
    public IReadOnlyList<string> FailReasons { get; init; } = [];
    public bool Passed => FailReasons.Count == 0;

    public static SoakResult From(IReadOnlyList<SoakMonitor.Sample> samples,
        IReadOnlyList<SoakMonitor.Checkpoint> checkpoints, int saves, int saveFailures,
        double maxHeapGrowthMbPerHour, string? csvPath)
    {
        double duration = samples.Count > 0 ? samples[^1].Minutes : 0;
        double warmup = Math.Max(5, duration / 5);
        var steady = samples.Where(s => s.Minutes >= warmup).ToList();
        double heapSlope = Slope(steady, s => s.HeapMb);
        double wsSlope = Slope(steady, s => s.WorkingSetMb);

        var reasons = new List<string>();
        int anomalies = checkpoints.Sum(c => c.Anomalies);
        if (anomalies > 0)
            reasons.Add($"world audit found {anomalies} anomalies ({checkpoints.First(c => c.Anomalies > 0).FirstAnomaly})");
        if (saveFailures > 0)
            reasons.Add($"{saveFailures} of {saves} saves failed");
        if (steady.Count < 3)
            reasons.Add($"NOT MEASURED: memory trend needs a longer run (only {steady.Count} samples after warm-up)");
        else if (heapSlope > maxHeapGrowthMbPerHour)
            reasons.Add($"managed heap grows {heapSlope:F1} MB/h (limit {maxHeapGrowthMbPerHour:F0})");

        return new SoakResult
        {
            DurationMinutes = duration,
            Samples = samples.Count,
            HeapGrowthMbPerHour = heapSlope,
            WorkingSetGrowthMbPerHour = wsSlope,
            HeapStartMb = samples.Count > 0 ? samples[0].HeapMb : 0,
            HeapEndMb = samples.Count > 0 ? samples[^1].HeapMb : 0,
            Audits = checkpoints.Count,
            TotalAnomalies = anomalies,
            Saves = saves,
            SaveFailures = saveFailures,
            CsvPath = csvPath,
            FailReasons = reasons,
        };
    }

    /// <summary>Least-squares slope of <paramref name="value"/> over time, per hour.</summary>
    public static double Slope(IReadOnlyList<SoakMonitor.Sample> samples, Func<SoakMonitor.Sample, double> value)
    {
        if (samples.Count < 2)
            return 0;
        double n = samples.Count;
        double meanX = samples.Average(s => s.Minutes);
        double meanY = samples.Average(value);
        double num = 0, den = 0;
        foreach (var s in samples)
        {
            double dx = s.Minutes - meanX;
            num += dx * (value(s) - meanY);
            den += dx * dx;
        }
        return den == 0 ? 0 : num / den * 60.0;
    }
}
