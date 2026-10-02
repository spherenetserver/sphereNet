using System.Text.Json;
using System.Text.Json.Nodes;

namespace SphereNet.LoadTest;

/// <summary>One server start under one (workers, bots) pair.</summary>
internal sealed class RunResult
{
    public int Workers { get; init; }
    public int Bots { get; init; }
    public bool Valid { get; set; }
    public string ScenarioHash { get; set; } = "";
    /// <summary>The attempt this run replaced after it broke a limit, if any.</summary>
    public RunResult? FirstAttempt { get; set; }
    public List<string> Problems { get; } = [];
    /// <summary>Flat metric name -> value, the names the baseline limits use.</summary>
    public SortedDictionary<string, double> Metrics { get; } = new(StringComparer.Ordinal);
    /// <summary>The server's own [perf_window] report, verbatim.</summary>
    public JsonNode? Server { get; set; }
    public List<string> ServerErrorLines { get; set; } = [];
}

internal sealed class CheckResult
{
    public int Workers { get; init; }
    public int Bots { get; init; }
    public string Metric { get; init; } = "";
    public double? Value { get; init; }
    public double? Max { get; init; }
    public double? Min { get; init; }
    public bool Pass { get; init; }
}

/// <summary>
/// loadtest-baseline.json: per bot count, a limit for each metric. The limits are
/// measured numbers with headroom for slower and noisier CI hardware, not targets; a
/// run that breaks one is a regression to look at. A metric the run did not produce
/// fails the check rather than passing it - missing data is not a good result.
/// </summary>
internal sealed class Baseline
{
    public required JsonObject Root { get; init; }

    public static Baseline Load(string path) =>
        new() { Root = JsonNode.Parse(File.ReadAllText(path))!.AsObject() };

    public List<CheckResult> Check(IEnumerable<RunResult> runs)
    {
        var checks = new List<CheckResult>();
        var profiles = Root["profiles"]?.AsArray() ?? [];
        foreach (var run in runs)
        {
            var profile = profiles.FirstOrDefault(p => p?["bots"]?.GetValue<int>() == run.Bots);
            if (profile == null)
            {
                checks.Add(new CheckResult { Workers = run.Workers, Bots = run.Bots, Metric = "(no baseline profile for this bot count)", Pass = false });
                continue;
            }
            checks.Add(new CheckResult { Workers = run.Workers, Bots = run.Bots, Metric = "run.valid", Value = run.Valid ? 1 : 0, Min = 1, Pass = run.Valid });
            foreach (var (metric, limitNode) in profile["limits"]!.AsObject())
            {
                double? max = limitNode?["max"]?.GetValue<double>();
                double? min = limitNode?["min"]?.GetValue<double>();
                bool has = run.Metrics.TryGetValue(metric, out double value);
                bool pass = has && (max == null || value <= max) && (min == null || value >= min);
                checks.Add(new CheckResult
                {
                    Workers = run.Workers, Bots = run.Bots, Metric = metric,
                    Value = has ? value : null, Max = max, Min = min, Pass = pass,
                });
            }
        }
        return checks;
    }
}

internal static class Stats
{
    /// <summary>Nearest-rank percentile of microsecond samples, in ms.</summary>
    public static double PercentileMs(int[] sortedUs, double p)
    {
        if (sortedUs.Length == 0) return 0;
        int index = (int)Math.Ceiling(p * sortedUs.Length) - 1;
        return Math.Round(sortedUs[Math.Clamp(index, 0, sortedUs.Length - 1)] / 1000.0, 3);
    }

    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return Math.Round(sorted[Math.Clamp(index, 0, sorted.Length - 1)], 3);
    }

    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
}
