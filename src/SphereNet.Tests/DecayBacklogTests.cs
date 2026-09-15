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
/// What a decay backlog costs, and what it must not lose (review work item D04).
///
/// Ground-item decay is a due-ordered queue drained at most 256 items per tick, and
/// that cap is the documented contract. What nothing measured is the shape of the
/// backlog it is there for: a world load arrives with every deadline it was stored
/// with, most of them already past, and the queue has to work through them without
/// dropping one, running one twice, or taking a tick hostage.
///
/// The number worth writing down is the drain rate, because it is what an operator sees
/// after a restart: at 256 per tick and ten ticks a second, a backlog clears at 2,560
/// items a second, and the oldest deadline goes first.
/// </summary>
public sealed class DecayBacklogTests
{
    private readonly ITestOutputHelper _out;
    public DecayBacklogTests(ITestOutputHelper output) => _out = output;

    private const int PerTickCap = 256;

    private static GameWorld World()
    {
        var w = new GameWorld(LoggerFactory.Create(_ => { }));
        w.InitMap(0, 2048, 2048);
        ObjBase.ResolveWorld = () => w;
        Item.ResolveWorld = () => w;
        return w;
    }

    /// <summary>Ground items with decay deadlines spaced <paramref name="stepMs"/>
    /// apart, item 0 the earliest. Decay is armed relative to the real clock - the
    /// engine's own door for it takes an interval, not an instant - so the collection
    /// time below is simply far enough past all of them.</summary>
    private static (List<Item> Items, long AllDue) Armed(GameWorld world, int count, int stepMs)
    {
        long baseNow = Environment.TickCount64;
        var items = new List<Item>(count);
        for (int i = 0; i < count; i++)
        {
            var item = world.CreateItem();
            item.BaseId = 0x0EED;
            world.PlaceItem(item, new Point3D((short)(100 + i % 40), (short)(100 + i / 40), 0, 0));
            item.SetDecayTime((i + 1) * stepMs);
            items.Add(item);
        }
        // Past every deadline, with room for the time the loop above took.
        return (items, baseNow + (long)(count + 1) * stepMs + 60_000);
    }

    // ---- the backlog -----------------------------------------------------

    [Fact]
    public void ABacklogDrainsAtTheDocumentedRateAndLosesNothing()
    {
        var world = World();
        const int Backlog = 5000;
        var (_, now) = Armed(world, Backlog, stepMs: 1);

        var collected = new List<Item>();
        var buffer = new List<Item>();
        int ticks = 0;
        int worstPerTick = 0;

        while (collected.Count < Backlog && ticks < 1000)
        {
            buffer.Clear();
            world.CollectDueDecay(now, PerTickCap, buffer);
            if (buffer.Count == 0) break;
            worstPerTick = Math.Max(worstPerTick, buffer.Count);
            collected.AddRange(buffer);
            ticks++;
        }

        _out.WriteLine($"{Backlog} overdue items: cleared in {ticks} ticks, " +
                       $"worst tick {worstPerTick}, {collected.Count} collected " +
                       $"({Backlog / (double)PerTickCap:F0} ticks expected, " +
                       $"{Backlog / 2560.0:F1}s at ten ticks a second)");

        // Everything, exactly once, and never more than the cap in one tick.
        Assert.Equal(Backlog, collected.Count);
        Assert.Equal(Backlog, collected.Select(i => i.Uid.Value).Distinct().Count());
        Assert.True(worstPerTick <= PerTickCap, $"one tick took {worstPerTick}");
        Assert.Equal((int)Math.Ceiling(Backlog / (double)PerTickCap), ticks);
    }

