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
/// Every way an item can come due and survive, checked against the registration audit.
///
/// The world keeps decay deadlines in a due-ordered queue and audits it once a minute,
/// warning when it finds an item that is armed but unqueued. A live log carried that
/// warning in bulk at startup and then a trickle, which means the collector hands an
/// item out and something puts it back in that state.
///
/// The collector removes the entry; from then on the item is only queued again if
/// something re-arms it. So for every path where a due item SURVIVES its tick, the
/// question is the same: did that path leave a deadline standing with no entry behind
/// it? This walks each of those paths and asks the audit.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DecayLifecycleAuditTests
{
    private readonly ITestOutputHelper _out;
    public DecayLifecycleAuditTests(ITestOutputHelper output) => _out = output;

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
        item.SetAttr(ObjAttributes.Decay);
        world.PlaceItem(item, new Point3D(x, 100, 0, 0));
        return item;
    }

    /// <summary>One maintenance pass, as the main loop runs it: collect what is due,
    /// tick each of them, delete whatever the tick killed - then audit.</summary>
    private static int PassAndAudit(GameWorld world, long now)
    {
        var due = new List<Item>();
        world.CollectDueDecay(now, 256, due);
        foreach (var item in due)
        {
            _ = item.OnTick();
            if (item.IsDeleted)
                world.DeleteObject(item);
        }
        // Far enough ahead that the once-a-minute audit is allowed to run.
        return world.AuditDecayRegistrations(now + GameWorld.DecayAuditIntervalMs + 1);
    }

    [Fact]
    public void APlainGroundItemRotsAndLeavesNothingBehind()
    {
        var world = World();
        var item = Ground(world);
        item.SetDecayAt(System.Environment.TickCount64 - 1);   // already past due

        int repaired = PassAndAudit(world, System.Environment.TickCount64);

        _out.WriteLine($"deleted={item.IsDeleted} audit repaired={repaired}");
        Assert.True(item.IsDeleted);
        Assert.Equal(0, repaired);
    }

    [Fact]
    public void AScriptThatKeepsTheItemLeavesItQueuedAgain()
    {
        // @Timer RETURN 1: the item survives and the tick pushes its decay out. The
        // push has to go through the arming door, or the item is armed and unqueued.
        var world = World();
        var item = Ground(world);
        item.SetDecayAt(System.Environment.TickCount64 - 1);
        Item.OnTimerExpired = _ => SphereNet.Core.Enums.TriggerResult.True;
        try
        {
            int repaired = PassAndAudit(world, System.Environment.TickCount64);

            _out.WriteLine($"deleted={item.IsDeleted} decay={item.DecayTime} audit repaired={repaired}");
            Assert.False(item.IsDeleted);
            Assert.Equal(0, repaired);
        }
        finally
        {
            Item.OnTimerExpired = null;
        }
    }

    [Fact]
    public void ACorpseThatStagesItselfStaysQueued()
    {
        // A player corpse turns to bones rather than vanishing: the handler answers
        // "not consumed" and re-arms the deadline itself.
        var world = World();
        var corpse = world.CreateItem();
        corpse.BaseId = 0x2006;
        corpse.ItemType = ItemType.Corpse;
        corpse.SetAttr(ObjAttributes.Decay);
        world.PlaceItem(corpse, new Point3D(100, 100, 0, 0));
        corpse.SetDecayAt(System.Environment.TickCount64 - 1);

        Item.OnCorpseDecay = c =>
        {
            c.SetDecayAt(System.Environment.TickCount64 + 60_000);
            return false;                       // staged, not consumed
        };
        try
        {
            int repaired = PassAndAudit(world, System.Environment.TickCount64);

            _out.WriteLine($"deleted={corpse.IsDeleted} decay={corpse.DecayTime} audit repaired={repaired}");
            Assert.False(corpse.IsDeleted);
            Assert.Equal(0, repaired);
        }
        finally
        {
            Item.OnCorpseDecay = null;
        }
    }

    [Fact]
    public void AnItemPickedUpBeforeItRotsLeavesNothingBehind()
    {
        // The leak this began from: due, but no longer on the ground.
        var world = World();
        var item = Ground(world);
        item.SetDecayAt(System.Environment.TickCount64 - 1);

        var bag = world.CreateItem();
        bag.BaseId = 0x0E75;
        bag.ItemType = ItemType.Container;
        world.PlaceItem(bag, new Point3D(105, 100, 0, 0));
        world.HideFromSector(item);
        bag.TryAddItem(item);

        int repaired = PassAndAudit(world, System.Environment.TickCount64);

        _out.WriteLine($"decay={item.DecayTime} audit repaired={repaired}");
        Assert.Equal(0, repaired);
    }

    [Fact]
    public void AWholeBatchComingDueAtOnceSettles()
    {
        // The startup shape: a save restores many items whose remaining decay was
        // already spent, so they all come due on the first tick.
        var world = World();
        var items = new List<Item>();
        for (short i = 0; i < 40; i++)
        {
            var it = Ground(world, (short)(100 + i));
            it.SetDecayAt(System.Environment.TickCount64 - 1);
            items.Add(it);
        }

        int repaired = PassAndAudit(world, System.Environment.TickCount64);

        _out.WriteLine($"{items.Count(i => i.IsDeleted)} of {items.Count} rotted; " +
                       $"audit repaired {repaired}");
        Assert.Equal(0, repaired);
    }
}
