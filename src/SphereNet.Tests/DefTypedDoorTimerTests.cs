using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// An item built from its definition alone - a house door materialized from the
/// MULTIDEF component list - leaves the raw type field Normal and takes its type from
/// the def; opening it swaps it onto the t_door_open def. Upstream's timer switches on
/// the effective type (CItem::_OnTick, CItem.cpp:6247), so the door swings shut. The
/// timer switched on the raw field, missed the door case, and the house door stayed
/// open for ever.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DefTypedDoorTimerTests
{
    private const string Script = """
        [ITEMDEF 06a5]
        DEFNAME=i_door_wood
        TYPE=t_door

        [ITEMDEF 06a6]
        DEFNAME=i_door_wood_o
        TYPE=t_door_open
        """;

    private static GameWorld Setup()
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_deftypeddoor_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, Script);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(file) ?? "",
        };
        resources.LoadResourceFile(file);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ADoorTypedOnlyByItsDefinitionClosesOnItsTimer()
    {
        var world = Setup();
        var door = world.CreateItem();
        door.BaseId = 0x06A5;
        world.PlaceItem(door, new Point3D(101, 100, 0, 0));
        Assert.Equal(ItemType.Door, door.ItemType);

        Assert.True(DoorHelper.TryOpenDoorState(door, ignoreLock: true));
        Assert.Equal(0x06A6, door.DispIdFull);
        Assert.Equal(ItemType.DoorOpen, door.ItemType);

        door.SetTimeout(Environment.TickCount64 - 1);
        Assert.True(door.OnTick());

        Assert.Equal(0x06A5, door.DispIdFull);
        Assert.False(door.TryGetTag("DOOR_OPEN", out _));
    }
}
