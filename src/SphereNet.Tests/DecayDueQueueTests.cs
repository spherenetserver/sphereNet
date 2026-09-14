using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Decay is reached by a due-ordered queue, so finding expired items costs what is
/// due rather than what exists.
///
/// The pass this replaced walked every ground item in the world every five seconds:
/// measured at 300,000 items it took 11 ms to find nothing at all, on the server
/// thread, twelve times a minute. The five-second cadence was the price of that
/// walk, not a requirement — with a queue the check runs every tick and costs
/// nothing when nothing is due.
///
/// The queue carries the deadline each entry was made with. That is what makes
/// re-arming free (push a new entry, the old one is dropped when it surfaces) and
/// cancelling not need a removal at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DecayDueQueueTests
{
    private readonly ITestOutputHelper _out;
    public DecayDueQueueTests(ITestOutputHelper output) => _out = output;

    private GameWorld _world = null!;

    /// <summary>Built in the test body: ResetEngineStatics runs after the constructor
    /// and clears the ambient world resolvers the registration door goes through.</summary>
    private void Setup()
    {
        _world = new GameWorld(LoggerFactory.Create(_ => { }));
        _world.InitMap(0, 1024, 1024);
        ObjBase.ResolveWorld = () => _world;
        Item.ResolveWorld = () => _world;
    }

    private Item Ground(short x, long decayInMs)
    {
        var item = _world.CreateItem();
        item.BaseId = 0x0EED;
        _world.PlaceItem(item, new Point3D(x, 100, 0, 0));
        if (decayInMs != 0)
            item.SetDecayAt(Environment.TickCount64 + decayInMs);
        return item;
    }

    private List<Item> Due(long now, int max = 256)
    {
        var buffer = new List<Item>();
        _world.CollectDueDecay(now, max, buffer);
        return buffer;
    }

    // ------------------------------------------------------------------

    [Fact]
    public void NothingDueCostsNothing()
    {
        Setup();
        for (short i = 0; i < 500; i++)
            Ground(i, decayInMs: 600_000);

        var due = Due(Environment.TickCount64);

        // The old pass walked all 500 to answer this. The queue answers it by looking
        // at one entry - the earliest - and stopping.
        _out.WriteLine($"500 armed items, none due: collected {due.Count}, queue holds {_world.DecayQueueCount}");
        Assert.Empty(due);
        Assert.Equal(500, _world.DecayQueueCount);
    }

    [Fact]
    public void ItemsComeBackInDeadlineOrder()
    {
        Setup();
        var third = Ground(1, 300);
        var first = Ground(2, 100);
        var second = Ground(3, 200);

        var due = Due(Environment.TickCount64 + 1000);

        _out.WriteLine(string.Join(", ", due.Select(i => i.Uid.Value.ToString("X8"))));
        Assert.Equal([first.Uid.Value, second.Uid.Value, third.Uid.Value],
                     due.Select(i => i.Uid.Value).ToArray());
    }

    [Fact]
    public void ARearmedItemIsNotCollectedOnItsOldDeadline()
    {
        Setup();
        var item = Ground(1, 100);

        // Push the deadline out. The queue still holds the first entry - nothing
        // removes it - so the stale entry has to be recognised when it surfaces,
        // which is the whole reason entries carry their deadline.
        item.SetDecayAt(Environment.TickCount64 + 500_000);
        Assert.Equal(2, _world.DecayQueueCount);   // both entries; one is already dead weight

        var due = Due(Environment.TickCount64 + 1000);

        // Collecting consumes the stale entry on the way past, so the count has to be
        // read BEFORE the drain - reading it after measures the cleanup, not the
        // duplicate.
        _out.WriteLine($"after the drain: queue={_world.DecayQueueCount}, collected {due.Count}");
        Assert.Empty(due);
        Assert.Equal(1, _world.DecayQueueCount);   // only the live entry is left
    }

    [Fact]
    public void ACancelledDecayIsNotCollected()
    {
        Setup();
        var item = Ground(1, 100);
        item.ClearDecay();

        Assert.Empty(Due(Environment.TickCount64 + 1000));
        _out.WriteLine("cleared decay leaves a stale entry behind and collects nothing");
    }

    [Fact]
    public void ADeletedItemIsNotCollected()
    {
        Setup();
        var item = Ground(1, 100);
        _world.DeleteObject(item);

        Assert.Empty(Due(Environment.TickCount64 + 1000));
    }

    [Fact]
    public void TheCapBoundsOneCallAndTheRestAreStillWaiting()
    {
        Setup();
        for (short i = 0; i < 40; i++)
            Ground(i, decayInMs: 10);

        long later = Environment.TickCount64 + 1000;
        var first = Due(later, max: 16);
        var second = Due(later, max: 16);

        // The cap bounds the work of one tick. What it does NOT do any more is send
        // the remainder to the back of another full scan five seconds later: they are
        // simply the front of the next call.
        _out.WriteLine($"40 due, cap 16: first={first.Count}, second={second.Count}");
        Assert.Equal(16, first.Count);
        Assert.Equal(16, second.Count);
        Assert.Empty(first.Intersect(second));
    }

    [Fact]
    public void TheAuditFindsAndReQueuesAnArmedItemTheQueueNeverSaw()
    {
        Setup();
        var item = Ground(1, 100);

        // Drain the queue so the item is armed but unqueued - the shape of an item
        // whose deadline was set somewhere the registration door could not reach, and
        // which would therefore simply never decay.
        Due(Environment.TickCount64 + 1000);
        Assert.Equal(0, _world.DecayQueueCount);
        Assert.True(item.DecayTime > 0);

        int missing = _world.AuditDecayRegistrations(
            Environment.TickCount64 + GameWorld.DecayAuditIntervalMs);

        _out.WriteLine($"audit re-queued {missing} armed item(s)");
        Assert.Equal(1, missing);
        Assert.Equal(1, _world.DecayQueueCount);

        // And it says so rather than quietly patching the hole: an item that needs
        // the auditor is evidence of a registration that did not happen.
        Assert.Single(Due(Environment.TickCount64 + 1000));
    }

    [Fact]
    public void TheAuditRunsOnItsOwnCadenceAndNotEveryTick()
    {
        Setup();
        Ground(1, 100);
        Due(Environment.TickCount64 + 1000);

        long t0 = Environment.TickCount64 + GameWorld.DecayAuditIntervalMs;
        Assert.Equal(1, _world.AuditDecayRegistrations(t0));

        // The auditor is the old full scan. It is affordable once a minute and was
        // not affordable every five seconds, so calling it again immediately has to
        // do nothing at all.
        Ground(2, 100);
        Due(Environment.TickCount64 + 1000);
        Assert.Equal(0, _world.AuditDecayRegistrations(t0 + 1));

        _out.WriteLine($"second call inside the interval re-queued nothing (interval " +
                       $"{GameWorld.DecayAuditIntervalMs} ms)");
    }

    [Fact]
    public void AnItemPickedUpOffTheGroundIsNotCollected()
    {
        Setup();
        var pack = _world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = SphereNet.Core.Enums.ItemType.Container;
        _world.PlaceItem(pack, new Point3D(300, 300, 0, 0));

        var item = Ground(1, 100);
        pack.AddItem(item);      // now contained, no longer a ground item

        // Decay is a ground-item rule. The queue entry from when it lay on the floor
        // must not reach in and delete something out of a player's bag.
        Assert.Empty(Due(Environment.TickCount64 + 1000));
        _out.WriteLine("an item that left the ground keeps its deadline but is not collected");
    }
}
