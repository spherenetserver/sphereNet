using SphereNet.Panel;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Scheduled shutdown/restart: the countdown players see and the action
/// at the end, with a delay seam so no test waits for real minutes.</summary>
public sealed class PanelFeatureTests
{
    private sealed class Probe
    {
        public readonly List<string> Said = [];
        public int Shutdowns, Restarts;
        public readonly List<TimeSpan> Waits = [];
        public ShutdownScheduler Make(Func<TimeSpan, CancellationToken, Task>? delay = null) => new(
            msg => { lock (Said) Said.Add(msg); return true; },
            () => { Shutdowns++; return true; },
            () => { Restarts++; return true; },
            clock: () => new DateTime(2026, 9, 23, 12, 0, 0, DateTimeKind.Utc),
            delay: delay ?? ((t, ct) => { lock (Waits) Waits.Add(t); return Task.CompletedTask; }));
    }

    [Fact]
    public async Task AFiveMinuteShutdownWarnsAtEachMarkThenShutsDown()
    {
        var probe = new Probe();
        var scheduler = probe.Make();

        Assert.True(scheduler.TrySchedule(300, restart: false, "Maintenance.", out var status));
        Assert.True(status.Pending);
        Assert.Equal(new DateTime(2026, 9, 23, 12, 5, 0, DateTimeKind.Utc), status.DueUtc);
        await scheduler.Running!;

        Assert.Equal(
        [
            "Server shutdown in 5 minutes. Maintenance.",
            "Server shutdown in 2 minutes. Maintenance.",
            "Server shutdown in 1 minute. Maintenance.",
            "Server shutdown in 30 seconds. Maintenance.",
            "Server shutdown in 10 seconds. Maintenance.",
            "Server shutdown now.",
        ], probe.Said);
        // The waits add up to the whole delay.
        Assert.Equal(TimeSpan.FromSeconds(300), probe.Waits.Aggregate(TimeSpan.Zero, (a, b) => a + b));
        Assert.Equal(1, probe.Shutdowns);
        Assert.Equal(0, probe.Restarts);
        Assert.False(scheduler.Status.Pending);
    }

    [Fact]
    public async Task ARestartRunsTheRestartAction()
    {
        var probe = new Probe();
        var scheduler = probe.Make();
        Assert.True(scheduler.TrySchedule(60, restart: true, null, out _));
        await scheduler.Running!;
        Assert.Equal(1, probe.Restarts);
        Assert.Equal(0, probe.Shutdowns);
        Assert.StartsWith("Server restart in 1 minute.", probe.Said[0]);
    }

    [Fact]
    public async Task CancellingStopsTheActionAndSaysSo()
    {
        var probe = new Probe();
        var gate = new TaskCompletionSource();
        var scheduler = probe.Make(async (t, ct) =>
        {
            await gate.Task.WaitAsync(ct);
        });

        Assert.True(scheduler.TrySchedule(600, restart: false, null, out _));
        Assert.False(scheduler.TrySchedule(60, restart: true, null, out var existing));   // one at a time
        Assert.True(existing.Pending);

        var after = scheduler.Cancel();
        Assert.False(after.Pending);
        await scheduler.Running!;
        Assert.Equal(0, probe.Shutdowns);
        Assert.Contains("The scheduled server shutdown has been cancelled.", probe.Said);
    }
}
