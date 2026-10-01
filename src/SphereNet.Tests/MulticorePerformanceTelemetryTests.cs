using Microsoft.Extensions.Logging.Abstractions;
using SphereNet.Core.Configuration;
using SphereNet.Core.Types;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Diagnostics.Scenarios;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scheduling;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// A tick histogram must not hide samples above its range: a percentile that lands
/// there is reported as overflow (a lower bound with the observed maximum), never as
/// the bucket ceiling.
/// </summary>
public sealed class TickHistogramOverflowTests
{
    [Fact]
    public void SamplesAboveTheRangeAreOverflowNotTheLastBucket()
    {
        var h = new TickHistogram(500, 1);
        for (int i = 0; i < 100; i++) h.Record(1000);

        Assert.Equal(100, h.OverflowCount);
        Assert.Equal(1000, h.MaxMs);

        var p95 = h.GetPercentile(0.95);
        Assert.True(p95.IsOverflow);
        Assert.Equal(501, p95.LowerBoundMs);
        Assert.Equal(1000, p95.MaxMs);
        // The conservative value is the observed maximum, not the 500 ms ceiling.
        Assert.Equal(1000, h.P95);
        Assert.Equal(1000, h.P99);
        Assert.Contains("overflow", p95.ToString());
    }

    [Fact]
    public void InRangePercentilesAreUnchangedAndOnlyTheTailOverflows()
    {
        var h = new TickHistogram(500, 1);
        for (int i = 0; i < 98; i++) h.Record(10);
        h.Record(500);  // the top of the range is still in range
        h.Record(900);

        Assert.Equal(1, h.OverflowCount);
        Assert.False(h.GetPercentile(0.95).IsOverflow);
        Assert.Equal(10, h.P95);
        Assert.False(h.GetPercentile(0.99).IsOverflow);
        Assert.Equal(500, h.P99);
        Assert.True(h.GetPercentile(1.0).IsOverflow);
        Assert.Equal(900, h.Percentile(1.0));
    }

    [Fact]
    public void ResetClearsTheOverflow()
    {
        var h = new TickHistogram(100, 1);
        h.Record(5000);
        h.Reset();
        Assert.Equal(0, h.OverflowCount);
        Assert.Equal(0, h.Count);
        Assert.Equal(0, h.P99);
    }
}

/// <summary>
/// The bot scenario gate: tick percentiles bounded to the scenario window, an
/// explicit "insufficient data" outcome instead of a pass, minimum bot and movement
/// sample counts, and the configured move reject rate applied to the measured one.
/// </summary>
public sealed class BotPerformanceGateTests
{
    private static TickHistogram Ticks(int count, int ms)
    {
        var h = new TickHistogram(500, 1);
        for (int i = 0; i < count; i++) h.Record(ms);
        return h;
    }

    private static BotScenarioReport Report(int bots, int moveRequests, int moveRejects,
        TickHistogram? ticks, BotPerformanceGate? gate = null, int active = -1,
        IReadOnlyList<BotAnomaly>? anomalies = null) =>
        BotScenarioReport.FromMeasurements("test", TimeSpan.FromMinutes(1), bots,
            active < 0 ? bots : active, 0, 0, anomalies ?? [], moveRequests, moveRejects,
            ticks, gate ?? new BotPerformanceGate());

    [Fact]
    public void AHealthyMeasuredRunPasses()
    {
        var r = Report(bots: 20, moveRequests: 500, moveRejects: 10, ticks: Ticks(100, 5));
        Assert.Equal(BotGateStatus.Passed, r.Status);
        Assert.True(r.Passed);
        Assert.Empty(r.FailReasons);
        Assert.Equal(100, r.TickSamples);
    }

    [Fact]
    public void NoTickHistogramIsNotMeasuredNotAPass()
    {
        var r = Report(bots: 20, moveRequests: 500, moveRejects: 0, ticks: null);
        Assert.Equal(BotGateStatus.InsufficientData, r.Status);
        Assert.False(r.Passed);
        Assert.Contains(r.FailReasons, f => f.Contains("NOT MEASURED") && f.Contains("p95"));
    }

    [Fact]
    public void AnEmptyHistogramIsNotMeasuredNotAPass()
    {
        var r = Report(bots: 20, moveRequests: 500, moveRejects: 0, ticks: new TickHistogram(500, 1));
        Assert.Equal(BotGateStatus.InsufficientData, r.Status);
        Assert.False(r.Passed);
    }

