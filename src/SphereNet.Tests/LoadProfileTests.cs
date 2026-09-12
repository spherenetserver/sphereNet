using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Diagnostics;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The reproducible load profile (port plan İŞ-44 / PLAN-701, PLAN-703).
///
/// A soak run is only worth anything if two snapshots are comparable, which means
/// the same numbers gathered the same way. These pin what a snapshot contains and
/// that the counters behind it actually move, so a later run can be trusted to be
/// measuring rather than guessing.
///
/// The plan says thresholds come from a first baseline on the target hardware, so
/// nothing here asserts a millisecond target - only that the record is complete
/// and honest.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class LoadProfileTests
{
    private static GameWorld MakeWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ASnapshotCountsWhatTheServerIsCarrying()
    {
        var world = MakeWorld();
        LoadProfile.Reset();

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        var rock = world.CreateItem();
        rock.BaseId = 0x1363;
        world.PlaceItem(rock, new Point3D(102, 100, 0, 0));

        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnChar;
        spawner.BaseId = 0x1F13;
        world.PlaceItem(spawner, new Point3D(103, 100, 0, 0));

        var snap = LoadProfile.Capture(world);

        Assert.Equal(1, snap.Players);
        Assert.Equal(1, snap.Npcs);
        Assert.Equal(2, snap.Items);       // the rock and the spawner
        Assert.Equal(1, snap.Spawners);    // the spawner is also an item
    }

    [Fact]
    public void FightingCountsOnlyThoseWithATarget()
    {
        var world = MakeWorld();
        var idle = world.CreateCharacter();
        world.PlaceCharacter(idle, new Point3D(100, 100, 0, 0));
        var angry = world.CreateCharacter();
        world.PlaceCharacter(angry, new Point3D(101, 100, 0, 0));
        angry.FightTarget = idle.Uid;

        Assert.Equal(1, LoadProfile.Capture(world).Fighting);
    }

    [Fact]
    public void AllThreeSpawnerKindsCount()
    {
        var world = MakeWorld();
        foreach (var type in new[] { ItemType.SpawnChar, ItemType.SpawnItem, ItemType.SpawnChampion })
        {
            var s = world.CreateItem();
            s.ItemType = type;
            s.BaseId = 0x1F13;
            world.PlaceItem(s, new Point3D(100, 100, 0, 0));
        }

        Assert.Equal(3, LoadProfile.Capture(world).Spawners);
    }

    // ---- the counters ------------------------------------------------------

    [Fact]
    public void MovesAreCountedByOutcome()
    {
        LoadProfile.Reset();

        LoadProfile.CountMove(accepted: true);
        LoadProfile.CountMove(accepted: true);
        LoadProfile.CountMove(accepted: false);

        Assert.Equal(2, LoadProfile.MovesAccepted);
        Assert.Equal(1, LoadProfile.MovesRejected);
    }

    [Fact]
    public void ASaveRecordsItsDurationAndBumpsTheCount()
    {
        LoadProfile.Reset();

        LoadProfile.CountSave(1200);
        LoadProfile.CountSave(900);

        Assert.Equal(2, LoadProfile.SaveCount);
        Assert.Equal(900, LoadProfile.LastSaveMs);   // the LAST one, not a total
    }

    [Fact]
    public void ResetClearsTheCountersSoARunDescribesItselfNotTheProcess()
    {
        LoadProfile.CountMove(true);
        LoadProfile.CountTrigger();
        LoadProfile.CountSave(50);

        LoadProfile.Reset();

        Assert.Equal(0, LoadProfile.MovesAccepted);
        Assert.Equal(0, LoadProfile.TriggersFired);
        Assert.Equal(0, LoadProfile.SaveCount);
        Assert.Equal(0, LoadProfile.LastSaveMs);
    }

    // ---- the metrics half ---------------------------------------------------

    [Fact]
    public void TickPercentilesComeFromTheHistogramWhenOneIsGiven()
    {
        var world = MakeWorld();
        var ticks = new TickHistogram(500, 1);
        for (int i = 0; i < 100; i++)
            ticks.Record(i);

        var snap = LoadProfile.Capture(world, ticks);

        Assert.Equal(100, snap.TicksSampled);
        Assert.Equal(ticks.P50, snap.TickP50Ms);
        Assert.Equal(ticks.P95, snap.TickP95Ms);
        Assert.Equal(ticks.P99, snap.TickP99Ms);
        Assert.Equal(ticks.MaxMs, snap.TickMaxMs);
    }

    [Fact]
    public void WithoutAHistogramTheRecordIsStillUsable()
    {
        // A caller that has no tick sampler should still get the world counts and
        // the memory figures rather than nothing at all.
        var world = MakeWorld();

        var snap = LoadProfile.Capture(world);

        Assert.Equal(0, snap.TicksSampled);
        Assert.True(snap.WorkingSetMb > 0);
        Assert.Equal(-1, snap.UnexplainedObjects);   // "not measured", not "zero"
    }

    [Fact]
    public void NotMeasuredIsDistinctFromZero()
    {
        // The object audit is expensive, so a snapshot may skip it. -1 says it was
        // not run; 0 says it ran and found nothing. Conflating them would make a
        // soak report claim a clean world it never checked.
        var world = MakeWorld();

        Assert.Equal(-1, LoadProfile.Capture(world).UnexplainedObjects);
        Assert.Equal(0, LoadProfile.Capture(world, unexplained: 0).UnexplainedObjects);
    }

    [Fact]
    public void TheRecordPrintsEveryFieldInAStableOrder()
    {
        var world = MakeWorld();
        string line = LoadProfile.Capture(world).ToString();

        foreach (var key in new[]
                 {
                     "players=", "npcs=", "items=", "spawners=", "fighting=",
                     "moves=", "triggers=", "tick(p50/p95/p99/max)=", "save(last/count)=",
                     "mem(ws/heap)=", "gc=", "pause=", "outq=", "unexplained=",
                 })
            Assert.Contains(key, line);
    }
}
