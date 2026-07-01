using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X notoriety display parity (wiki/notoriety-crime-remaining.txt):
//   personal-grey via aggressor memory, pet-inherits-owner colour, and the
//   @Criminal duration override. ComputeNotoriety is per-viewer; these lock the
//   viewer-relative colour byte (1=blue, 2=green, 4=grey).
public class NotorietyDisplayParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true; ch.BodyId = 0x0190;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    // ---- personal grey: an aggressor shows grey to the player they harmed ----

    [Fact]
    public void Aggressor_ShowsGreyToVictim_ButVictimStaysBlueToAggressor()
    {
        var world = CreateWorld();
        var aggressor = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101);

        // Combat order: aggressor records IAggressor(victim), victim records HarmedBy(aggressor).
        aggressor.Memory_Fight_Start(victim);
        victim.Memory_Fight_Start(aggressor);

        Assert.Equal(4, GameClient.ComputeNotoriety(world, victim, aggressor));  // grey to the victim
        Assert.Equal(1, GameClient.ComputeNotoriety(world, aggressor, victim));  // innocent blue to the aggressor
    }

    [Fact]
    public void NoCombat_PlayerIsInnocentBlue()
    {
        var world = CreateWorld();
        var a = MakePlayer(world, 100);
        var b = MakePlayer(world, 101);

        Assert.Equal(1, GameClient.ComputeNotoriety(world, a, b));
    }

    // ---- pet inherits its owner's notoriety ----

    private static Character MakePet(GameWorld world, int x, Character owner)
    {
        var pet = world.CreateCharacter();
        pet.NpcBrain = NpcBrainType.Animal;
        pet.SetTag("OWNER_UID", owner.Uid.Value.ToString());
        world.PlaceCharacter(pet, new Point3D((short)x, 100, 0, 0));
        return pet;
    }

    [Fact]
    public void OwnPet_ShowsNeutralToOwner()
    {
        // Source-X Noto_CalcFlag: your own pet renders NOTO_NEUTRAL by default
        // (only the OF_PetBehaviorOwnerNeutral flag switches to true notoriety).
        // Pre-W-D SphereNet showed it friendly green.
        var world = CreateWorld();
        var owner = MakePlayer(world, 100);
        var pet = MakePet(world, 101, owner);

        Assert.Equal(3, GameClient.ComputeNotoriety(world, owner, pet));
    }

    [Fact]
    public void CriminalOwnersPet_ShowsGreyToOthers()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 100);
        owner.SetStatFlag(StatFlag.Criminal); // owner is grey
        var pet = MakePet(world, 101, owner);
        var viewer = MakePlayer(world, 102);

        Assert.Equal(4, GameClient.ComputeNotoriety(world, viewer, pet)); // inherits owner's grey
    }

    [Fact]
    public void InnocentOwnersPet_ShowsBlueToOthers()
    {
        var world = CreateWorld();
        var owner = MakePlayer(world, 100);
        var pet = MakePet(world, 101, owner);
        var viewer = MakePlayer(world, 102);

        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, pet)); // inherits owner's innocent blue
    }

    // ---- @Criminal duration override ----

    [Fact]
    public void Criminal_DurationOverride_SetsCustomTimer()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);

        Character.OnCriminalCheck = _ => 60; // 60s, overriding the 180s default

        ch.MakeCriminal();

        Assert.True(ch.IsStatFlag(StatFlag.Criminal));
        // Custom 60s (wide lower bound for CI-load robustness), clearly below the 180s default.
        Assert.InRange(ch.CriminalTimerRemainingSeconds, 40, 60);
    }

    [Fact]
    public void Criminal_ReturnNull_CancelsFlag()
    {
        var world = CreateWorld();
        var ch = MakePlayer(world, 100);

        Character.OnCriminalCheck = _ => null; // cancel the crime

        ch.MakeCriminal();

        Assert.False(ch.IsStatFlag(StatFlag.Criminal));
    }
}
