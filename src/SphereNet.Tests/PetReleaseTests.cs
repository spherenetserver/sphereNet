using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>"release" as upstream's PC_RELEASE / NPC_PetRelease (CCharNPCPet.cpp:233,
/// 860): no target cursor, @PetRelease may keep the pet, and a conjured creature is
/// taken away rather than left in the world ownerless.</summary>
[Collection("VendorStateSerial")]
public sealed class PetReleaseTests
{
    private static (GameWorld World, GameClient Client, Character Owner) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 6201);
        var owner = world.CreateCharacter();
        owner.BodyId = 0x0190;
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, owner);
        return (world, client, owner);
    }

    private static Character Pet(GameWorld world, Character owner)
    {
        var pet = world.CreateCharacter();
        pet.Name = "rex";
        pet.NpcMaster = owner.Uid;
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        return pet;
    }

    [Fact]
    public void ReleaseActsAtOnceWithoutATargetCursor()
    {
        var (world, client, owner) = Setup();
        var pet = Pet(world, owner);

        client.HandleSpeech(0, 0, 0, "rex release");

        Assert.False(pet.HasOwner(owner.Uid));
        Assert.False(pet.IsDeleted);
        Assert.False(client.Targets.CursorActive);
    }

    [Fact]
    public void ASummonIsTakenAwayNotSetLoose()
    {
        var (world, client, owner) = Setup();
        var pet = Pet(world, owner);
        pet.SetStatFlag(StatFlag.Conjured);

        client.HandleSpeech(0, 0, 0, "rex release");

        Assert.True(pet.IsDeleted);
    }

    [Fact]
    public void PetReleaseReturnOneKeepsThePet()
    {
        var (world, client, owner) = Setup();
        var pet = Pet(world, owner);
        Character? seenSrc = null;
        Character.OnPetRelease = (p, src) => { seenSrc = src; return true; };

        client.HandleSpeech(0, 0, 0, "rex release");

        Assert.Same(owner, seenSrc);
        Assert.True(pet.HasOwner(owner.Uid));
    }
}
