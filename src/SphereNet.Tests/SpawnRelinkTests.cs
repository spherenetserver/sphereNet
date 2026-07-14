using System;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X rebuilds a char spawner's live-children count from its saved ADDOBJ
/// entries on load (CCSpawn::AddObj), independent of the SPAWNID tag. SphereNet
/// only did this for classic SPAWNID spawners, so a native MORE1/AMOUNT worldgem
/// forgot its NPCs on every reload, saw CurrentCount 0 &lt; AMOUNT, and respawned
/// its whole quota each restart — one worldgem accumulating many NPCs. The re-link
/// now runs inside InitializeSpawnComponent for every char spawner.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnRelinkTests
{
    private static GameWorld MakeWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static ResourceHolder EmptyResources() =>
        new(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());

    [Fact]
    public void InitializeSpawnComponent_RelinksAddObjChildren_WithoutSpawnId()
    {
        var world = MakeWorld();

        // A native Source-X worldgem: MORE1 creature + AMOUNT cap, NO SPAWNID tag.
        var spawner = world.CreateItem();
        spawner.BaseId = 0x1EA7;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.More1 = 0x0190;
        spawner.Amount = 2; // AMOUNT is the authoritative cap
        world.PlaceItem(spawner, new Point3D(1000, 1000, 0, 0));

        // Two children already alive in the world (loaded from the save).
        var npc1 = world.CreateCharacter();
        var npc2 = world.CreateCharacter();
        world.PlaceCharacter(npc1, new Point3D(1001, 1000, 0, 0));
        world.PlaceCharacter(npc2, new Point3D(999, 1000, 0, 0));

        // The saver wrote one ADDOBJ per child; the loader accumulates them
        // comma-joined into the ADDOBJ tag (leading-0 hex, as UIDs serialize).
        spawner.SetTag("ADDOBJ", $"0{npc1.Uid.Value:x},0{npc2.Uid.Value:x}");

        spawner.InitializeSpawnComponent(world, EmptyResources());

        Assert.NotNull(spawner.SpawnChar);
        // The cap is AMOUNT and the live count is restored from ADDOBJ — so the
        // spawner is already full and will not respawn its quota.
        Assert.Equal(2, spawner.SpawnChar!.MaxCount);
        Assert.Equal(2, spawner.SpawnChar.CurrentCount);

        // The children were reconnected to the spawner (back-link + Spawned flag).
        Assert.True(npc1.IsStatFlag(StatFlag.Spawned));
        Assert.True(npc2.TryGetTag("SPAWNITEM", out _));
    }

    [Fact]
    public void ResetAllSpawners_ReplacesExistingChildren()
    {
        var world = MakeWorld();
        var spawner = world.CreateItem();
        spawner.ItemType = ItemType.SpawnChar;
        world.PlaceItem(spawner, new Point3D(1000, 1000, 0, 0));
        spawner.SpawnChar = new SphereNet.Game.Components.SpawnComponent(spawner, world)
        {
            CharDefId = 0x0190,
            SpawnRange = 0,
            MaxCount = 1,
        };
        spawner.SpawnChar.RespawnNow();
        var oldUid = Assert.Single(spawner.SpawnChar.SpawnedUids);
        var oldChild = world.FindChar(oldUid);
        Assert.NotNull(oldChild);
        Guid oldUuid = oldChild!.Uuid; // serials get recycled — identity is the UUID

        // RESPAWN FULL: the existing (possibly broken) child must be gone,
        // replaced by a freshly materialized one.
        int touched = world.ResetAllSpawners();

        Assert.Equal(1, touched);
        var newUid = Assert.Single(spawner.SpawnChar.SpawnedUids);
        var newChild = world.FindChar(newUid);
        Assert.NotNull(newChild);
        Assert.NotEqual(oldUuid, newChild!.Uuid);
        Assert.True(oldChild.IsDeleted);
    }

    [Fact]
    public void InitializeSpawnComponent_AtCapacity_DoesNotOverspawnOnTick()
    {
        var world = MakeWorld();

        var spawner = world.CreateItem();
        spawner.BaseId = 0x1EA7;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.More1 = 0x0190;
        spawner.Amount = 1;
        world.PlaceItem(spawner, new Point3D(1000, 1000, 0, 0));

        var child = world.CreateCharacter();
        world.PlaceCharacter(child, new Point3D(1001, 1000, 0, 0));
        spawner.SetTag("ADDOBJ", $"0{child.Uid.Value:x}");

        spawner.InitializeSpawnComponent(world, EmptyResources());
        Assert.Equal(1, spawner.SpawnChar!.CurrentCount);

        int before = spawner.SpawnChar.CurrentCount;
        // Even forced well past the spawn timer, a full spawner adds nothing —
        // the pre-fix bug spawned a fresh quota here because CurrentCount was 0.
        spawner.SpawnChar.OnTick(long.MaxValue);
        Assert.Equal(before, spawner.SpawnChar.CurrentCount);
    }

    [Fact]
    public void InitializeSpawnComponent_DeadOrMissingAddObj_IsSkipped()
    {
        var world = MakeWorld();

        var spawner = world.CreateItem();
        spawner.BaseId = 0x1EA7;
        spawner.ItemType = ItemType.SpawnChar;
        spawner.More1 = 0x0190;
        spawner.Amount = 2;
        world.PlaceItem(spawner, new Point3D(1000, 1000, 0, 0));

        var alive = world.CreateCharacter();
        world.PlaceCharacter(alive, new Point3D(1001, 1000, 0, 0));

        // One live serial, one stale/never-existed serial — only the live one links.
        spawner.SetTag("ADDOBJ", $"0{alive.Uid.Value:x},0deadbeef");

        spawner.InitializeSpawnComponent(world, EmptyResources());

        Assert.Equal(1, spawner.SpawnChar!.CurrentCount);
    }
}