    [Fact]
    public void ZeroBotsAndZeroSamplesIsNotMeasuredNotAPass()
    {
        var r = Report(bots: 0, moveRequests: 0, moveRejects: 0, ticks: new TickHistogram(500, 1));
        Assert.Equal(BotGateStatus.InsufficientData, r.Status);
        Assert.False(r.Passed);
        var gate = r.Gate!;
        Assert.Contains(gate.Checks, c => c.Name == "Bot Count" && c.Insufficient);
        Assert.Contains(gate.Checks, c => c.Name == "Move Reject Rate" && !c.Measured);
    }

    [Fact]
    public void TooFewBotsOrMovementSamplesIsInsufficient()
    {
        var gate = new BotPerformanceGate { MinBots = 10, MinMoveSamples = 100 };
        Assert.Equal(BotGateStatus.InsufficientData,
            Report(bots: 5, moveRequests: 500, moveRejects: 0, ticks: Ticks(100, 5), gate).Status);
        Assert.Equal(BotGateStatus.InsufficientData,
            Report(bots: 20, moveRequests: 99, moveRejects: 0, ticks: Ticks(100, 5), gate).Status);
        Assert.Equal(BotGateStatus.Passed,
            Report(bots: 20, moveRequests: 100, moveRejects: 0, ticks: Ticks(100, 5), gate).Status);
    }

    [Fact]
    public void TheMoveRejectThresholdIsAppliedToTheMeasuredRate()
    {
        var gate = new BotPerformanceGate { MaxMoveRejectRate = 0.10 };
        var bad = Report(bots: 20, moveRequests: 1000, moveRejects: 200, ticks: Ticks(100, 5), gate);
        Assert.Equal(BotGateStatus.Failed, bad.Status);
        Assert.Equal(0.2, bad.MoveRejectRate, 3);
        Assert.Contains(bad.FailReasons, f => f.StartsWith("FAILED") && f.Contains("Move Reject Rate"));

        var ok = Report(bots: 20, moveRequests: 1000, moveRejects: 100, ticks: Ticks(100, 5), gate);
        Assert.Equal(BotGateStatus.Passed, ok.Status);
    }

    [Fact]
    public void AMeasuredFailureWinsOverMissingData()
    {
        // Every bot disconnected and nothing moved: the disconnect failure is a real
        // result and must not be softened into "insufficient data".
        var r = Report(bots: 20, moveRequests: 0, moveRejects: 0, ticks: null, active: 0);
        Assert.Equal(BotGateStatus.Failed, r.Status);
    }

    [Fact]
    public void OverflowSamplesAboveAThresholdFailTheTickCheck()
    {
        // 100 ticks of 1000 ms in a 500 ms histogram, with thresholds raised above the
        // bucket ceiling. Reading the ceiling (500) as the percentile passed this run.
        var gate = new BotPerformanceGate { MaxP95TickMs = 700, MaxP99TickMs = 700 };
        var r = Report(bots: 20, moveRequests: 500, moveRejects: 0, ticks: Ticks(100, 1000), gate);
        Assert.Equal(BotGateStatus.Failed, r.Status);
        var p95 = r.Gate!.Checks.Single(c => c.Name == "p95 Server Tick");
        Assert.False(p95.Passed);
        Assert.Contains("overflow", p95.Actual);
    }

    [Fact]
    public void OverflowWithinTheThresholdStillPasses()
    {
        var gate = new BotPerformanceGate { MaxP95TickMs = 2000, MaxP99TickMs = 2000 };
        var r = Report(bots: 20, moveRequests: 500, moveRejects: 0, ticks: Ticks(100, 1000), gate);
        Assert.Equal(BotGateStatus.Passed, r.Status);
    }

    [Fact]
    public void TheEngineGatesOnlyTicksInsideTheScenarioWindow()
    {
        using var engine = new BotEngine(NullLogger.Instance);
        // Before the scenario: not part of its window.
        for (int i = 0; i < 10; i++) engine.RecordServerTickSample(900);

        engine.BeginScenarioWindow(new WalkTalkScenario(), 0);
        for (int i = 0; i < 60; i++) engine.RecordServerTickSample(4);
        engine.FinishScenario();

        // After the scenario: ignored too.
        for (int i = 0; i < 10; i++) engine.RecordServerTickSample(900);

        var report = engine.LastScenarioReport!;
        Assert.Equal(60, report.TickSamples);
        Assert.NotNull(report.Gate);
        var p99 = report.Gate!.Checks.Single(c => c.Name == "p99 Server Tick");
        Assert.True(p99.Passed);
        Assert.Equal("4ms", p99.Actual);
        // No bots ran: the run is not a pass.
        Assert.Equal(BotGateStatus.InsufficientData, report.Status);
        Assert.False(report.Passed);
        Assert.False(engine.IsScenarioRunning);
    }
}

