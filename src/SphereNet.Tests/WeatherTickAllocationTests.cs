using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// The weather tick costs nothing when there is no weather.
///
/// It runs ten times a second for the life of the shard, and on a shard with NOWEATHER -
/// the shipped default, and upstream's - there is never anything in the dictionary to
/// expire and never a die to roll. It still allocated an expiry list every single tick.
///
/// That matters because of how a stall gets reported. A collection suspends the tick
/// thread wherever it happens to be, and the slow-tick line blamed whichever maintenance
/// step was running: a live report of "worst weather 206.7ms" named a step that does
/// almost nothing. The step now reports the GC pause inside it separately, and the
/// garbage that feeds those pauses is gone from the step itself.
///
/// Measured with GC.GetAllocatedBytesForCurrentThread, which is exact for this thread and
/// does not depend on when a collection happens to run.
/// </summary>
public sealed class WeatherTickAllocationTests
{
    private static SphereNet.Game.World.Regions.Region Probe()
    {
        var region = new SphereNet.Game.World.Regions.Region { Name = "probe" };
        region.AddRect(90, 90, 120, 120);
        return region;
    }

    /// <summary>The stored duration is five minutes of real uptime, which no test can
    /// wait out; reach the stored end time instead.</summary>
    private static void PullEndTickIntoThePast(WeatherEngine engine)
    {
        var field = typeof(WeatherEngine).GetField("_regionWeather",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var dict = (System.Collections.IEnumerable)field.GetValue(engine)!;
        foreach (var entry in dict)
        {
            object state = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
            state.GetType().GetField("EndTick")!.SetValue(state, 0L);
        }
    }

    private static long Churn(WeatherEngine engine, int ticks)
    {
        engine.OnTick();                                   // let any one-time setup settle
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < ticks; i++) engine.OnTick();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>An idle shard's weather tick allocates nothing at all.</summary>
    [Fact]
    public void AnIdleWeatherTickAllocatesNothing()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var engine = new WeatherEngine(world) { SeasonChangeInterval = 0 };

        Assert.Equal(0, Churn(engine, 500));
    }

    /// <summary>And with weather stored but not yet due, still nothing - the expiry
    /// list is built only when something has actually expired.</summary>
    [Fact]
    public void StoredWeatherThatIsNotDueAllocatesNothing()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var engine = new WeatherEngine(world) { SeasonChangeInterval = 0 };

        engine.SetRegionWeather(Probe(), WeatherType.Rain, 20, 15);

        Assert.Equal(0, Churn(engine, 500));
    }

    /// <summary>The expiry itself still happens - the list is skipped, not the work.</summary>
    [Fact]
    public void WeatherStillExpires()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var engine = new WeatherEngine(world) { SeasonChangeInterval = 0 };

        var region = Probe();
        engine.SetRegionWeather(region, WeatherType.Rain, 20, 15);

        WeatherType? cleared = null;
        engine.OnWeatherChanged = (r, t, _, _) => { if (r == region) cleared = t; };

        // Pull the stored end time back into the past the way 5 minutes of uptime would.
        PullEndTickIntoThePast(engine);
        engine.OnTick();

        Assert.Equal(WeatherType.None, cleared);
        Assert.Equal(WeatherType.None, engine.GetWeatherForRegion(region).Type);
    }
}
