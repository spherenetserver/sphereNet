namespace SphereNet.Tests;

public class PeriodicJobTimingTests
{
    /// <summary>Periodic world jobs (region periodic triggers, party health bars,
    /// house decay) run on wall-clock periods, so a different ServerTickMs does not
    /// change how often they fire.</summary>
    [Fact]
    public void PeriodicJobsFireOncePerWallClockPeriod()
    {
        long next = 0;
        Assert.False(SphereNet.Server.Program.PeriodDue(ref next, 1_000, 4_000));  // arms
        Assert.False(SphereNet.Server.Program.PeriodDue(ref next, 4_999, 4_000));
        Assert.True(SphereNet.Server.Program.PeriodDue(ref next, 5_000, 4_000));
        Assert.False(SphereNet.Server.Program.PeriodDue(ref next, 5_050, 4_000));  // next tick, 50 ms later
        Assert.True(SphereNet.Server.Program.PeriodDue(ref next, 9_000, 4_000));
    }
}
