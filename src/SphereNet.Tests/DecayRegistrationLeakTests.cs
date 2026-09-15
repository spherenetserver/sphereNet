using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// An armed decay that the due queue does not hold.
///
/// The world keeps decay deadlines in a due-ordered queue and audits the registration
/// once a minute, saying so when it finds an armed item the queue is missing. A live
/// log carried that warning repeatedly - 196 of them at startup, then one or two a
/// minute afterwards - which means something was arming a deadline and losing the
/// entry, over and over.
///
/// It was the collector. An item that came due while it was no longer on the ground -
/// picked up, bagged, worn between arming and the deadline - was dropped from the queue
/// with its deadline left standing. Decay belongs to top-level items: upstream turns a
/// decay request on anything else into no timer at all (CItem::SetDecayTime,
/// CItem.cpp:1493). Leaving it armed made the item permanently armed AND permanently
/// unqueued, so the audit re-queued it every minute, found it off the ground again, and
/// dropped it again - for as long as the item existed.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DecayRegistrationLeakTests
{
    private readonly ITestOutputHelper _out;
    public DecayRegistrationLeakTests(ITestOutputHelper output) => _out = output;

    private static GameWorld World()
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Ground(GameWorld world, short x = 100)
    {
        var item = world.CreateItem();
        item.BaseId = 0x0EED;
        world.PlaceItem(item, new Point3D(x, 100, 0, 0));
        return item;
    }

    [Fact]
    public void AnItemThatLeavesTheGroundStopsBeingArmed()
    {
        var world = World();
        var item = Ground(world);
        item.SetDecayAt(1000);

        // Somebody bags it before it rots.
        var bag = world.CreateItem();
        bag.BaseId = 0x0E75;
        bag.ItemType = ItemType.Container;
        world.PlaceItem(bag, new Point3D(105, 100, 0, 0));
        world.HideFromSector(item);
        Assert.True(bag.TryAddItem(item));

        var due = new List<Item>();
        world.CollectDueDecay(2000, 64, due);

        _out.WriteLine($"collected {due.Count}; decay now {item.DecayTime}");
        Assert.DoesNotContain(item, due);          // it must not rot inside the bag
        Assert.Equal(0, item.DecayTime);           // ...and it must not stay armed
    }

    [Fact]
    public void TheAuditFindsNothingToRepairAfterwards()
    {
        // The symptom, stated directly: the audit is a check, not a mechanism. If it
        // keeps finding work, something upstream of it is leaking.
        var world = World();
        var item = Ground(world);
        item.SetDecayAt(1000);

        var bag = world.CreateItem();
        bag.BaseId = 0x0E75;
        bag.ItemType = ItemType.Container;
        world.PlaceItem(bag, new Point3D(105, 100, 0, 0));
        world.HideFromSector(item);
        bag.TryAddItem(item);

        var due = new List<Item>();
        world.CollectDueDecay(2000, 64, due);

        // Far enough ahead that the once-a-minute audit is allowed to run.
        int repaired = world.AuditDecayRegistrations(GameWorld.DecayAuditIntervalMs + 5000);
        _out.WriteLine($"audit repaired {repaired} registration(s)");
        Assert.Equal(0, repaired);
    }

    [Fact]
    public void AGroundItemStillRots()
    {
        // The control: the collector's job is unchanged for the case it exists for.
        var world = World();
        var item = Ground(world);
        item.SetDecayAt(1000);

        var due = new List<Item>();
        world.CollectDueDecay(2000, 64, due);

        Assert.Contains(item, due);
    }

    [Fact]
    public void AnItemThatIsNotDueYetIsLeftAlone()
    {
        var world = World();
        var item = Ground(world);
        item.SetDecayAt(10_000);

        var due = new List<Item>();
        world.CollectDueDecay(2000, 64, due);

        Assert.Empty(due);
        Assert.Equal(10_000, item.DecayTime);
    }

    [Fact]
    public void ARearmedItemKeepsTheLaterDeadline()
    {
        // A stale entry must not be able to disarm an item that was re-armed after it.
        var world = World();
        var item = Ground(world);
        item.SetDecayAt(1000);
        item.SetDecayAt(50_000);

        var due = new List<Item>();
        world.CollectDueDecay(2000, 64, due);

        Assert.Empty(due);
        Assert.Equal(50_000, item.DecayTime);
    }
}
