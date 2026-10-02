using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// The item-timer and decay queues keep ONE entry per item, as upstream's ticking
/// list does (CWorldTicker::AddTimedObject erases the previous entry before it
/// inserts the new timeout; DelTimedObject removes it).
///
/// Before, every re-arm pushed another entry and stale ones were recognised only
/// when they surfaced, without counting against the per-tick budget: 100,000
/// re-arms of one item left 100,001 entries and a single drain walked 100,000 dead
/// ones; 1,000 writes of the same decay deadline came back as 256 copies of one item
/// from a single 256-item collection.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ItemDeadlineQueueTests
{
    private readonly ITestOutputHelper _out;
    public ItemDeadlineQueueTests(ITestOutputHelper output) => _out = output;

    private static Item Ground(GameWorld world, short x)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(x, 100, 0, 0));
        return item;
    }

    private static List<Item> Due(GameWorld world, long now, int max = 256)
    {
        var buffer = new List<Item>();
        world.CollectDueDecay(now, max, buffer);
        return buffer;
    }

    // ---------------------------------------------------------------- timers

    [Fact]
    public void HundredThousandTimerRearmsLeaveOneEntryAndADrainDoesNoStaleWork()
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        long now = Environment.TickCount64;
        int calls = 0;
        Item.OnTimerExpired = _ => { calls++; return TriggerResult.True; };

        // The report's probe: 100,000 different deadlines, all already past, then
        // the live one moved well into the future.
        for (int i = 0; i < 100_000; i++)
            item.SetTimeout(now - 200_000 + i);
        item.SetTimeout(now + 600_000);

        long droppedBefore = world.TimerQueueStaleDropped;
        var sw = Stopwatch.StartNew();
        world.OnTick();
        sw.Stop();

        _out.WriteLine($"queue={world.TimerQueueCount} stale_dropped=" +
                       $"{world.TimerQueueStaleDropped - droppedBefore} drain+tick={sw.Elapsed.TotalMilliseconds:F2}ms");
        Assert.Equal(1, world.TimerQueueCount);           // was 100,001
        Assert.Equal(0, world.TimerQueueStaleDropped - droppedBefore); // was 100,000
        Assert.Equal(0, calls);
        Assert.True(world.TimerQueueContains(item, now + 600_000));
    }

    [Fact]
    public void ClearingOrDeletingATimerRemovesItsEntry()
    {
        var world = TestHarness.CreateWorld();
        var cleared = world.CreateItem();
        var deleted = world.CreateItem();
        long future = Environment.TickCount64 + 600_000;
        cleared.SetTimeout(future);
        deleted.SetTimeout(future);
        Assert.Equal(2, world.TimerQueueCount);

        cleared.SetTimeout(0);
        Assert.Equal(1, world.TimerQueueCount);
        world.DeleteObject(deleted);
        Assert.Equal(0, world.TimerQueueCount);
    }

    [Fact]
    public void ARearmToAnEarlierDeadlineFiresAtTheEarlierOne()
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        int calls = 0;
        Item.OnTimerExpired = _ => { calls++; return TriggerResult.True; };

        item.SetTimeout(Environment.TickCount64 + 600_000);
        item.SetTimeout(Environment.TickCount64 - 1);   // pulled in: sift up
        world.OnTick();
        Assert.Equal(1, calls);

        item.SetTimeout(Environment.TickCount64 - 1);
        item.SetTimeout(Environment.TickCount64 + 600_000);   // pushed out: sift down
        world.OnTick();
        Assert.Equal(1, calls);
        Assert.Equal(1, world.TimerQueueCount);
    }

    [Fact]
    public void TheInspectionBudgetSpreadsABacklogWithoutLosingACallback()
    {
        var world = TestHarness.CreateWorld();
        world.MaxItemTimerInspectionsPerTick = 100;
        long past = Environment.TickCount64 - 1000;
        var items = new List<Item>();
        for (int i = 0; i < 1000; i++)
        {
            var it = world.CreateItem();
            it.SetTimeout(past + i);
            items.Add(it);
        }
        var fired = new Dictionary<Item, int>();
        Item.OnTimerExpired = it =>
        {
            fired[it] = fired.GetValueOrDefault(it) + 1;
            return TriggerResult.True;
        };

        world.OnTick();
        Assert.Equal(100, fired.Count);
        int ticks = 1;
        while (world.TimerQueueCount > 0 && ticks < 50)
        {
            world.OnTick();
            ticks++;
        }

        _out.WriteLine($"1000 due timers, 100 inspections/tick: drained in {ticks} ticks, " +
                       $"cap hits={world.TimerQueueInspectionCapHits}");
        Assert.Equal(10, ticks);
        Assert.Equal(1000, fired.Count);
        Assert.All(fired.Values, n => Assert.Equal(1, n));
    }

    [Fact]
    public void TimersFireInDeadlineOrderAfterArbitraryRearms()
    {
        var world = TestHarness.CreateWorld();
        var rng = new Random(1234);
        // Deadlines must be positive (0 and below mean "no timer") and already due.
        // TickCount64 is the machine uptime: a fresh CI runner has been up for only a few
        // minutes, so "now - 1,000,000" went negative there and nothing fired.
        long now = Environment.TickCount64;
        long baseTime = 1;
        int span = (int)Math.Clamp(now - 2, 1, 100_000);
        var items = Enumerable.Range(0, 500).Select(_ => world.CreateItem()).ToList();
        var expected = new Dictionary<Item, long>();
        for (int op = 0; op < 20_000; op++)
        {
            var it = items[rng.Next(items.Count)];
            if (rng.Next(10) == 0)
            {
                it.SetTimeout(0);
                expected.Remove(it);
            }
            else
            {
                long d = baseTime + rng.Next(span);
                it.SetTimeout(d);
                expected[it] = d;
            }
        }
        Assert.Equal(expected.Count, world.TimerQueueCount);

        var order = new List<Item>();
        Item.OnTimerExpired = it => { order.Add(it); return TriggerResult.True; };
        world.OnTick();

        Assert.Equal(expected.Count, order.Count);
        var deadlines = order.Select(i => expected[i]).ToList();
        Assert.Equal(deadlines.OrderBy(d => d), deadlines);
        Assert.Equal(expected.Keys.ToHashSet(), order.ToHashSet());
    }

    [Fact]
    public void TheTimerAuditDoesNotReportAQueuedTimer()
    {
        var world = TestHarness.CreateWorld();
        var item = world.CreateItem();
        item.SetTimeout(Environment.TickCount64 + 600_000);
        Assert.Equal(0, world.AuditTimerRegistrations(
            Environment.TickCount64 + GameWorld.DecayAuditIntervalMs));
        Assert.Equal(1, world.TimerQueueCount);
    }

    // ----------------------------------------------------------------- decay

    [Fact]
    public void AThousandWritesOfOneDecayDeadlineAreOneEntryAndOneReturn()
    {
        var world = TestHarness.CreateWorld();
        var item = Ground(world, 1);
        long deadline = Environment.TickCount64 + 100;
        for (int i = 0; i < 1000; i++)
            item.SetDecayAt(deadline);

        Assert.Equal(1, world.DecayQueueCount);   // was 1,000
        var due = Due(world, deadline + 1000);
        _out.WriteLine($"1000 same-deadline writes: queue before=1, collected {due.Count}, " +
                       $"distinct {due.Distinct().Count()}");
        Assert.Single(due);                        // was 256 copies of one item
        Assert.Equal(0, world.DecayQueueCount);
    }

    [Fact]
    public void HundredThousandDecayRearmsLeaveOneEntryAndACollectionDoesNoStaleWork()
    {
        var world = TestHarness.CreateWorld();
        var item = Ground(world, 1);
        long now = Environment.TickCount64;
        for (int i = 0; i < 100_000; i++)
            item.SetDecayAt(now - 200_000 + i);
        item.SetDecayAt(now + 600_000);

        long droppedBefore = world.DecayQueueStaleDropped;
        var sw = Stopwatch.StartNew();
        var due = Due(world, now);
        sw.Stop();

        _out.WriteLine($"queue={world.DecayQueueCount} stale_dropped=" +
                       $"{world.DecayQueueStaleDropped - droppedBefore} collect={sw.Elapsed.TotalMilliseconds:F3}ms");
        Assert.Empty(due);
        Assert.Equal(1, world.DecayQueueCount);                      // was 100,001
        Assert.Equal(0, world.DecayQueueStaleDropped - droppedBefore); // was 100,000
        Assert.Single(Due(world, now + 700_000));
    }

    [Fact]
    public void DecayClearDeleteAndDifferentDeadlines()
    {
        var world = TestHarness.CreateWorld();
        long now = Environment.TickCount64;
        var cleared = Ground(world, 1);
        var deleted = Ground(world, 2);
        var pulledIn = Ground(world, 3);
        var pushedOut = Ground(world, 4);
        cleared.SetDecayAt(now + 100);
        deleted.SetDecayAt(now + 100);
        pulledIn.SetDecayAt(now + 600_000);
        pushedOut.SetDecayAt(now + 100);
        Assert.Equal(4, world.DecayQueueCount);

        cleared.ClearDecay();
        world.DeleteObject(deleted);
        pulledIn.SetDecayAt(now + 50);
        pushedOut.SetDecayAt(now + 600_000);
        Assert.Equal(2, world.DecayQueueCount);

        Assert.Equal([pulledIn], Due(world, now + 1000));
        Assert.Equal([pushedOut], Due(world, now + 700_000));
    }

    [Fact]
    public void TheDecayInspectionBudgetBoundsDeadEntriesAndLosesNothing()
    {
        var world = TestHarness.CreateWorld();
        world.MaxDecayInspectionsPerCall = 100;
        long now = Environment.TickCount64;

        // 1,000 armed items that then went into a bag without the world hearing
        // about it (Item.AddItem): their entries are dead the moment they surface.
        // (One bag per 100 items keeps clear of container item-count limits.)
        Item pack = null!;
        for (short i = 0; i < 1000; i++)
        {
            if (i % 100 == 0)
            {
                pack = world.CreateItem();
                pack.BaseId = 0x0E75;
                pack.ItemType = ItemType.Container;
                world.PlaceItem(pack, new Point3D((short)(300 + i / 100), 300, 0, 0));
            }
            var it = Ground(world, (short)(10 + i % 500));
            it.SetDecayAt(now + 10);
            pack.AddItem(it);
            Assert.False(it.IsOnGround);
        }
        var live = Ground(world, 1);
        live.SetDecayAt(now + 20);

        int calls = 0;
        List<Item> due;
        do
        {
            due = Due(world, now + 1000);
            calls++;
            Assert.True(due.Count <= 1);
        } while (due.Count == 0 && calls < 50);

        _out.WriteLine($"1000 dead + 1 live entry, 100 inspections/call: live item " +
                       $"returned on call {calls}, cap hits={world.DecayQueueInspectionCapHits}");
        Assert.Equal(11, calls);
        Assert.Equal([live], due);
        Assert.Equal(0, world.DecayQueueCount);
    }
}
