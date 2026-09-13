using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// What a copy does with the time the source had left (port plan İŞ-56 /
/// PLAN-105).
///
/// The master plan carried this as an investigation: "does a spawner copy keep the
/// remaining timeout - measure against a normal item and against the active, full
/// and stopped spawner shapes."
///
/// Upstream's contract is one line: CItem::DupeCopy calls
/// _SetTimeout(pItem->_GetTimerAdjusted()) (CItem.cpp:4109), so a copy inherits
/// what was left on the clock. CCSpawn::Copy takes six configuration fields and
/// touches no timer at all (CCSpawn.cpp:1272-1288), so building the copy's
/// component must not overwrite what DupeCopy just set.
///
/// The three spawner shapes are asked separately because they reach the timer by
/// different routes: an active spawner is counting down to its next creature, a
/// full one has parked the timer at its quota, and a stopped one has parked it
/// permanently. A copy that came up armed from a stopped source would start
/// producing creatures nobody asked for.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnerCopyTimerTests
{
    private readonly ITestOutputHelper _out;
    public SpawnerCopyTimerTests(ITestOutputHelper output) => _out = output;

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Spawner(GameWorld world, ItemType type, int amount = 3)
    {
        var stone = world.CreateItem();
        stone.BaseId = type == ItemType.SpawnItem ? (ushort)0x1F14 : (ushort)0x1F13;
        stone.ItemType = type;
        stone.Amount = (ushort)amount;
        world.PlaceItem(stone, new Point3D(100, 100, 0, 0));
        stone.InitializeSpawnComponent(world, null);
        return stone;
    }

    private static Character Creature(GameWorld world, short x)
    {
        var ch = world.CreateCharacter();
        ch.BaseId = 0x0190;
        ch.BodyId = 0x0190;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    private const long Deadline = 60_000;

    [Fact]
    public void APlainItemCopyInheritsTheTimeLeftOnTheSource()
    {
        var world = NewWorld();
        var src = world.CreateItem();
        src.BaseId = 0x0EED;
        world.PlaceItem(src, new Point3D(50, 50, 0, 0));
        src.SetTimeout(Environment.TickCount64 + Deadline);

        var copy = src.CreateDupe(world);
        _out.WriteLine($"plain item: source {src.Timeout}, copy {copy.Timeout}");

        // The control for every case below, and upstream's plain contract
        // (CItem.cpp:4109). If this were not true the spawner answers would mean
        // nothing, because the timer never arrived in the first place.
        Assert.Equal(src.Timeout, copy.Timeout);
    }

    [Fact]
    public void AnActiveSpawnerCopyKeepsTheTimeTheSourceHadLeft()
    {
        var world = NewWorld();
        var src = Spawner(world, ItemType.SpawnChar);
        src.SetTimeout(Environment.TickCount64 + Deadline);
        long before = src.Timeout;

        var copy = src.CreateDupe(world);
        _out.WriteLine($"active spawner: source {before}, copy {copy.Timeout}");

        // Building the copy's component must not re-arm or clear what DupeCopy set.
        // A copy that came up with a fresh interval would produce its first creature
        // at a different moment than upstream, every time.
        Assert.Equal(before, copy.Timeout);
    }

    [Fact]
    public void AnItemSpawnerCopyKeepsItTheSameWay()
    {
        var world = NewWorld();
        var src = Spawner(world, ItemType.SpawnItem);
        src.SetTimeout(Environment.TickCount64 + Deadline);
        long before = src.Timeout;

        var copy = src.CreateDupe(world);
        _out.WriteLine($"item spawner: source {before}, copy {copy.Timeout}");

        Assert.Equal(before, copy.Timeout);
    }

    [Fact]
    public void AFullSpawnersCopyDoesNotComeBackArmed()
    {
        var world = NewWorld();
        var src = Spawner(world, ItemType.SpawnChar, amount: 1);
        Assert.True(src.SpawnChar!.AddObj(Creature(world, 101).Uid));
        long before = src.Timeout;

        var copy = src.CreateDupe(world);
        _out.WriteLine($"full spawner: source {before}, copy {copy.Timeout}, " +
                       $"copy members {copy.SpawnChar?.SpawnedUids.Count ?? -1}");

        // Filling the last slot parks the timer (CCSpawn.cpp:643/648). The copy does
        // not inherit the members - İŞ-55 - so it is an EMPTY spawner, and an empty
        // spawner is entitled to run. What it must not do is inherit a clock that
        // means something different than it did on the source.
        Assert.Empty(copy.SpawnChar?.SpawnedUids
                     ?? (System.Collections.Generic.IReadOnlyList<Serial>)Array.Empty<Serial>());
        Assert.Equal(before, copy.Timeout);
    }

    [Fact]
    public void AStoppedSpawnersCopyIsStoppedToo()
    {
        var world = NewWorld();
        var src = Spawner(world, ItemType.SpawnChar);
        src.SpawnChar!.Stop();
        long before = src.Timeout;

        var copy = src.CreateDupe(world);
        _out.WriteLine($"stopped spawner: source timeout {before} stopped={src.SpawnChar!.IsStopped}, " +
                       $"copy timeout {copy.Timeout} stopped={copy.SpawnChar?.IsStopped}");

        // STOP is part of what a spawner IS, and the save carries it for exactly that
        // reason. A copy of a spawner someone deliberately switched off must not come
        // up producing creatures.
        Assert.True(copy.SpawnChar?.IsStopped);
        Assert.Equal(before, copy.Timeout);
    }
}
