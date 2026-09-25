using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the @NPCSeeNewPlayer first contact (Character.SeeNewPlayer). Source-X
// NPC_LookAtChar (CCharNPCAct.cpp:1042-1058) fires the trigger for a player the NPC
// holds no MEMORY_SPEAK of and, unless the script returns 1, records that memory -
// the same one a first spoken line leaves. Character.OnNpcSeeNewPlayer is nulled
// between tests by ResetEngineStatics.
[Collection("DefinitionLoaderSerial")]
public class NpcSeeNewPlayerTriggerTests
{
    private static (Character npc, Character player) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var npc = world.CreateCharacter();
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(101, 100, 0, 0));
        return (npc, player);
    }

    [Fact]
    public void SeeNewPlayer_FiresOnce_AndRecordsASpeakMemory()
    {
        var (npc, player) = Setup();
        var sightings = new List<uint>();
        Character.OnNpcSeeNewPlayer = (n, p) => { if (n == npc) sightings.Add(p.Uid.Value); return false; };

        Assert.True(npc.SeeNewPlayer(player));
        Assert.Equal([player.Uid.Value], sightings);
        Assert.NotNull(npc.Memory_FindObjTypes(player.Uid, MemoryType.Speak));

        // Remembered: no second greeting while the memory lasts.
        Assert.False(npc.SeeNewPlayer(player));
        Assert.Single(sightings);
    }

    [Fact]
    public void SeeNewPlayer_Return1_LeavesThePlayerUnrecorded()
    {
        var (npc, player) = Setup();
        int fires = 0;
        Character.OnNpcSeeNewPlayer = (_, _) => { fires++; return true; };

        npc.SeeNewPlayer(player);
        npc.SeeNewPlayer(player);

        Assert.Equal(2, fires);
        Assert.Null(npc.Memory_FindObjTypes(player.Uid, MemoryType.Speak));
    }

    [Fact]
    public void SeeNewPlayer_AfterTheFirstSpokenLine_DoesNotFire()
    {
        var (npc, player) = Setup();
        int fires = 0;
        Character.OnNpcSeeNewPlayer = (_, _) => { fires++; return false; };

        // NPC_OnHear records MEMORY_SPEAK on the first line (CCharNPCAct.cpp:312).
        npc.Memory_AddObjTypes(player.Uid, MemoryType.Speak);

        Assert.False(npc.SeeNewPlayer(player));
        Assert.Equal(0, fires);
    }

    [Fact]
    public void SeeNewPlayer_Unhooked_RecordsNothing()
    {
        var (npc, player) = Setup();
        Character.OnNpcSeeNewPlayer = null;

        Assert.False(npc.SeeNewPlayer(player));
        Assert.Null(npc.Memory_FindObjTypes(player.Uid, MemoryType.Speak));
    }
}