/// <summary>The slow-tick summary must name the most expensive leaf phase, never a
/// group phase that contains it.</summary>
public sealed class DominantTickPhaseTests
{
    private static string Pick(long compute = 0, long npcBuild = 0, long clientState = 0,
        long npcApply = 0, long viewBuild = 0, long snapshot = 0, long worldTick = 0,
        long apply = 0, long postApply = 0, long flush = 0) =>
        SphereNet.Server.Program.PickDominantTickPhase(snapshot, worldTick, compute,
            npcBuild, clientState, npcApply, viewBuild, apply, postApply, flush);

    [Fact]
    public void AChildPhaseIsNamedNotTheComputeGroupThatContainsIt()
    {
        Assert.Equal("npc_build", Pick(compute: 12_000, npcBuild: 9_000));
        Assert.Equal("view_build", Pick(compute: 20_000, npcBuild: 2_000, viewBuild: 15_000));
    }

    [Fact]
    public void UnmeasuredComputeTimeIsReportedAsItsResidual()
    {
        Assert.Equal("compute_other",
            Pick(compute: 20_000, npcBuild: 1_000, clientState: 1_000, npcApply: 1_000, viewBuild: 1_000));
    }

    [Fact]
    public void TheSnapshotGroupIsReplacedByItsResidualToo()
    {
        Assert.Equal("world_tick", Pick(snapshot: 10_000, worldTick: 9_000, flush: 2_000));
        Assert.Equal("snapshot", Pick(snapshot: 10_000, worldTick: 1_000, flush: 2_000));
    }

    [Fact]
    public void PhasesOutsideTheGroupsStillWin()
    {
        Assert.Equal("flush", Pick(compute: 5_000, npcBuild: 4_000, flush: 6_000));
        Assert.Equal("apply", Pick(compute: 5_000, npcBuild: 4_000, apply: 7_000));
    }
}

/// <summary>
/// The multicore world tick honours its cancellation token at safe boundaries:
/// it returns (not throws) at the next boundary, applies nothing half way, and the
/// next tick runs what was skipped.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WorldTickCooperativeCancellationTests
{
    private static Character AddPlayer(GameWorld world, int x, int y)
    {
        var p = world.CreateCharacter();
        p.IsPlayer = true;
        p.IsOnline = true;
        world.PlaceCharacter(p, new Point3D((short)x, (short)y, 0, 0));
        world.AddOnlinePlayer(p);
        return p;
    }

    [Fact]
    public void ACancelledTokenReturnsBeforeAnyCallbackAndTheNextTickRecovers()
    {
        var world = TestHarness.CreateWorld();
        var fired = new List<string>();
        world.TimerFExpired = (_, entry) => fired.Add(entry.FunctionName);
        AddPlayer(world, 300, 300);
        var item = world.CreateItem();
        world.PlaceItem(item, new Point3D(300, 301, 0, 0));
        item.AddTimerF(0, "f_due", "");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ex = Record.Exception(() => world.OnTickParallel(2, cts.Token));

        Assert.Null(ex);              // cooperative: returns, does not throw
        Assert.Empty(fired);          // stopped at the first boundary
        Assert.Single(item.TimerFEntries); // the job is still queued, not lost

        world.OnTickParallel(2, CancellationToken.None);
        Assert.Equal(new[] { "f_due" }, fired);
    }

    [Fact]
    public void CancellationDuringAPhaseStopsAtTheNextBoundaryWithoutCuttingTheRunningWork()
    {
        var world = TestHarness.CreateWorld();
        AddPlayer(world, 300, 300);
        var timerfItem = world.CreateItem();
        world.PlaceItem(timerfItem, new Point3D(300, 301, 0, 0));
        timerfItem.AddTimerF(0, "f_a", "");
        timerfItem.AddTimerF(0, "f_b", "");

        var timed = world.CreateItem();
        timed.SetTimeout(Environment.TickCount64 - 1);
        int itemTimerCalls = 0;
        Item.OnTimerExpired = _ => { itemTimerCalls++; return SphereNet.Core.Enums.TriggerResult.True; };

        using var cts = new CancellationTokenSource();
        var fired = new List<string>();
        world.TimerFExpired = (_, entry) =>
        {
            fired.Add(entry.FunctionName);
            cts.Cancel(); // the timeout elapses while a TIMERF callback runs
        };

        world.OnTickParallel(2, cts.Token);

        // The running phase is not interrupted: both due TIMERF jobs of the phase ran.
        Assert.Equal(new[] { "f_a", "f_b" }, fired);
        // The next boundary was honoured: the item timer phase did not start.
        Assert.Equal(0, itemTimerCalls);

        // The following tick, with a fresh token, recovers normally.
        world.OnTickParallel(2, CancellationToken.None);
        Assert.Equal(1, itemTimerCalls);
        Assert.Equal(2, fired.Count); // nothing re-ran
    }
}

