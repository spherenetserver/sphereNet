using Microsoft.Extensions.Logging;
using SphereNet.Game.Diagnostics;

namespace SphereNet.Tests;

public sealed class WorldTickProbeTests
{
    [Fact]
    public void AttributesLargeAllocationsAndThrottlesRepeatedReports()
    {
        var collector = new ScriptDiagnosticCollector();
        using var factory = LoggerFactory.Create(b => b.AddProvider(new CollectingLoggerProvider(collector)));
        var logger = factory.CreateLogger("probe");
        long last = 0;
        var probe = WorldTickProbe.Begin();
        probe.Mark("clock");
        var allocation = new byte[17 * 1024 * 1024];
        probe.Mark("item_timers");
        probe.Report(logger, ref last);
        GC.KeepAlive(allocation);
        var entry = Assert.Single(collector.Entries);
        Assert.Contains("allocation_source=item_timers", entry.Message);
        Assert.Contains("[world_tick_detail]", entry.Message);
        probe.Report(logger, ref last);
        Assert.Single(collector.Entries);
    }

    [Fact]
    public void CheckpointsDoNotAllocatePerTick()
    {
        for (int i = 0; i < 10; i++) { var warm = WorldTickProbe.Begin(); warm.Mark("clock"); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            var probe = WorldTickProbe.Begin();
            probe.Mark("clock"); probe.Mark("sectors"); probe.Mark("maintenance");
            probe.Mark("timerf"); probe.Mark("item_timers");
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
