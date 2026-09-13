using System;
using System.Linq;
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
/// What a duplicated spawner inherits, and what a teardown reaches (port plan İŞ-55
/// / PLAN-104).
///
/// The master plan carried this as an investigation, not a confirmed defect: "does
/// a spawner copy re-link the source's existing children through its ADDOBJ
/// records, and does STOP or deletion on one reach the other's children?"
///
/// The shape that makes it plausible: ADDOBJ accumulates into a TAG that is never
/// cleared (Item.cs, the ADDOBJ setter), the load path rebuilds membership from
/// that tag, and a copy carries every tag its source had. So a spawner that has
/// been through a save could hand its copy a list of children that belong to
/// somebody else - and then STOP on the copy would destroy the source's
/// creatures, which upstream's CCSpawn::Copy explicitly does not do: it carries
/// configuration only (CCSpawn.cpp:1272).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpawnerCopyMembershipTests
{
    private readonly ITestOutputHelper _out;
    public SpawnerCopyMembershipTests(ITestOutputHelper output) => _out = output;

    private static GameWorld NewWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item Spawner(GameWorld world, ItemType type, short x = 100)
    {
        var stone = world.CreateItem();
        stone.BaseId = type == ItemType.SpawnItem ? (ushort)0x1F14 : (ushort)0x1F13;
        stone.ItemType = type;
        stone.Amount = 3;
        world.PlaceItem(stone, new Point3D(x, 100, 0, 0));
        stone.InitializeSpawnComponent(world, null);
        return stone;
    }

    private static Character Creature(GameWorld world, short x)
    {
        var ch = world.CreateCharacter();
        ch.BaseId = 0x0190;
        ch.BodyId = 0x0190;
        ch.Name = "a spawned thing";
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void ACopyDoesNotInheritTheSourcesMembersThroughItsAddObjTag()
    {
        var world = NewWorld();
        var source = Spawner(world, ItemType.SpawnChar);
        var child = Creature(world, 101);

        // How a loaded world leaves a spawner: the members are in the component AND
        // the tag they were rebuilt from, because nothing clears it.
        Assert.True(source.SpawnChar!.AddObj(child.Uid));
        source.SetTag("ADDOBJ", $"0{child.Uid.Value:x}");

        var copy = source.CreateDupe(world);
        _out.WriteLine($"source members {source.SpawnChar!.SpawnedUids.Count}, " +
                       $"copy members {copy.SpawnChar?.SpawnedUids.Count ?? -1}, " +
                       $"copy ADDOBJ tag '{copy.Tags.Get("ADDOBJ")}'");

        // CCSpawn::Copy carries configuration only (CCSpawn.cpp:1272). A copy holding
        // the source's creature would mean two spawners believing they own the same
        // object, and the first STOP would destroy what the other thought was its own.
        Assert.Empty(copy.SpawnChar?.SpawnedUids ?? (System.Collections.Generic.IReadOnlyList<Serial>)Array.Empty<Serial>());
        Assert.Single(source.SpawnChar!.SpawnedUids);
    }

    [Fact]
    public void AnItemSpawnerCopyDoesNotInheritTheSourcesChildrenEither()
    {
        var world = NewWorld();
        var source = Spawner(world, ItemType.SpawnItem);

        var child = world.CreateItem();
        child.BaseId = 0x0EED;
        world.PlaceItem(child, new Point3D(101, 100, 0, 0));
        source.SpawnItem!.RegisterExisting(child.Uid);
        source.SetTag("ADDOBJ", $"0{child.Uid.Value:x}");

        var copy = source.CreateDupe(world);
        _out.WriteLine($"source {source.SpawnItem!.SpawnedUids.Count}, " +
                       $"copy {copy.SpawnItem?.SpawnedUids.Count ?? -1}");

        // The item spawner rebuilds membership from the same tag on load, so it has
        // the same exposure and needs the same answer.
        Assert.Empty(copy.SpawnItem?.SpawnedUids ?? (System.Collections.Generic.IReadOnlyList<Serial>)Array.Empty<Serial>());
        Assert.Single(source.SpawnItem!.SpawnedUids);
    }

    [Fact]
    public void StoppingTheCopyLeavesTheSourcesCreaturesAlone()
    {
        var world = NewWorld();
        var source = Spawner(world, ItemType.SpawnChar);
        var child = Creature(world, 101);
        Assert.True(source.SpawnChar!.AddObj(child.Uid));
        source.SetTag("ADDOBJ", $"0{child.Uid.Value:x}");

        var copy = source.CreateDupe(world);
        Assert.True(world.PlaceItem(copy, new Point3D(120, 100, 0, 0)));

        copy.SpawnChar?.Stop();
        _out.WriteLine($"after STOP on the copy: child deleted={child.IsDeleted}");

        // STOP is kill-all plus a permanent park. If the copy had inherited the
        // membership, this line would have deleted a creature belonging to a spawner
        // nobody touched - the concrete harm behind the investigation.
        Assert.False(child.IsDeleted);
        Assert.Single(source.SpawnChar!.SpawnedUids);
    }

    [Fact]
    public void DeletingTheCopyLeavesTheSourcesCreaturesAlone()
    {
        var world = NewWorld();
        var source = Spawner(world, ItemType.SpawnChar);
        var child = Creature(world, 101);
        Assert.True(source.SpawnChar!.AddObj(child.Uid));
        source.SetTag("ADDOBJ", $"0{child.Uid.Value:x}");

        var copy = source.CreateDupe(world);
        Assert.True(world.PlaceItem(copy, new Point3D(120, 100, 0, 0)));

        world.DeleteObject(copy);
        copy.Delete();
        _out.WriteLine($"after deleting the copy: child deleted={child.IsDeleted}");

        // Deleting a spawner takes its children with it. The same question as STOP,
        // through the other door.
        Assert.False(child.IsDeleted);
    }

    [Fact]
    public void StoppingTheSourceStillReachesItsOwnCreatures()
    {
        var world = NewWorld();
        var source = Spawner(world, ItemType.SpawnChar);
        var child = Creature(world, 101);
        Assert.True(source.SpawnChar!.AddObj(child.Uid));

        source.SpawnChar!.Stop();
        _out.WriteLine($"after STOP on the source: child deleted={child.IsDeleted}");

        // The control. Without it, a spawner whose teardown reached nothing at all
        // would pass every test above for entirely the wrong reason.
        Assert.True(child.IsDeleted);
    }
}
