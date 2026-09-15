using System;
using System.Diagnostics;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Server;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Telling "the loop was slow" apart from "the machine did not schedule us".
///
/// A stall report names the phase the time was spent in, and the yield phase is the one
/// that misleads: nothing it can do takes more than a few milliseconds, so a yield
/// measured at 200 ms is not this engine being slow, it is the thread not getting the
/// CPU back. Those two are fixed in completely different places - one in the code, one
/// on the box - so the report now carries what the yield ASKED for beside what it took.
///
/// The other half is the panel's stats refresh. It runs on the main loop every two
/// seconds for as long as a dashboard is open, and it used to walk every sector of
/// every map twice over to produce a handful of numbers.
/// </summary>
public sealed class LoopStallAttributionTests
{
    private readonly ITestOutputHelper _out;
    public LoopStallAttributionTests(ITestOutputHelper output) => _out = output;

    // ---- what the yield can and cannot account for ------------------------

    [Theory]
    [InlineData(0)]     // spin
    [InlineData(1)]     // sleep(1)
    [InlineData(2)]     // hybrid
    [InlineData(3)]     // adaptive
    public void NoYieldModeEverAsksForMoreThanAFewMilliseconds(int mode)
    {
        // Whatever slack it is offered - here, a whole second of it.
        int asked = TickYieldStrategy.Yield(mode, msUntilNextDeadline: 1000);

        _out.WriteLine($"mode {mode}: asked for {asked}ms");

        // This is the fact that makes the stall report readable: a yield of 200 ms in
        // the log cannot have been requested, so it was imposed.
        Assert.InRange(asked, 0, TickYieldStrategy.AdaptiveMaxSleepMs);
    }

    [Fact]
    public void TheAdaptiveYieldNeverSleepsPastTheDeadline()
    {
        // The deadline is the tick cadence; sleeping through it would delay movement
        // and ping processing by exactly as much as it overslept.
        for (long slack = -5; slack <= 20; slack++)
        {
            int ms = TickYieldStrategy.ComputeAdaptiveSleepMs(slack, TickYieldStrategy.AdaptiveMaxSleepMs);
            Assert.True(ms < Math.Max(slack, 1), $"slack {slack} → sleep {ms}");
            Assert.InRange(ms, 0, TickYieldStrategy.AdaptiveMaxSleepMs);
        }
    }

    [Fact]
    public void AYieldThatTakesFarLongerThanItAskedForIsTheMachineNotTheLoop()
    {
        // The shape the live log showed: a yield that asked for a few milliseconds and
        // came back hundreds later, with every other phase at zero and no GC. Measured
        // here as an assertion about the CONTRACT rather than about a timing, because
        // the point is what the two numbers mean together.
        var sw = Stopwatch.StartNew();
        int asked = TickYieldStrategy.Yield(3, msUntilNextDeadline: 100);
        sw.Stop();

        _out.WriteLine($"asked {asked}ms, took {sw.Elapsed.TotalMilliseconds:F1}ms");
        Assert.InRange(asked, 0, TickYieldStrategy.AdaptiveMaxSleepMs);
    }

    // ---- what the dashboard costs the loop ---------------------------------

    [Fact]
    public void TheWorldTotalsAndThePerMapFiguresAgree()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.InitMap(1, 512, 512);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        for (int i = 0; i < 20; i++)
        {
            var item = world.CreateItem();
            item.BaseId = 0x0EED;
            world.PlaceItem(item, new Point3D((short)(100 + i), 100, 0, (byte)(i % 2)));
        }
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var (chars, items, sectors) = world.GetStats();
        var maps = world.GetMapStats();

        _out.WriteLine($"totals: {chars} chars, {items} items, {sectors} sectors across {maps.Count} maps");

        // The totals are now DERIVED from the per-map walk rather than taken by a
        // second walk of their own, so the two can no longer drift apart - and the
        // panel pays for one pass instead of two.
        Assert.Equal(maps.Sum(m => m.Chars), chars);
        Assert.Equal(maps.Sum(m => m.Items), items);
        Assert.Equal(maps.Sum(m => m.Sectors), sectors);
        Assert.Equal(21, chars + items);
    }

    [Fact]
    public void AnEmptyWorldStillReportsItsSectors()
    {
        // The control: deriving the totals must not have turned them into zeroes for a
        // world with nothing in it - the sector count is what the panel draws its map
        // rows from.
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);

        var (chars, items, sectors) = world.GetStats();
        Assert.Equal(0, chars);
        Assert.Equal(0, items);
        Assert.True(sectors > 0);
        Assert.Equal(world.GetMapStats().Sum(m => m.Sectors), sectors);
    }
}
