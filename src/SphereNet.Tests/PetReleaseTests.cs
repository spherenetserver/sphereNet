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

    /// <summary>The RELEASE verb - what a pack's d_pet_release OKAY button runs - is
    /// NPC_PetRelease (CCharNPCAct.cpp:185): the pet stops answering its owner.</summary>
    [Fact]
    public void ReleaseVerbFreesThePetForGood()
    {
        var (world, client, owner) = Setup();
        var pet = Pet(world, owner);
        Character? seenSrc = null;
        Character.OnPetRelease = (p, src) => { seenSrc = src; return false; };

        Assert.True(pet.TryExecuteCommand("RELEASE", "", new Console(), out _));

        Assert.Same(owner, seenSrc);
        Assert.False(pet.HasOwner(owner.Uid));
        Assert.False(pet.OwnerSerial.IsValid);
        Assert.False(pet.CanAcceptPetCommandFrom(owner, false));
    }

    /// <summary>NPC_OnHearPetCmd runs the SPEECHPET blocks on the pet with the command
    /// before anything else (CCharNPCPet.cpp:80): a RETURN 1 keeps the command - the
    /// way a pack refuses TRANSFER or RELEASE - and a block with no RETURN lets it on.</summary>
    [Fact]
    public void PetSpeechReturnOneKeepsTheCommand()
    {
        var (world, client, owner) = Setup();
        var pet = Pet(world, owner);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_petspk_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [SPEECH spk_pet_test]
            on=transfer
            TAG.REFUSED=1
            return 1

            ON=RELEASE
            return 1

            ON=COME
            ON=STAY
            """);
        try
        {
            stack.Resources.LoadResourceFile(path);
            stack.Dispatcher.SpeechPetResources.Add(
                ResourceId.FromString("spk_pet_test", SphereNet.Core.Enums.ResType.Speech));
            client.SetEngines(triggerDispatcher: stack.Dispatcher);

            client.HandleSpeech(0, 0, 0, "all transfer");
            Assert.False(client.Targets.CursorActive);
            Assert.Equal("1", pet.TagValue("REFUSED"));

            client.HandleSpeech(0, 0, 0, "rex release");
            Assert.True(pet.HasOwner(owner.Uid));

            // An empty block lets the command through.
            client.HandleSpeech(0, 0, 0, "all stay");
            Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
        }
        finally { File.Delete(path); }
    }

    /// <summary>A pet whose owner is only a MEMORY_IPET memory - what a script taming
    /// with an i_memory leaves - is owned (NPC_PetGetOwner, CCharNPCStatus.cpp:471): the
    /// pet AI, which keys on NpcMaster, runs for it.</summary>
    [Fact]
    public void AMemoryOwnedPetHasItsMaster()
    {
        var (world, _, owner) = Setup();
        var pet = world.CreateCharacter();
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        pet.Memory_AddObjTypes(owner.Uid, MemoryType.IPet);
        pet.SetStatFlag(StatFlag.Pet);

        Assert.Equal(owner.Uid, pet.NpcMaster);
        Assert.Equal(owner.Uid, pet.OwnerSerial);
    }

    private sealed class Console : SphereNet.Core.Interfaces.ITextConsole
    {
        public PrivLevel GetPrivLevel() => PrivLevel.Owner;
        public string GetName() => "test";
        public void SysMessage(string text) { }
    }
}
