using Microsoft.Extensions.Logging;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the @PetDesert trigger. The pet loyalty loop already exists
// (TickPetOwnershipTimers decays NpcFood, NpcAI.TryEatFood restores it); this
// locks the desertion event: when loyalty hits zero the pet fires @PetDesert and
// goes wild, and a script returning 1 cancels the desertion. The loyalty clock is
// driven deterministically via the PET_NEXT_LOYALTY_TICK tag (no real waiting).
// Character.OnPetDesert is nulled between tests by ResetEngineStatics.
public class PetDesertTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePet(GameWorld world, Character owner)
    {
        var pet = world.CreateCharacter();
        pet.NpcMaster = owner.Uid;        // owned → loyalty timer runs
        pet.NpcFood = 1;                  // one decay tick from deserting
        pet.SetTag("PET_NEXT_LOYALTY_TICK", "1"); // a due-in-the-past loyalty tick
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        return pet;
    }

    [Fact]
    public void PetDesert_LoyaltyZero_FiresAndGoesWild()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pet = MakePet(world, owner);

        int fired = 0;
        Character.OnPetDesert = (_, _) => { fired++; return false; }; // don't cancel

        pet.TickPetOwnershipTimers(1_000_000); // NpcFood 1 -> 0 -> desert

        Assert.Equal(1, fired);
        Assert.Equal(0, pet.NpcFood);
        Assert.False(pet.OwnerSerial.IsValid); // ownership cleared — went wild
    }

    [Fact]
    public void PetDesert_ScriptReturnsTrue_CancelsDesertion()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pet = MakePet(world, owner);

        Character.OnPetDesert = (_, _) => true; // cancel the desertion

        pet.TickPetOwnershipTimers(1_000_000);

        Assert.True(pet.OwnerSerial.IsValid); // still serving its master
    }
}
