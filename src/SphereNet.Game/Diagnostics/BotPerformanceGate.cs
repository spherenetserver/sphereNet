using SphereNet.Game.Diagnostics.Scenarios;

namespace SphereNet.Game.Diagnostics;

/// <summary>
/// Pass/fail gate for a bot scenario run.
///
/// The gate has three outcomes, not two. A run with no tick samples, no bots or
/// too few movement samples did not measure what the gate exists to judge, and
/// reporting that as a pass is how a scenario that never reached the server read as
/// a green load test. Such a run is <see cref="BotGateStatus.InsufficientData"/>;
/// a measured check that exceeds its threshold is <see cref="BotGateStatus.Failed"/>
/// whatever else is missing.
/// </summary>
public sealed class BotPerformanceGate
{
    public double MaxDisconnectRate { get; set; } = 0.02;
    public int MaxP95TickMs { get; set; } = 50;
    public int MaxP99TickMs { get; set; } = 100;
    public double MaxMoveRejectRate { get; set; } = 0.10;
    public double MaxAnomalyRate { get; set; } = 0.05;
    public int MaxCriticalAnomalies { get; set; } = 0;

    /// <summary>Bots the scenario must have started for any rate to mean something.</summary>
    public int MinBots { get; set; } = 10;
    /// <summary>Move requests needed before the reject rate is judged.</summary>
    public int MinMoveSamples { get; set; } = 100;
    /// <summary>Server tick samples (inside the scenario window) needed before the
    /// tick percentiles are judged.</summary>
    public int MinTickSamples { get; set; } = 50;

    public BotGateResult Evaluate(BotScenarioReport report, TickHistogram? tickStats)
    {
        var checks = new List<BotGateCheck>();
        bool enoughBots = report.TotalBots >= MinBots;

        checks.Add(new BotGateCheck
        {
            Name = "Bot Count",
            Measured = true,
            Passed = enoughBots,
            Insufficient = !enoughBots,
            Actual = $"{report.TotalBots}",
            Threshold = $">= {MinBots}",
        });

        if (enoughBots)
        {
            checks.Add(new BotGateCheck
            {
                Name = "Disconnect Rate",
                Measured = true,
                Passed = report.DisconnectRate <= MaxDisconnectRate,
                Actual = $"{report.DisconnectRate:P1}",
                Threshold = $"<= {MaxDisconnectRate:P0}",
            });
        }
        else
        {
            checks.Add(NotMeasured("Disconnect Rate", $"<= {MaxDisconnectRate:P0}", "too few bots"));
        }

        int tickSamples = tickStats?.Count ?? 0;
        if (tickStats != null && tickSamples >= MinTickSamples)
        {
            checks.Add(TickCheck("p95 Server Tick", tickStats.GetPercentile(0.95), MaxP95TickMs));
            checks.Add(TickCheck("p99 Server Tick", tickStats.GetPercentile(0.99), MaxP99TickMs));
        }
        else
        {
            string why = tickStats == null ? "no tick histogram"
                : $"{tickSamples} tick samples (< {MinTickSamples})";
            checks.Add(NotMeasured("p95 Server Tick", $"<= {MaxP95TickMs}ms", why));
            checks.Add(NotMeasured("p99 Server Tick", $"<= {MaxP99TickMs}ms", why));
        }

        if (report.TotalMoveRequests >= MinMoveSamples)
        {
            double rejectRate = (double)report.TotalMoveRejects / report.TotalMoveRequests;
            checks.Add(new BotGateCheck
            {
                Name = "Move Reject Rate",
                Measured = true,
                Passed = rejectRate <= MaxMoveRejectRate,
                Actual = $"{rejectRate:P1} ({report.TotalMoveRejects}/{report.TotalMoveRequests})",
                Threshold = $"<= {MaxMoveRejectRate:P0}",
            });
        }
        else
        {
            checks.Add(NotMeasured("Move Reject Rate", $"<= {MaxMoveRejectRate:P0}",
                $"{report.TotalMoveRequests} move samples (< {MinMoveSamples})"));
        }

        if (enoughBots)
        {
            double anomalyRate = (double)report.AnomalyCount / report.TotalBots;
            checks.Add(new BotGateCheck
            {
                Name = "Anomaly Rate",
                Measured = true,
                Passed = anomalyRate <= MaxAnomalyRate,
                Actual = $"{anomalyRate:P1}",
                Threshold = $"<= {MaxAnomalyRate:P0}",
            });
        }
        else
        {
            checks.Add(NotMeasured("Anomaly Rate", $"<= {MaxAnomalyRate:P0}", "too few bots"));
        }

        // A critical anomaly is a failure no matter how small the run was.
        int criticals = report.Anomalies.Count(a => a.Severity == BotAnomalySeverity.Critical);
        checks.Add(new BotGateCheck
        {
            Name = "Critical Anomalies",
            Measured = true,
            Passed = criticals <= MaxCriticalAnomalies,
            Actual = $"{criticals}",
            Threshold = $"<= {MaxCriticalAnomalies}",
        });

        BotGateStatus status;
        if (checks.Any(c => c.Measured && !c.Passed && !c.Insufficient))
            status = BotGateStatus.Failed;
        else if (checks.Any(c => !c.Measured || c.Insufficient))
            status = BotGateStatus.InsufficientData;
        else
            status = BotGateStatus.Passed;

        return new BotGateResult
        {
            Status = status,
            Checks = checks,
        };
    }

    /// <summary>A percentile in the histogram's overflow is only known to lie between
    /// the bucket range and the observed maximum. It passes only when even that
    /// maximum is within the threshold: an overflow sample above the threshold fails
    /// the check instead of being read as the bucket ceiling.</summary>
    private static BotGateCheck TickCheck(string name, TickPercentile p, int thresholdMs) => new()
    {
        Name = name,
        Measured = true,
        Passed = (p.IsOverflow ? p.MaxMs : p.ValueMs) <= thresholdMs,
        Actual = p.ToString(),
        Threshold = $"<= {thresholdMs}ms",
    };

    private static BotGateCheck NotMeasured(string name, string threshold, string why) => new()
    {
        Name = name,
        Measured = false,
        Passed = false,
        Actual = $"not measured ({why})",
        Threshold = threshold,
    };
}

public enum BotGateStatus
{
    Passed,
    Failed,
    /// <summary>The run did not produce enough data to judge: not a pass.</summary>
    InsufficientData,
}

public sealed class BotGateResult
{
    public BotGateStatus Status { get; init; } = BotGateStatus.InsufficientData;
    /// <summary>True only when every check was measured and within threshold.</summary>
    public bool Passed => Status == BotGateStatus.Passed;
    public IReadOnlyList<BotGateCheck> Checks { get; init; } = [];
}

public sealed class BotGateCheck
{
    public string Name { get; init; } = string.Empty;
    /// <summary>False when the run produced no data for this check.</summary>
    public bool Measured { get; init; } = true;
    /// <summary>A measured minimum-sample requirement that was not met: the run is
    /// too small to judge, which is not the same as a threshold failure.</summary>
    public bool Insufficient { get; init; }
    public bool Passed { get; init; }
    public string Actual { get; init; } = string.Empty;
    public string Threshold { get; init; } = string.Empty;
}
