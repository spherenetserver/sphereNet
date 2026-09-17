using SphereNet.Game.World.Sectors;

namespace SphereNet.Tests;

/// <summary>
/// Whether a sector sleeps must not depend on how long the MACHINE has been up.
///
/// The sleep timeout is measured from the last time a client stood in the sector, and
/// the clock behind it is Environment.TickCount64 - host uptime. A sector no client had
/// ever entered carried a stamp of 0, so the test "nowMs - stamp > SECTORSLEEP" was
/// really "has this machine been up longer than SECTORSLEEP". On a host that had just
/// rebooted - which is exactly when a shard is started - the answer was no for every
/// sector in the world, and none of them could sleep for the first ten minutes.
///
/// It showed up as two tests that passed on a developer's machine, whose uptime is
/// days, and failed on a CI runner, whose uptime is minutes.
/// </summary>
public sealed class SectorSleepClockTests : IDisposable
{
    private readonly long _delay = Sector.SleepDelayMs;

    public void Dispose() => Sector.SleepDelayMs = _delay;

    private static Sector Fresh()
    {
        Sector.SleepDelayMs = 10L * 60 * 1000;
        return new Sector(0, 0, 0, 64);
    }

    /// <summary>Five seconds after the host booted, a sector nobody has visited is
    /// asleep - the same answer it gives on a machine that has been up for days.</summary>
    [Fact]
    public void AnUnvisitedSectorSleepsOnAFreshlyBootedHost()
    {
        var sector = Fresh();

        Assert.True(sector.CanSleep(nowMs: 5_000));
        Assert.True(sector.CanSleep(nowMs: 5L * 24 * 60 * 60 * 1000));
    }

    /// <summary>And it has not simply been made to sleep always: a client who was
    /// here keeps it awake for the whole grace, on the same young clock.</summary>
    [Fact]
    public void AVisitedSectorStaysAwakeForTheGrace()
    {
        var sector = Fresh();
        sector.SetLastClientTime(4_000);

        Assert.False(sector.CanSleep(nowMs: 5_000));                    // 1s in
        Assert.False(sector.CanSleep(nowMs: 4_000 + 10L * 60 * 1000));  // exactly at
        Assert.True(sector.CanSleep(nowMs: 4_000 + 10L * 60 * 1000 + 1));
    }

    /// <summary>"Never visited" and "visited at time zero" are different states, which
    /// is what the old single long could not say.</summary>
    [Fact]
    public void NeverVisitedIsNotTheSameAsVisitedAtZero()
    {
        var never = Fresh();
        Assert.False(never.HasEverHadClient);

        var atZero = Fresh();
        atZero.SetLastClientTime(0);
        Assert.True(atZero.HasEverHadClient);
        Assert.False(atZero.CanSleep(nowMs: 1_000));
    }
}
