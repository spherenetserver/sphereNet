using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// The weather tick costs nothing when there is nothing due.
///
/// It runs ten times a second for the life of the shard. It once allocated an expiry
/// list every single tick, and a collection suspends the tick thread wherever it happens
/// to be: a live report of "worst weather 206.7ms" named a step that does almost nothing.
/// The step now reports the GC pause inside it separately, and the garbage that feeds
/// those pauses is gone from the step itself.
///
/// Weather is per sector (CSectorEnviron); a sector recalculates on its own 30-second
/// tick (SECTOR_TICKING_PERIOD, CSector.cpp:20), so between those ticks the pass over the
/// occupied sectors must not allocate either.
///
/// Measured with GC.GetAllocatedBytesForCurrentThread, which is exact for this thread and
/// does not depend on when a collection happens to run.
/// </summary>
public sealed class WeatherTickAllocationTests
{
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

    /// <summary>With a player standing in a sector whose tick is not yet due, still
    /// nothing - the occupied-sector pass reuses its scratch set.</summary>
    [Fact]
    public void AnOccupiedSectorThatIsNotDueAllocatesNothing()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        var me = world.CreateCharacter();
        me.IsPlayer = true;
        world.PlaceCharacter(me, new Point3D(100, 100, 0, 0));
        me.IsOnline = true;
        world.AddOnlinePlayer(me);
        var engine = new WeatherEngine(world) { SeasonChangeInterval = 0 };

        Assert.Equal(0, Churn(engine, 500));
    }

    /// <summary>The recalculation itself still happens on the sector's tick.</summary>
    [Fact]
    public void ASectorTickStillRecalculatesTheWeather()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        WeatherEngine.NoWeather = true; // NOWEATHER: every recalculation is DRY
        var sector = world.GetSector(new Point3D(100, 100, 0, 0))!;
        sector.Weather = (byte)WeatherType.Rain;
        var engine = new WeatherEngine(world);
        typeof(WeatherEngine)
            .GetField("_rand", System.Reflection.BindingFlags.Instance |
                               System.Reflection.BindingFlags.NonPublic)!
            .SetValue(engine, new ZeroRoll());

        engine.OnSectorTick(sector);

        Assert.Equal((byte)WeatherType.None, sector.Weather);
    }

    private sealed class ZeroRoll : Random
    {
        public override int Next(int maxValue) => 0;
        public override int Next(int minValue, int maxValue) => minValue;
    }
}
