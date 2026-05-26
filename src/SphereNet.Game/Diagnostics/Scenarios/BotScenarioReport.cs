namespace SphereNet.Game.Diagnostics.Scenarios;

public sealed class BotScenarioReport
{
    public string ScenarioName { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int TotalBots { get; init; }
    public int ActiveAtEnd { get; init; }
    public int Disconnects { get; init; }
    public double DisconnectRate { get; init; }
    public long TotalPacketsSent { get; init; }
    public long TotalPacketsReceived { get; init; }
    public int AnomalyCount { get; init; }
    public IReadOnlyList<BotAnomaly> Anomalies { get; init; } = [];
    public bool Passed { get; init; }
    public IReadOnlyList<string> FailReasons { get; init; } = [];

    public static BotScenarioReport Generate(string scenarioName, BotEngine engine,
        TimeSpan elapsed, int totalBotsStarted)
    {
        var stats = engine.GetStats();
        int active = stats.ActiveBots;
        int disconnects = totalBotsStarted - active;
        double disconnectRate = totalBotsStarted > 0 ? (double)disconnects / totalBotsStarted : 0;

        var anomalies = new List<BotAnomaly>();
        while (engine.Anomalies.TryDequeue(out var a))
            anomalies.Add(a);

        var failReasons = new List<string>();
        if (disconnectRate > 0.02)
            failReasons.Add($"Disconnect rate {disconnectRate:P1} exceeds 2% threshold");
        if (anomalies.Any(a => a.Severity == BotAnomalySeverity.Critical))
            failReasons.Add("Critical anomaly detected");

        double anomalyRate = totalBotsStarted > 0
            ? (double)anomalies.Count / totalBotsStarted : 0;
        if (anomalyRate > 0.05)
            failReasons.Add($"Anomaly rate {anomalyRate:P1} exceeds 5% threshold");

        return new BotScenarioReport
        {
            ScenarioName = scenarioName,
            Duration = elapsed,
            TotalBots = totalBotsStarted,
            ActiveAtEnd = active,
            Disconnects = disconnects,
            DisconnectRate = disconnectRate,
            TotalPacketsSent = stats.TotalPacketsSent,
            TotalPacketsReceived = stats.TotalPacketsReceived,
            AnomalyCount = anomalies.Count,
            Anomalies = anomalies,
            Passed = failReasons.Count == 0,
            FailReasons = failReasons,
        };
    }
}
