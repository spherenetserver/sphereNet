using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class PetCrimeResponsibilityTests
{
    private static Character Actor(GameWorld world, int x, bool player = true)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.IsOnline = player;
        ch.PrivLevel = PrivLevel.Player;
        ch.NpcBrain = NpcBrainType.Human;
        ch.Str = ch.MaxHits = ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    private static Character Pet(GameWorld world, Character owner, int x = 101)
    {
        var pet = Actor(world, x, player: false);
        pet.NpcMaster = owner.Uid;
        pet.SetStatFlag(StatFlag.Pet);
        return pet;
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(10, 1)]
    [InlineData(10, 2)]
    public void InnocentKilledByPets_ChargesOwnerOnce(int ownerDamage, int petCount)
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var victim = Actor(world, 103);
        victim.CombatState.AddAttacker(owner.Uid); // all kill, with no direct damage
        if (ownerDamage > 0) victim.RecordAttack(owner.Uid, ownerDamage);
        for (int i = 0; i < petCount; i++)
            victim.RecordAttack(Pet(world, owner, 101 + i).Uid, 20);

        new DeathEngine(world).ProcessDeath(victim);

        Assert.Equal(1, owner.Kills);
        Assert.True(owner.IsCriminal);
        Assert.True(owner.MurderDecayRemainingSeconds > 0);
        Assert.Equal(0, owner.Fame);
    }

    [Theory]
    [InlineData(2)] // party/guild
    [InlineData(4)] // criminal or personal grey
    [InlineData(6)] // murderer
    public void OwnerUsesItsOwnViewOfVictim(int ownerNotoriety)
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var pet = Pet(world, owner);
        var victim = Actor(world, 103);
        Character.OnNotoSend = (viewer, subject) => new Character.NotorietyResult(
            subject == victim && viewer == owner ? (byte)ownerNotoriety : (byte)1, 0);
        victim.RecordAttack(pet.Uid, 20);

        new DeathEngine(world).ProcessDeath(victim);

        Assert.Equal(0, owner.Kills);
        Assert.False(owner.IsCriminal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OwnerKillVetoIsRespectedEvenWithMultiplePets(bool directDamage)
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var victim = Actor(world, 104);
        victim.RecordAttack(Pet(world, owner).Uid, 20);
        if (directDamage) victim.RecordAttack(owner.Uid, 10);
        victim.RecordAttack(Pet(world, owner, 102).Uid, 20);
        int calls = 0;
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPLAYER", "Kill", (obj, args) =>
        {
            Assert.Same(owner, obj);
            Assert.Same(owner, args.CharSrc);
            Assert.Same(victim, args.O1);
            calls++;
            return TriggerResult.True;
        });

        new DeathEngine(world) { TriggerDispatcher = dispatcher }.ProcessDeath(victim);

        Assert.Equal(1, calls);
        Assert.Equal(0, owner.Kills);
    }

    [Fact]
    public void PetKillVetoDoesNotEraseOwnersResponsibility()
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var victim = Actor(world, 103);
        victim.RecordAttack(Pet(world, owner).Uid, 20);
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPET", "Kill", (_, _) => TriggerResult.True);

        new DeathEngine(world) { TriggerDispatcher = dispatcher }.ProcessDeath(victim);

        Assert.Equal(1, owner.Kills);
    }

    [Fact]
    public void OwnerMurderMarkCanVetoTheCount()
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var victim = Actor(world, 103);
        victim.RecordAttack(Pet(world, owner).Uid, 20);
        int calls = 0;
        Character.OnMurderMark = (killer, dead, count) =>
        {
            Assert.Same(owner, killer);
            Assert.Same(victim, dead);
            Assert.Equal(1, count);
            calls++;
            return new Character.MurderMarkDecision(null, false);
        };

        new DeathEngine(world).ProcessDeath(victim);

        Assert.Equal(1, calls);
        Assert.Equal(0, owner.Kills);
    }

    [Fact]
    public void PetWithoutDamageDoesNotChargeOwner()
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var victim = Actor(world, 103);
        var pet = Pet(world, owner);
        victim.CombatState.AddAttacker(pet.Uid);

        new DeathEngine(world).ProcessDeath(victim, pet);

        Assert.Equal(0, owner.Kills);
    }

    [Fact]
    public void PetKillingItsOwnerDoesNotChargeTheVictim()
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var pet = Pet(world, owner);
        owner.RecordAttack(pet.Uid, 20);

        new DeathEngine(world).ProcessDeath(owner);

        Assert.Equal(0, owner.Kills);
    }

    [Fact]
    public void SummonsUseOwnerCapturedBeforeKillScriptsRun()
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var newOwner = Actor(world, 104);
        var pet = Pet(world, owner);
        pet.SetStatFlag(StatFlag.Conjured);
        var victim = Actor(world, 103);
        victim.RecordAttack(pet.Uid, 20);
        var dispatcher = new TriggerDispatcher();
        dispatcher.RegisterCharEvent("EVENTSPET", "Kill", (_, _) =>
        {
            pet.NpcMaster = newOwner.Uid;
            return TriggerResult.Default;
        });

        new DeathEngine(world) { TriggerDispatcher = dispatcher }.ProcessDeath(victim);

        Assert.Equal(1, owner.Kills);
        Assert.Equal(0, newOwner.Kills);
    }

    [Fact]
    public void CircularOwnershipDoesNotChargeUnrelatedPlayer()
    {
        var world = TestHarness.CreateWorld();
        var owner = Actor(world, 100);
        var a = Pet(world, owner);
        var b = Pet(world, a, 102);
        a.NpcMaster = b.Uid;
        var victim = Actor(world, 103);
        victim.RecordAttack(a.Uid, 20);

        new DeathEngine(world).ProcessDeath(victim);

        Assert.Equal(0, owner.Kills);
    }
}
