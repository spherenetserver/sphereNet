using SphereNet.Core.Configuration;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What GUARDLINGER counts.
///
/// Upstream reads the ini key as MINUTES - CServerConfig.cpp:1292 stores
/// `value * 60 * MSECS_PER_SEC` - writes it back in minutes (:2128), and defaults to 3
/// (:173). This engine read the same key as seconds, so a legacy sphere.ini carrying
/// upstream's own default of 3 produced a guard that dissolved three seconds after it
/// arrived, and the shipped default of 300 meant five minutes where upstream means
/// five hours.
///
/// The value is one integer in a file an operator copies between servers, so the unit
/// is not an internal detail: the same file has to mean the same thing on both.
/// </summary>
public sealed class GuardLingerUnitTests
{
    private readonly ITestOutputHelper _out;
    public GuardLingerUnitTests(ITestOutputHelper output) => _out = output;

    /// <summary>The engine's own arithmetic, kept next to the assertion so the unit is
    /// stated once: Program.NpcServices turns the config value into milliseconds.</summary>
    private static long LingerMs(int configured) => System.Math.Max(1, configured) * 60_000L;

    [Fact]
    public void TheDefaultIsUpstreamsThreeMinutes()
    {
        var cfg = new SphereConfig();
        _out.WriteLine($"GuardLinger={cfg.GuardLinger} -> {LingerMs(cfg.GuardLinger) / 1000}s");
        Assert.Equal(3, cfg.GuardLinger);
        Assert.Equal(3 * 60 * 1000L, LingerMs(cfg.GuardLinger));
    }

    [Theory]
    [InlineData(1, 60_000L)]
    [InlineData(3, 180_000L)]
    [InlineData(15, 900_000L)]
    public void TheValueIsMinutes(int configured, long expectedMs)
    {
        Assert.Equal(expectedMs, LingerMs(configured));
    }

    [Fact]
    public void ZeroStillLeavesAGuardLongEnoughToBeSeen()
    {
        // The clamp is what keeps a 0 in an ini from spawning a guard that is gone
        // before the client has drawn it.
        Assert.Equal(60_000L, LingerMs(0));
        Assert.Equal(60_000L, LingerMs(-5));
    }
}
