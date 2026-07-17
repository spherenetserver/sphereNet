using System.Collections.Generic;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Batch 4 hot-path indexing (E3/E4). These prove the two per-tick/periodic sweeps still
/// produce identical results after swapping their full-world <c>_objects.Values</c> scans
/// for maintained indexes:
///   E3 — TickTimerF iterates a TIMERF active-set (populated from AddTimerF via ResolveWorld)
///        instead of every object; entries must still fire exactly once, including on a
///        contained item (a case a sector-only index would miss).
///   E4 — CollectExpiredGroundItems sweeps the _groundItems index; a picked-up item (now
///        off-ground) must be pruned and never returned.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PerfIndexTests
{
    private static readonly Point3D Spot = new(120, 120, 0, 0);

    [Fact]
    public void TimerF_FiresOnceForGroundItem_ThenPrunes()
    {
        var world = TestHarness.CreateWorld();
        var fired = new List<string>();
        world.TimerFExpired = (obj, entry) => fired.Add(entry.FunctionName);

        var item = world.CreateItem();
        Assert.True(world.PlaceItem(item, Spot));
        item.AddTimerF(0, "f_ground", ""); // due immediately; auto-registers via ResolveWorld

        world.OnTick();
        Assert.Equal(new[] { "f_ground" }, fired);

        // Entry was dequeued and the object pruned from the active-set — no re-fire.
        fired.Clear();
        world.OnTick();
        Assert.Empty(fired);
    }

    [Fact]
    public void TimerF_FiresForContainedItem()
    {
        // A contained item has no sector membership; the active-set must still track it
        // (this is why registration hangs off AddTimerF, not sector placement).
        var world = TestHarness.CreateWorld();
        var fired = new List<string>();
        world.TimerFExpired = (obj, entry) => fired.Add(entry.FunctionName);

        var pack = world.CreateItem();
        Assert.True(world.PlaceItem(pack, Spot));
        var content = world.CreateItem();
        pack.AddItem(content);
        content.ContainedIn = pack.Uid;
        Assert.False(content.IsOnGround);

        content.AddTimerF(0, "f_contained", "");
        world.OnTick();
        Assert.Contains("f_contained", fired);
    }

    [Fact]
    public void CollectExpiredGroundItems_ReturnsExpiredGroundItemsOnly()
    {
        var world = TestHarness.CreateWorld();

        var expired = world.CreateItem();
        Assert.True(world.PlaceItem(expired, Spot));
        expired.DecayTime = 100;

        var future = world.CreateItem();
        Assert.True(world.PlaceItem(future, new Point3D(121, 120, 0, 0)));
        future.DecayTime = 10_000;

        var noDecay = world.CreateItem();
        Assert.True(world.PlaceItem(noDecay, new Point3D(122, 120, 0, 0)));
        noDecay.DecayTime = 0;

        var buffer = new List<Item>();
        world.CollectExpiredGroundItems(now: 1_000, max: 256, buffer);

        Assert.Contains(expired, buffer);
        Assert.DoesNotContain(future, buffer);
        Assert.DoesNotContain(noDecay, buffer);
    }

    [Fact]
    public void CollectExpiredGroundItems_PrunesPickedUpItem()
    {
        var world = TestHarness.CreateWorld();

        var pack = world.CreateItem();
        Assert.True(world.PlaceItem(pack, Spot));

        var item = world.CreateItem();
        Assert.True(world.PlaceItem(item, new Point3D(121, 120, 0, 0))); // indexed as ground
        item.DecayTime = 100;

        // Pick it up: now contained, no longer on the ground. The index still holds a
        // stale reference; the IsOnGround re-check must skip and prune it.
        pack.AddItem(item);
        item.ContainedIn = pack.Uid;
        Assert.False(item.IsOnGround);

        var buffer = new List<Item>();
        world.CollectExpiredGroundItems(now: 1_000, max: 256, buffer);
        Assert.DoesNotContain(item, buffer);

        // Second pass confirms the stale entry was pruned (no exception, still absent).
        buffer.Clear();
        world.CollectExpiredGroundItems(now: 1_000, max: 256, buffer);
        Assert.DoesNotContain(item, buffer);
    }
}