    [Fact]
    public void TheOldestDeadlineGoesFirst()
    {
        var world = World();
        // Widely spaced so the time the creation loop itself takes cannot reorder
        // neighbouring deadlines.
        var (_, now) = Armed(world, 200, stepMs: 50);

        var collected = new List<Item>();
        var buffer = new List<Item>();
        for (int t = 0; t < 10; t++)
        {
            buffer.Clear();
            world.CollectDueDecay(now, PerTickCap, buffer);
            collected.AddRange(buffer);
        }
        Assert.Equal(200, collected.Count);

        // Due order, not insertion order: a backlog that came back in the wrong order
        // would leave the oldest corpse on the ground while newer ones went.
        var deadlines = collected.Select(i => i.DecayTime).ToList();
        _out.WriteLine($"first three deadlines: {string.Join(",", deadlines.Take(3))}; " +
                       $"last three: {string.Join(",", deadlines.TakeLast(3))}");
        for (int i = 1; i < deadlines.Count; i++)
            Assert.True(deadlines[i] >= deadlines[i - 1],
                $"item {i} was due at {deadlines[i]}, after one due at {deadlines[i - 1]}");
    }

    [Fact]
    public void NothingComesBackASecondTime()
    {
        var world = World();
        var (_, now) = Armed(world, 600, stepMs: 1);

        var seen = new HashSet<uint>();
        var buffer = new List<Item>();
        int duplicates = 0;
        for (int t = 0; t < 20; t++)
        {
            buffer.Clear();
            world.CollectDueDecay(now, PerTickCap, buffer);
            foreach (var item in buffer)
                if (!seen.Add(item.Uid.Value)) duplicates++;
        }

        _out.WriteLine($"600 items over 20 ticks: {seen.Count} collected, {duplicates} duplicates");
        Assert.Equal(600, seen.Count);
        Assert.Equal(0, duplicates);
    }

    // ---- what changes while the backlog waits ----------------------------

    [Fact]
    public void AnItemReArmedWhileItWaitsIsNotDecayedOnItsOldDeadline()
    {
        var world = World();
        var (items, now) = Armed(world, 300, stepMs: 1);

        // One of them is picked up and put back down with a fresh timer while the
        // backlog in front of it is still draining.
        var reArmed = items[250];
        reArmed.SetDecayTime(600_000);

        var collected = new List<Item>();
        var buffer = new List<Item>();
        for (int t = 0; t < 5; t++)
        {
            buffer.Clear();
            world.CollectDueDecay(now, PerTickCap, buffer);
            collected.AddRange(buffer);
        }

        _out.WriteLine($"re-armed item collected: {collected.Contains(reArmed)}");

        // Its old entry is still in the queue and must retire rather than decay an
        // item whose deadline has moved.
        Assert.DoesNotContain(reArmed, collected);
        Assert.Equal(299, collected.Count);
    }

    [Fact]
    public void AnItemDeletedWhileItWaitsIsSkipped()
    {
        var world = World();
        var (items, now) = Armed(world, 300, stepMs: 1);

        var gone = items[100];
        world.DeleteObject(gone);
        gone.Delete();

        var collected = new List<Item>();
        var buffer = new List<Item>();
        for (int t = 0; t < 5; t++)
        {
            buffer.Clear();
            world.CollectDueDecay(now, PerTickCap, buffer);
            collected.AddRange(buffer);
        }

        Assert.DoesNotContain(gone, collected);
        Assert.Equal(299, collected.Count);
    }

    [Fact]
    public void ADeadlineInTheFutureIsNotCollectedEarly()
    {
        // The control for all of the above: the drain must be taking only what is due.
        var world = World();
        var (_, now) = Armed(world, 100, stepMs: 1);

        var later = world.CreateItem();
        later.BaseId = 0x0EED;
        world.PlaceItem(later, new Point3D(300, 300, 0, 0));
        later.SetDecayTime(10 * 60_000);      // ten minutes out

        var buffer = new List<Item>();
        world.CollectDueDecay(now, PerTickCap, buffer);

        Assert.Equal(100, buffer.Count);
        Assert.DoesNotContain(later, buffer);

        // And it does come when its time arrives.
        buffer.Clear();
        world.CollectDueDecay(now + 10 * 60_000, PerTickCap, buffer);
        Assert.Contains(later, buffer);
    }
}
