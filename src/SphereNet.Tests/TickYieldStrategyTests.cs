using SphereNet.Server;

namespace SphereNet.Tests;

public class TickYieldStrategyTests
{
    [Theory]
    [InlineData(0, TickYieldAction.Spin)]
    [InlineData(1, TickYieldAction.SleepOne)]
    [InlineData(2, TickYieldAction.Hybrid)]
    [InlineData(99, TickYieldAction.Hybrid)]
    [InlineData(-1, TickYieldAction.Hybrid)]
    public void Resolve_MapsConfiguredModesToStableActions(int mode, TickYieldAction expected)
    {
        Assert.Equal(expected, TickYieldStrategy.Resolve(mode));
    }
}
