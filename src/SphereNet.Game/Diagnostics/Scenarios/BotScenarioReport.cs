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
    /// <summary>Move requests the scenario's bots sent and the server rejected.</summary>
    public int TotalMoveRequests { get; init; }
    public int TotalMoveRejects { get; init; }
    public double MoveRejectRate => TotalMoveRequests > 0 ? (double)TotalMoveRejects / TotalMoveRequests : 0;
    /// <summary>Server tick samples recorded between scenario start and finish.</summary>
    public int TickSamples { get; init; }
    /// <summary>The performance gate's verdict. <see cref="Passed"/> is true only when
    /// the gate passed; an unmeasured run is reported as such, not as a pass.</summary>
    public BotGateResult? Gate { get; init; }
    public BotGateStatus Status => Gate?.Status ?? BotGateStatus.InsufficientData;
    public bool Passed { get; init; }
    public IReadOnlyList<string> FailReasons { get; init; } = [];

    public static BotScenarioReport Generate(string scenarioName, BotEngine engine,
        TimeSpan elapsed, int totalBotsStarted, TickHistogram? scenarioTicks = null,
        BotPerformanceGate? gate = null)
    {
        var stats = engine.GetStats();

        var anomalies = new List<BotAnomaly>();
        while (engine.Anomalies.TryDequeue(out var a))
            anomalies.Add(a);

        var (moveRequests, moveRejects) = engine.GetMoveTotals();

        return FromMeasurements(scenarioName, elapsed, totalBotsStarted, stats.ActiveBots,
            stats.TotalPacketsSent, stats.TotalPacketsReceived, anomalies,
            moveRequests, moveRejects, scenarioTicks, gate ?? new BotPerformanceGate());
    }

    /// <summary>Build the report from already-collected measurements and run the gate
    /// over them. <paramref name="scenarioTicks"/> must hold only the ticks recorded
    /// inside the scenario window.</summary>
    public static BotScenarioReport FromMeasurements(string scenarioName, TimeSpan elapsed,
        int totalBotsStarted, int activeAtEnd, long packetsSent, long packetsReceived,
        IReadOnlyList<BotAnomaly> anomalies, int moveRequests, int moveRejects,
        TickHistogram? scenarioTicks, BotPerformanceGate gate)
    {
        int disconnects = Math.Max(0, totalBotsStarted - activeAtEnd);
        double disconnectRate = totalBotsStarted > 0 ? (double)disconnects / totalBotsStarted : 0;

        var measured = new BotScenarioReport
        {
            ScenarioName = scenarioName,
            Duration = elapsed,
            TotalBots = totalBotsStarted,
            ActiveAtEnd = activeAtEnd,
            Disconnects = disconnects,
            DisconnectRate = disconnectRate,
            TotalPacketsSent = packetsSent,
            TotalPacketsReceived = packetsReceived,
            AnomalyCount = anomalies.Count,
            Anomalies = anomalies,
            TotalMoveRequests = moveRequests,
            TotalMoveRejects = moveRejects,
            TickSamples = scenarioTicks?.Count ?? 0,
        };

        var result = gate.Evaluate(measured, scenarioTicks);

        var failReasons = new List<string>();
        foreach (var check in result.Checks)
        {
            if (check.Passed) continue;
            string kind = !check.Measured || check.Insufficient ? "NOT MEASURED" : "FAILED";
            failReasons.Add($"{kind}: {check.Name} {check.Actual} (threshold {check.Threshold})");
        }

        return new BotScenarioReport
        {
            ScenarioName = measured.ScenarioName,
            Duration = measured.Duration,
            TotalBots = measured.TotalBots,
            ActiveAtEnd = measured.ActiveAtEnd,
            Disconnects = measured.Disconnects,
            DisconnectRate = measured.DisconnectRate,
            TotalPacketsSent = measured.TotalPacketsSent,
            TotalPacketsReceived = measured.TotalPacketsReceived,
            AnomalyCount = measured.AnomalyCount,
            Anomalies = measured.Anomalies,
            TotalMoveRequests = measured.TotalMoveRequests,
            TotalMoveRejects = measured.TotalMoveRejects,
            TickSamples = measured.TickSamples,
            Gate = result,
            Passed = result.Passed,
            FailReasons = failReasons,
        };
    }
}
