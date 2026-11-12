using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class PetOwnerWitnessTests
{
    private static (GameWorld World, Character Attacker, Character Owner, Character Pet) Setup(int ownerX = 102)
    {
        var world = TestHarness.CreateWorld();
        Character Actor(int x, bool player)
        {
            var ch = world.CreateCharacter();
            ch.IsPlayer = ch.IsOnline = player;
            ch.PrivLevel = PrivLevel.Player;
            ch.BodyId = 0x190;
            ch.NpcBrain = NpcBrainType.Human;
            ch.Karma = 1000;
            ch.Str = ch.MaxHits = ch.Hits = 100;
            world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
            return ch;
        }
        var attacker = Actor(100, true);
        var owner = Actor(ownerX, true);
        var pet = Actor(101, false);
        pet.NpcMaster = owner.Uid;
        pet.SetStatFlag(StatFlag.Pet);
        return (world, attacker, owner, pet);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PetHarmedWithoutAttackRequestNotifiesOwner(bool globalCrime)
    {
        var (world, attacker, owner, pet) = Setup();
        int calls = 0;
        CrimeWitnessService.OnSeeCrime = (w, c, mark) =>
        {
            Assert.Same(owner, w);
            Assert.Same(attacker, c);
            Assert.Same(owner, mark);
            calls++;
            return globalCrime;
        };

        Assert.True(pet.OnAttackedBy(attacker)); // harmful spell / direct damage entry

        Assert.Equal(1, calls);
        Assert.Equal(4, GameClient.ComputeNotoriety(world, owner, attacker));
        Assert.Equal(globalCrime, attacker.IsCriminal);
        Assert.NotNull(pet.Memory_FindObjTypes(attacker.Uid, MemoryType.Aggreived));
    }

    [Fact]
    public void AttackRequestStillNotifiesOwnerBeforeDamage()
    {
        var (world, attacker, owner, pet) = Setup();
        using var logs = TestHarness.CreateLoggerFactory();
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 1981);
        TestHarness.AttachCharacter(client, attacker);

        client.HandleAttack(pet.Uid.Value);

        Assert.NotNull(owner.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
        Assert.False(attacker.IsCriminal);
    }

    [Fact]
    public void DistantOwnerDoesNotNoticePetHarm()
    {
        var (_, attacker, owner, pet) = Setup(ownerX: 150);
        pet.OnAttackedBy(attacker);
        Assert.Null(owner.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisconnectedOwnerOnlyWitnessesWhileStillLingering(bool lingering)
    {
        var (_, attacker, owner, pet) = Setup();
        owner.IsOnline = false;
        if (lingering)
            owner.SetTag("CLIENT_LINGER_UNTIL", (Environment.TickCount64 + 60000).ToString());

        pet.OnAttackedBy(attacker);

        Assert.Equal(lingering, owner.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime) != null);
    }

    [Fact]
    public void WallBlocksOwnerWitnessingPetHarm()
    {
        var (world, attacker, owner, pet) = Setup(ownerX: 106);
        using var md = new MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        md.SetSyntheticItemTile(0x80, new ItemTileData
        { Flags = TileFlag.Wall | TileFlag.Impassable, Height = 30, Name = "wall" });
        world.MapData = md;
        world.AdvancedLos = 3;
        var wall = world.CreateItem();
        wall.BaseId = 0x80;
        world.PlaceItem(wall, new Point3D(103, 100, 0, 0));
        Assert.False(world.CanSeeLOSFor(owner, attacker, LosFlags.NbWindows));

        pet.OnAttackedBy(attacker);

        Assert.Null(owner.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
    }

    [Fact]
    public void SelfDefenseAgainstPetDoesNotBecomeCrimeToOwner()
    {
        var (_, attacker, owner, pet) = Setup();
        attacker.Memory_AddObjTypes(pet.Uid, MemoryType.Aggreived);
        pet.OnAttackedBy(attacker);
        Assert.Null(owner.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
        Assert.False(attacker.IsCriminal);
    }

    [Fact]
    public void EvilPetDoesNotGrantOwnerNewCrimeMemory()
    {
        var (_, attacker, owner, pet) = Setup();
        pet.Karma = -10000;
        pet.OnAttackedBy(attacker);
        Assert.Null(owner.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
    }
}
