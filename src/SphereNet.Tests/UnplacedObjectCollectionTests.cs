using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Components;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Source-X CWorldThread::GarbageCollection_NewObjs: an object created and
/// never placed is deleted before the save instead of being written out, and a spawner
/// that is not top level spawns nothing (CCSpawn::GenerateChar).</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class UnplacedObjectCollectionTests
{
    [Fact]
    public void AnItemPlacedOnAMapNotInUseIsCollectedAndThePlacedOnesStay()
    {
        var world = TestHarness.CreateWorld();

        var lost = world.CreateItem();
        lost.BaseId = 0x1F1C;
        lost.TrySetProperty("P", "100,100,0,7");   // map 7 is not initialised

        var ground = world.CreateItem();
        ground.BaseId = 0x0EED;
        Assert.True(world.PlaceItem(ground, new Point3D(100, 100, 0, 0)));

        var bag = world.CreateItem();
        bag.ItemType = ItemType.Container;
        Assert.True(world.PlaceItem(bag, new Point3D(101, 100, 0, 0)));
        var inside = world.CreateItem();
        Assert.True(bag.TryAddItem(inside));

        Assert.False(world.IsItemPlaced(lost));
        Assert.Equal(1, world.CollectUnplacedNewItems());

        Assert.True(lost.IsDeleted);
        Assert.False(ground.IsDeleted);
        Assert.False(bag.IsDeleted);
        Assert.False(inside.IsDeleted);
    }

    [Fact]
    public void WhatTheSaveLoadedIsNotCollected()
    {
        var world = TestHarness.CreateWorld();
        var kept = world.CreateItem();          // stands in for a loaded, unplaceable record
        world.ForgetUnplacedNewItems();

        Assert.Equal(0, world.CollectUnplacedNewItems());
        Assert.False(kept.IsDeleted);
    }

    [Fact]
    public void GarbageRunsTheUnplacedCollectionToo()
    {
        var world = TestHarness.CreateWorld();
        var lost = world.CreateItem();
        lost.BaseId = 0x1F1C;

        var stats = world.GarbageCollection();

        Assert.True(lost.IsDeleted);
        Assert.True(stats.Deleted >= 1);
    }

    [Fact]
    public void AnUnplacedSpawnerSpawnsNothing()
    {
        var world = TestHarness.CreateWorld();
        var spawner = world.CreateItem();
        spawner.BaseId = 0x1EA7;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.SpawnChar = new SpawnComponent(spawner, world) { CharDefId = 0x0190, MaxCount = 3 };

        spawner.SpawnChar.RespawnNow();

        Assert.Equal(0, spawner.SpawnChar.CurrentCount);
    }
}
