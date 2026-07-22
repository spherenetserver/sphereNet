using SphereNet.Server;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// İş 15 / M5 — tick-yield CPU policy. The default hybrid yield (SpinWait + Sleep(0))
/// never actually sleeps, so an idle server hot-spins one core. Mode 3 adds a
/// deadline-aware adaptive yield that sleeps out the slack until the next tick is
/// due (capped), dropping idle CPU without moving the tick cadence. The sleep
/// duration is a pure function so the policy is verified deterministically here;
/// the runtime effect is measured live via the [tick_stats] loops-per-tick gauge.
/// </summary>
public class TickYieldStrategyTests
{
    [Theory]
    [InlineData(0, TickYieldAction.Spin)]
    [InlineData(1, TickYieldAction.SleepOne)]
    [InlineData(2, TickYieldAction.Hybrid)]
    [InlineData(3, TickYieldAction.Adaptive)]
    [InlineData(99, TickYieldAction.Hybrid)]
    [InlineData(-1, TickYieldAction.Hybrid)]
    public void Resolve_MapsConfiguredModesToStableActions(int mode, TickYieldAction expected)
    {
        Assert.Equal(expected, TickYieldStrategy.Resolve(mode));
    }

    [Theory]
    [InlineData(-10, 0)]  // overdue — never sleep
    [InlineData(0, 0)]    // due now — never sleep
    [InlineData(1, 0)]    // 1ms guard — do not risk overrunning the deadline
    [InlineData(2, 1)]    // 1ms of usable slack
    [InlineData(6, 5)]    // slack (5 after guard) is exactly the cap
    [InlineData(50, 5)]   // large idle window clamps to the cap, not the whole slack
    public void ComputeAdaptiveSleepMs_NeverOverrunsDeadlineAndClampsToCap(long slackMs, int expected)
    {
        Assert.Equal(expected, TickYieldStrategy.ComputeAdaptiveSleepMs(slackMs, TickYieldStrategy.AdaptiveMaxSleepMs));
    }

    [Fact]
    public void ComputeAdaptiveSleepMs_NeverExceedsCapAndNeverPassesDeadline()
    {
        for (long slack = -5; slack <= 1000; slack++)
        {
            int ms = TickYieldStrategy.ComputeAdaptiveSleepMs(slack, TickYieldStrategy.AdaptiveMaxSleepMs);
            Assert.InRange(ms, 0, TickYieldStrategy.AdaptiveMaxSleepMs);
            // Must never sleep to or past the deadline: the sleep is always < slack.
            if (slack > 0) Assert.True(ms < slack);
        }
    }
}
