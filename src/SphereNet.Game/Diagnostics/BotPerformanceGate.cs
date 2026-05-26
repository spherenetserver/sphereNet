using SphereNet.Game.Diagnostics.Scenarios;

namespace SphereNet.Game.Diagnostics;

public sealed class BotPerformanceGate
{
    public double MaxDisconnectRate { get; set; } = 0.02;
    public int MaxP95TickMs { get; set; } = 50;
    public int MaxP99TickMs { get; set; } = 100;
    public double MaxMoveRejectRate { get; set; } = 0.10;
    public double MaxAnomalyRate { get; set; } = 0.05;
    public int MaxCriticalAnomalies { get; set; } = 0;

    public BotGateResult Evaluate(BotScenarioReport report, TickHistogram? tickStats)
    {
        var checks = new List<BotGateCheck>();

        checks.Add(new BotGateCheck
        {
            Name = "Disconnect Rate",
            Passed = report.DisconnectRate <= MaxDisconnectRate,
            Actual = $"{report.DisconnectRate:P1}",
            Threshold = $"<= {MaxDisconnectRate:P0}",
        });

        if (tickStats != null)
        {
            int p95 = tickStats.P95;
            int p99 = tickStats.P99;

            checks.Add(new BotGateCheck
            {
                Name = "p95 Server Tick",
                Passed = p95 <= MaxP95TickMs,
                Actual = $"{p95}ms",
                Threshold = $"<= {MaxP95TickMs}ms",
            });

            checks.Add(new BotGateCheck
            {
                Name = "p99 Server Tick",
                Passed = p99 <= MaxP99TickMs,
                Actual = $"{p99}ms",
                Threshold = $"<= {MaxP99TickMs}ms",
            });
        }

        double anomalyRate = report.TotalBots > 0
            ? (double)report.AnomalyCount / report.TotalBots : 0;

        checks.Add(new BotGateCheck
        {
            Name = "Anomaly Rate",
            Passed = anomalyRate <= MaxAnomalyRate,
            Actual = $"{anomalyRate:P1}",
            Threshold = $"<= {MaxAnomalyRate:P0}",
        });

        int criticals = report.Anomalies.Count(a => a.Severity == BotAnomalySeverity.Critical);
        checks.Add(new BotGateCheck
        {
            Name = "Critical Anomalies",
            Passed = criticals <= MaxCriticalAnomalies,
            Actual = $"{criticals}",
            Threshold = $"<= {MaxCriticalAnomalies}",
        });

        return new BotGateResult
        {
            Passed = checks.All(c => c.Passed),
            Checks = checks,
        };
    }
}

public sealed class BotGateResult
{
    public bool Passed { get; init; }
    public IReadOnlyList<BotGateCheck> Checks { get; init; } = [];
}

public sealed class BotGateCheck
{
    public string Name { get; init; } = string.Empty;
    public bool Passed { get; init; }
    public string Actual { get; init; } = string.Empty;
    public string Threshold { get; init; } = string.Empty;
}
