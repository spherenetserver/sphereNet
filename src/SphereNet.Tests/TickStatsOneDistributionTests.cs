using System.Reflection;

namespace SphereNet.Tests;

/// <summary>
/// Every number on a tick-stats line describes the same ticks.
///
/// The percentiles were taken from the whole 2048-sample ring - about three and a half
/// minutes - while the average and the maximum came from separate accumulators reset
/// every thirty seconds. Two distributions on one line, with nothing saying so, and the
/// contradiction showed in a live report: "max=7.0ms ... p99=58.9ms", which no single
/// sample set can produce. A reader chases a 59ms stall that the reported window did not
/// have.
/// </summary>
public sealed class TickStatsOneDistributionTests
{
    private static readonly Type s_program =
        typeof(SphereNet.Server.Program);

    private static void Reset()
    {
        Field("_tickTelemetryWriteIndex").SetValue(null, 0);
        Field("_tickTelemetrySampleCount").SetValue(null, 0);
        var ring = (long[])Field("_tickTelemetryWindowUs").GetValue(null)!;
        Array.Clear(ring);
    }

    private static FieldInfo Field(string name) =>
        s_program.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException(name + " not found");

    private static void Record(long micros) =>
        s_program.GetMethod("RecordTickTelemetry", BindingFlags.Static | BindingFlags.NonPublic)!
                 .Invoke(null, [micros]);

    private static (double Avg, double Max, double P50, double P95, double P99) Snapshot(int lastSamples)
    {
        var m = s_program.GetMethod("GetTickTelemetrySnapshot",
            BindingFlags.Static | BindingFlags.NonPublic, [typeof(int)])!;
        object snap = m.Invoke(null, [lastSamples])!;
        double Get(string n) => (double)snap.GetType().GetProperty(n)!.GetValue(snap)!;
        return (Get("AvgMs"), Get("MaxMs"), Get("P50Ms"), Get("P95Ms"), Get("P99Ms"));
    }

    /// <summary>The reported shape: one old spike, then a quiet window. The window's
    /// numbers must not carry the spike.</summary>
    [Fact]
    public void AnOldSpikeStaysOutOfTheWindow()
    {
        Reset();
        Record(58_900);                                  // the spike, long ago
        for (int i = 0; i < 300; i++) Record(1_300);     // a quiet 30s window
        Record(7_000);                                   // this window's real worst

        var window = Snapshot(301);
        Assert.Equal(7.0, window.Max, 1);
        Assert.True(window.P99 <= window.Max,
            $"p99 {window.P99:F1} is above the window's own max {window.Max:F1}");

        // The long view still has it, which is what the ring is for.
        var everything = Snapshot(int.MaxValue);
        Assert.Equal(58.9, everything.Max, 1);
    }

    /// <summary>The invariant itself, over a spread of shapes: a percentile can never
    /// exceed the maximum of the same samples.</summary>
    [Fact]
    public void NoPercentileEverExceedsTheMaximum()
    {
        var rng = new Random(20260919);
        for (int trial = 0; trial < 50; trial++)
        {
            Reset();
            int n = rng.Next(1, 500);
            for (int i = 0; i < n; i++)
                Record(rng.Next(100, 200_000));

            var s = Snapshot(n);
            Assert.True(s.P50 <= s.Max && s.P95 <= s.Max && s.P99 <= s.Max,
                $"trial {trial}: p50={s.P50:F1} p95={s.P95:F1} p99={s.P99:F1} max={s.Max:F1}");
            Assert.True(s.Avg <= s.Max, $"trial {trial}: avg {s.Avg:F1} > max {s.Max:F1}");
            Assert.True(s.P50 <= s.P95 && s.P95 <= s.P99, $"trial {trial}: percentiles out of order");
        }
    }

    /// <summary>Asking for more than the ring holds is the whole ring, not a crash and
    /// not zeroes read off unwritten slots.</summary>
    [Fact]
    public void AskingForMoreThanIsThereGivesWhatIsThere()
    {
        Reset();
        for (int i = 0; i < 5; i++) Record(2_000);

        var s = Snapshot(1000);
        Assert.Equal(2.0, s.Max, 1);
        Assert.Equal(2.0, s.Avg, 1);
    }
}
