using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Tests;

public class MemorySystemTests
{
    private static GameWorld CreateWorld()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = new GameWorld(loggerFactory);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void Memory_Fight_ClearsWhenTargetOutOfRadarRange()
    {
        Character.MapViewRadarTiles = 5;
        var world = CreateWorld();
        var a = world.CreateCharacter();
        var b = world.CreateCharacter();
        world.PlaceCharacter(a, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(b, new Point3D(105, 100, 0, 0));

        a.Memory_Fight_Start(b);
        var mem = a.Memory_FindObjTypes(b.Uid, MemoryType.Fight);
        Assert.NotNull(mem);

        world.MoveCharacter(a, new Point3D(120, 100, 0, 0));
        Assert.True(a.Memory_OnTick(mem!));
        Assert.Null(a.Memory_FindObjTypes(b.Uid, MemoryType.Fight));
    }

    [Fact]
    public void Memory_Fight_Start_SkipsWhenAttackerAlreadyLogged()
    {
        var world = CreateWorld();
        var a = world.CreateCharacter();
        var b = world.CreateCharacter();
        world.PlaceCharacter(a, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(b, new Point3D(101, 100, 0, 0));

        a.Memory_Fight_Start(b);
        a.RecordAttack(b.Uid, 5);
        var flagsBefore = a.Memory_FindObj(b.Uid)!.GetMemoryTypes();

        a.Memory_Fight_Start(b);
        Assert.Equal(flagsBefore, a.Memory_FindObj(b.Uid)!.GetMemoryTypes());
    }

    [Fact]
    public void Memory_Fight_ClearsWhenTargetAtFullHealthAfterTwoMinutes()
    {
        var world = CreateWorld();
        var a = world.CreateCharacter();
        var b = world.CreateCharacter();
        b.Hits = b.MaxHits;
        world.PlaceCharacter(a, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(b, new Point3D(101, 100, 0, 0));

        a.Memory_Fight_Start(b);
        var mem = a.Memory_FindObj(b.Uid)!;
        mem.More1 = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 181);

        Assert.True(a.Memory_OnTick(mem));
        Assert.Null(a.Memory_FindObjTypes(b.Uid, MemoryType.Fight));
    }

    [Fact]
    public void Memory_ScriptProperty_AddsAndReadsRealMemoryFlags()
    {
        var world = CreateWorld();
        var witness = world.CreateCharacter();
        var criminal = world.CreateCharacter();
        world.PlaceCharacter(witness, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(criminal, new Point3D(101, 100, 0, 0));

        Assert.True(witness.TrySetProperty("MEMORY.SAWCRIME", $"0{criminal.Uid.Value:X}"));
        Assert.NotNull(witness.Memory_FindObjTypes(criminal.Uid, MemoryType.SawCrime));
        Assert.True(witness.TryGetProperty("MEMORY.SAWCRIME", out var present));
        Assert.Equal("1", present);
        Assert.Single(witness.GetMemoryEntriesByType("memory_sawcrime"));
    }

    [Fact]
    public void Memory_AddObjTypes_UsesMemoryItemBaseId()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        var pet = world.CreateCharacter();
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        pet.Memory_AddObjTypes(owner.Uid, MemoryType.IPet);
        var mem = pet.Memory_FindObj(owner.Uid);
        Assert.NotNull(mem);
        Assert.Equal(0x2007, mem!.BaseId);
    }
}
