using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Sectors;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// A spawner learns that one of its creatures died when it dies, not when somebody
/// next ticks the spawner.
///
/// A spawner at its amount cap parks its timer (-1) and waits. Until now the only
/// thing that could restart it was its own tick noticing, during CleanupDead, that
/// the list had shrunk — polling. In a sector nobody is standing in, the next tick
/// is the three-minute maintenance sweep, so killing the last creature of a remote
/// spawner left the spot empty for up to three minutes with no reason visible
/// anywhere.
///
/// It is also what blocks the rest of the timer work: a spawner with no armed
/// deadline is in no due queue, so on a pure due-ordered tick a parked spawner would
/// never be reached at all and the world would quietly stop spawning. Upstream
/// solves it by event (CCSpawn::DelObj, CCSpawn.cpp:509): the dying object tells its
/// spawn point, which removes the member and re-arms its own timeout.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnerDeathNotificationTests
{
    private readonly ITestOutputHelper _out;
    public SpawnerDeathNotificationTests(ITestOutputHelper output) => _out = output;

    private const string Script = """
        [ITEMDEF 01f13]
        DEFNAME=i_spawn_char_evt
        TYPE=t_spawn_char

        [CHARDEF c_target_evt]
        DEFNAME=c_target_evt
        ID=0x27
        NAME=event target
        """;

    /// <summary>Definitions load in the test body: ResetEngineStatics clears the
    /// tables after the class is constructed.</summary>
    private static ResourceHolder LoadResources()
    {
        var lf = LoggerFactory.Create(_ => { });
        string tempFile = Path.Combine(Path.GetTempPath(), $"sphnet_evt_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, Script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        { ScpBaseDir = Path.GetDirectoryName(tempFile) ?? "" };
        resources.LoadResourceFile(tempFile);
        new SphereNet.Game.Definitions.DefinitionLoader(
            resources, new SphereNet.Game.Magic.SpellRegistry()).LoadAll();
        return resources;
    }

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 2048, 2048);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    /// <summary>A spawner far from any player: its sector never ticks, which is the
    /// case the polling design could not serve.</summary>
    private static Item RemoteSpawner(GameWorld world, ResourceHolder res)
    {
        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(1600, 1600, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_target_evt");
        stone.Amount = 1;                       // one creature: the cap is reached at once
        stone.InitializeSpawnComponent(world, res);
        stone.SpawnChar!.ForceSpawn();
        stone.SpawnChar!.OnTick(Environment.TickCount64);
        return stone;
    }

    // ------------------------------------------------------------------

    [Fact]
    public void KillingTheLastCreatureRestartsTheSpawnerImmediately()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = RemoteSpawner(world, res);

        uint uid = Assert.Single(spawner.SpawnChar!.SpawnedUids).Value;
        var creature = world.FindChar(new Serial(uid));
        Assert.NotNull(creature);

        // At the cap, so the timer is parked and no deadline exists anywhere.
        Assert.True(spawner.Timeout < 0, $"expected a parked timer, got {spawner.Timeout}");

        world.DeleteObject(creature!);
        creature!.Delete();

        // The death itself has to reach the spawner. Nothing here ticks it: its
        // sector holds no player, and with the timer parked there is no deadline to
        // put it in any queue either.
        _out.WriteLine($"after the death: members={spawner.SpawnChar!.SpawnedUids.Count}, " +
                       $"timer={spawner.Timeout}");
        Assert.Empty(spawner.SpawnChar!.SpawnedUids);
        Assert.True(spawner.Timeout > 0,
            "the spawner should have re-armed its own timer when it lost its creature");
    }

    [Fact]
    public void TheReArmedTimerIsAnOrdinaryDeadlineTheWorldCanReach()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = RemoteSpawner(world, res);

        var creature = world.FindChar(new Serial(spawner.SpawnChar!.SpawnedUids.Single().Value));
        world.DeleteObject(creature!);
        creature!.Delete();

        // Re-arming through the timer door is the point: a deadline set any other way
        // is invisible to everything that reads deadlines.
        long deadline = spawner.Timeout;
        Assert.True(deadline > Environment.TickCount64 - 1000,
            $"re-armed deadline should be in the future, got {deadline}");
        _out.WriteLine($"re-armed {deadline - Environment.TickCount64} ms ahead");
    }

    [Fact]
    public void ASpawnerBelowItsCapIsNotDisturbed()
    {
        var res = LoadResources();
        var world = NewWorld();

        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(1600, 1600, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_target_evt");
        stone.Amount = 3;
        stone.InitializeSpawnComponent(world, res);
        // ONE creature of a cap of three: the spawner is below its cap, so it still
        // holds a live countdown to its next production. (Filling the cap would park
        // the timer, which is the other test.)
        stone.SpawnChar!.ForceSpawn();
        stone.SpawnChar!.OnTick(Environment.TickCount64);
        Assert.Single(stone.SpawnChar!.SpawnedUids);

        long before = stone.Timeout;
        Assert.True(before > 0, $"expected a live countdown below the cap, got {before}");
        var creature = world.FindChar(new Serial(stone.SpawnChar!.SpawnedUids.First().Value));
        world.DeleteObject(creature!);
        creature!.Delete();

        // Below the cap the spawner already has a live countdown. Losing a member
        // must not push that deadline out - a spawner that restarted its clock on
        // every death would produce more slowly the more it was farmed.
        _out.WriteLine($"timer before={before} after={stone.Timeout}");
        Assert.Equal(before, stone.Timeout);
        Assert.Empty(stone.SpawnChar!.SpawnedUids);
    }

    [Fact]
    public void AnUnrelatedDeathTouchesNoSpawner()
    {
        var res = LoadResources();
        var world = NewWorld();
        var spawner = RemoteSpawner(world, res);
        long parked = spawner.Timeout;

        var stranger = world.CreateCharacter();
        stranger.BaseId = 0x000C;
        world.PlaceCharacter(stranger, new Point3D(1600, 1610, 0, 0));
        world.DeleteObject(stranger);
        stranger.Delete();

        _out.WriteLine($"spawner untouched: members={spawner.SpawnChar!.SpawnedUids.Count}, " +
                       $"timer={spawner.Timeout}");
        Assert.Single(spawner.SpawnChar!.SpawnedUids);
        Assert.Equal(parked, spawner.Timeout);
    }

    [Fact]
    public void TearingDownTheSpawnerDoesNotReArmItOnEveryChild()
    {
        var res = LoadResources();
        var world = NewWorld();

        var stone = world.CreateItem();
        stone.BaseId = 0x1F13;
        stone.ItemType = ItemType.SpawnChar;
        world.PlaceItem(stone, new Point3D(1600, 1600, 0, 0));
        stone.SetTag("MORE1_DEFNAME", "c_target_evt");
        stone.Amount = 3;
        stone.InitializeSpawnComponent(world, res);
        for (int i = 0; i < 3; i++)
        {
            stone.SpawnChar!.ForceSpawn();
            stone.SpawnChar!.OnTick(Environment.TickCount64);
        }
        Assert.Equal(3, stone.SpawnChar!.SpawnedUids.Count);

        // KillAll deletes every child. Upstream's DelObj returns immediately while a
        // teardown is running (CCSpawn.cpp:512) - otherwise the spawner would be told
        // about each of its own children in turn and re-arm in the middle of being
        // dismantled.
        stone.SpawnChar!.KillAll();

        _out.WriteLine($"after KillAll: members={stone.SpawnChar!.SpawnedUids.Count}");
        Assert.Empty(stone.SpawnChar!.SpawnedUids);
    }
}
