using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A full spawner parks its timer, and getting a child back is what restarts it.
///
/// TIMER = -1 on a spawn gem reads like a broken spawner and is not one: upstream
/// writes exactly that when the quota fills (CCSpawn::AddObj, CCSpawn.cpp:645) so a
/// spawner with nothing to do sits in no schedule at all. The whole design then
/// rests on ONE edge — losing a child has to be an event, because a parked spawner
/// has no deadline and a due-ordered tick can never reach it again (CCSpawn::DelObj,
/// :509). Miss that edge and the world quietly stops spawning, with no error and no
/// slow tick to point at it.
///
/// So these pin the edge itself, per way a child can be lost: a script REMOVE (what
/// the live pack's self-deleting mounts do), death, and the deliberate exception —
/// the operator's own teardown, which upstream keeps silent on purpose by clearing
/// the spawn link before deleting (CCSpawn::KillChildren, :727).
/// </summary>
public sealed class SpawnerRearmOnChildLossTests(ITestOutputHelper output)
{
    private static Item Spawner(GameWorld world, Point3D at, int max = 1)
    {
        var spawner = world.CreateItem();
        spawner.BaseId = 0x1EA7;                    // i_worldgem_bit
        spawner.ItemType = ItemType.SpawnChar;
        spawner.SetAttr(ObjAttributes.Invis);
        spawner.SpawnChar = new SpawnComponent(spawner, world)
            { CharDefId = 0x00C8, MaxCount = max, SpawnRange = 0 };
        world.PlaceItem(spawner, at);
        return spawner;
    }

    /// <summary>Fill the spawner to its cap and return the parked gem.</summary>
    private Item FilledSpawner(GameWorld world, out SpawnComponent spawn)
    {
        var gem = Spawner(world, new Point3D(140, 140, 0, 0));
        spawn = gem.SpawnChar!;
        spawn.ForceSpawn();
        spawn.OnTick(Environment.TickCount64);

        Assert.Equal(1, spawn.CurrentCount);
        // Parked: no deadline at all, which is what TIMER reads back as -1.
        Assert.True(gem.Timeout <= 0,
            $"a full spawner should hold no deadline, found {gem.Timeout}");
        return gem;
    }

    [Fact]
    public void AScriptRemoveOnTheChildRestartsTheSpawner()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        try
        {
            var gem = FilledSpawner(world, out var spawn);
            var child = world.GetAllObjects()
                .OfType<SphereNet.Game.Objects.Characters.Character>()
                .Single(c => c.TryGetTag("SPAWN_POINT_UUID", out _));

            // Exactly what I_REMOVETIMER_MEM does to a horse it is equipped on:
            // TOPOBJ.REMOVE, through the ordinary verb, with no spawner in sight.
            Assert.True(child.TryExecuteCommand("REMOVE", "", null!));

            output.WriteLine($"after the child was REMOVEd: count={spawn.CurrentCount}, " +
                             $"gem timeout={gem.Timeout} (now={Environment.TickCount64})");
            Assert.Equal(0, spawn.CurrentCount);
            Assert.True(gem.Timeout > 0,
                "the spawner stayed parked after losing its child — it would never spawn again");
        }
        finally
        {
            ObjBase.ResolveWorld = null;
            Item.ResolveWorld = null;
        }
    }

    [Fact]
    public void DeletingTheChildAnyOtherWayRestartsItToo()
    {
        var world = TestHarness.CreateWorld();
        var gem = FilledSpawner(world, out var spawn);
        var child = world.GetAllObjects()
            .OfType<SphereNet.Game.Objects.Characters.Character>()
            .Single(c => c.TryGetTag("SPAWN_POINT_UUID", out _));

        world.TryDeleteObject(child);

        output.WriteLine($"after TryDeleteObject: count={spawn.CurrentCount}, gem timeout={gem.Timeout}");
        Assert.Equal(0, spawn.CurrentCount);
        Assert.True(gem.Timeout > 0, "the spawner stayed parked after its child was deleted");
    }

    [Fact]
    public void AnArmedSpawnerIsAlsoInTheWorldsDueQueue()
    {
        var world = TestHarness.CreateWorld();
        var gem = FilledSpawner(world, out var spawn);
        var child = world.GetAllObjects()
            .OfType<SphereNet.Game.Objects.Characters.Character>()
            .Single(c => c.TryGetTag("SPAWN_POINT_UUID", out _));

        world.TryDeleteObject(child);
        Assert.True(gem.Timeout > 0);

        // Holding a deadline is not the same as being reachable. The re-arm has to
        // land in the due queue as well, or the gem carries a time nothing reads.
        gem.SetTimeout(Environment.TickCount64 - 1);
        int fired = 0;
        Item.OnTimerExpired = it => { if (it == gem) fired++; return TriggerResult.Default; };
        try { world.OnTick(); }
        finally { Item.OnTimerExpired = null; }

        output.WriteLine($"re-armed spawner fired {fired}x on the next world tick");
        Assert.Equal(1, fired);
    }

    [Fact]
    public void TheOperatorsOwnTeardownLeavesItParked()
    {
        var world = TestHarness.CreateWorld();
        var gem = FilledSpawner(world, out var spawn);

        // KillAll is the double-click "clear" branch. Upstream clears each child's
        // spawn link before deleting it precisely so DelObj does NOT fire here
        // (CCSpawn::KillChildren), which is why the second double-click — not the
        // first — is the one that starts the spawner again.
        spawn.KillAll();

        output.WriteLine($"after KillAll: count={spawn.CurrentCount}, gem timeout={gem.Timeout}");
        Assert.Equal(0, spawn.CurrentCount);
    }
}