/// <summary>NPC batch budget: configurable limits with the field-tuned defaults,
/// and the due / deferred / wait / lateness telemetry.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcBudgetConfigAndTelemetryTests
{
    [Fact]
    public void DefaultsMatchTheFieldTunedValues()
    {
        var c = new SphereConfig();
        Assert.Equal(500, c.MaxNpcsPerTick);
        Assert.Equal(2, c.NpcPathPrestagePerTick);
        Assert.Equal(SphereNet.Server.Program.DefaultMaxNpcsPerTick, c.MaxNpcsPerTick);
        Assert.Equal(SphereNet.Server.Program.DefaultNpcPathPrestagePerTick, c.NpcPathPrestagePerTick);
    }

    [Theory]
    [InlineData("MaxNpcsPerTick=800\nNpcPathPrestagePerTick=4\n", 800, 4)]
    [InlineData("MaxNpcsPerTick=0\nNpcPathPrestagePerTick=-3\n", 1, 0)]
    [InlineData("", 500, 2)]
    public void TheIniKeysAreReadAndClamped(string body, int expectedNpcs, int expectedPaths)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_cfg_{Guid.NewGuid():N}.ini");
        File.WriteAllText(tmp, "[SPHERE]\n" + body);
        try
        {
            var parser = new IniParser();
            parser.Load(tmp);
            var config = new SphereConfig();
            config.LoadFromIni(parser);
            Assert.Equal(expectedNpcs, config.MaxNpcsPerTick);
            Assert.Equal(expectedPaths, config.NpcPathPrestagePerTick);
        }
        finally { try { File.Delete(tmp); } catch { } }
    }

    [Fact]
    public void TelemetryCountsDueDeferredOldestWaitAndLateness()
    {
        var world = TestHarness.CreateWorld();
        long now = 1_000_000;
        var due = new List<Character>();
        int[] lateBy = [10, 20, 30, 400, 900];
        foreach (int late in lateBy)
        {
            var npc = world.CreateCharacter();
            npc.NextNpcActionTime = now - late;
            due.Add(npc);
        }
        var woken = world.CreateCharacter();
        woken.NextNpcActionTime = 0; // bulk wake: no due time, no lateness
        due.Add(woken);

        var t = new NpcBudgetTelemetry();
        t.RecordBatch(due, limit: 3, nowMs: now);

        Assert.Equal(1, t.Ticks);
        Assert.Equal(6, t.Due);
        Assert.Equal(3, t.Deferred);
        Assert.Equal(900, t.OldestWaitMs);   // a deferred NPC is the oldest waiter
        Assert.Equal(3, t.LatenessSamples);  // only the NPCs that acted
        Assert.Equal(30, t.LatenessP99.ValueMs);

        t.Reset();
        Assert.Equal(0, t.Due);
        Assert.Equal(0, t.LatenessSamples);
    }

    [Fact]
    public void TheTickBudgetDefersOverflowAndRecordsIt()
    {
        var world = new GameWorld(Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { }));
        world.InitMap(0, 1024, 1024);
        long now = Environment.TickCount64;
        var wheel = new TimerWheel(now - 5000);
        int total = SphereNet.Server.Program.DefaultMaxNpcsPerTick + 20;
        for (int i = 0; i < total; i++)
        {
            var npc = world.CreateCharacter();
            npc.NextNpcActionTime = now - 50;
            wheel.Schedule(npc, now - 4000);
        }

        SphereNet.Server.Program.NpcBudgetStats.Reset();
        int acted = 0;
        try
        {
            SphereNet.Server.Program.RunDueNpcs(wheel, now, _ => acted++, _ => true);
            var s = SphereNet.Server.Program.NpcBudgetStats;
            Assert.Equal(SphereNet.Server.Program.DefaultMaxNpcsPerTick, acted);
            Assert.Equal(total, s.Due);
            Assert.Equal(20, s.Deferred);
            Assert.True(s.OldestWaitMs >= 50);
        }
        finally
        {
            SphereNet.Server.Program.NpcBudgetStats.Reset();
        }
    }
}
