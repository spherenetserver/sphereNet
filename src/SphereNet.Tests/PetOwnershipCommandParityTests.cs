using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Who a pet answers to, and what changing hands takes with it.
///
/// <list type="bullet">
/// <item>Use_Figurine refuses a figurine LINKed to somebody else before anything else
/// happens, unless the user is a GM (CCharUse.cpp:1123).</item>
/// <item>An attack order runs the victim's OnAttackedBy with the ordering owner as the
/// attacker and fCommandPet=true, and only a victim that accepts it is attacked: a
/// STONE or DEAD victim refuses (CCharNPCPet.cpp:447, CCharFight.cpp:337).</item>
/// <item>NPC_PetSetOwner clears the previous owner's MEMORY_IPET and every MEMORY_FRIEND
/// through NPC_PetClearOwners (CCharNPCPet.cpp:559, :614).</item>
/// <item>NPC_Act_Follow reads Flee / MaxDistance / MoveAway back from @NPCActFollow
/// (CCharNPCAct.cpp:1377-1379) and acts on all three (:1406-1437).</item>
/// <item>A dead bonded pet still hears commands; only GUARD, GUARD ME, ATTACK, KILL,
/// TRANSFER, DROP and DROP ALL are ignored (CCharNPCPet.cpp:154-160), and its action
/// tick keeps running while dead (CCharAct.cpp:5948-5952).</item>
/// <item>A berserk creature takes no pet command except from a GM (CCharNPCPet.cpp:85,
/// :391).</item>
/// </list>
/// A restore whose ownership hand-over is refused must not consume the figurine nor
/// leave the creature standing in the world.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PetOwnershipCommandParityTests
{
    private sealed record Bench(GameWorld World, NpcAI Ai, GameClient Client, Character Owner);

    private static Bench Setup(int clientId = 7841)
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ai = new NpcAI(world, new SphereConfig()) { Flags = NpcAIFlags.None };
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), clientId);
        var owner = Being(world, new Point3D(100, 100, 0, 0), player: true);
        owner.Name = "owner";
        owner.MaxFollower = 5;
        TestHarness.AttachCharacter(client, owner);
        return new Bench(world, ai, client, owner);
    }

    private static Character Being(GameWorld world, Point3D at, bool player = false)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;
        ch.Dex = 100; ch.Stam = 100; ch.Int = 100;
        world.PlaceCharacter(ch, at);
        return ch;
    }

    private static Character PetOf(Bench b, Point3D at)
    {
        var pet = Being(b.World, at);
        pet.Name = "reviewpet";
        pet.BodyId = 0x00C8;
        Assert.True(pet.TryAssignOwnership(b.Owner, b.Owner));
        pet.PetAIMode = PetAIMode.Stay;
        return pet;
    }

    private static void Tick(NpcAI ai, Character npc)
    {
        npc.NextNpcActionTime = 0;
        ai.OnTickAction(npc);
    }

    private static void ApplyPetTarget(GameClient client, Character pet, string verb, Serial target) =>
        typeof(ClientItemUseHandler)
            .GetMethod("ApplyPetTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client.ItemUse, [pet, verb, target, (short)0, (short)0, (sbyte)0]);

    private static Item Pack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    // --- figurine LINK lock (CCharUse.cpp:1123) ------------------------------

    /// <summary>A shrunk pet's figurine locked to its owner and held by somebody else.</summary>
    private static (Bench B, Character Pet, Item Figurine, Character Shrinker) LockedFigurine(bool lockToShrinker)
    {
        var b = Setup();
        var shrinker = Being(b.World, new Point3D(110, 110, 0, 0), player: true);
        shrinker.MaxFollower = 5;
        var pet = Being(b.World, new Point3D(111, 110, 0, 0));
        Assert.True(pet.TryAssignOwnership(shrinker, shrinker));

        var figurine = b.World.CreateItem();
        Assert.True(PetFigurine.Shrink(shrinker, pet, figurine, b.World));
        if (lockToShrinker)
            figurine.Link = shrinker.Uid;
        Assert.True(Pack(b.World, b.Owner).TryAddItem(figurine));
        return (b, pet, figurine, shrinker);
    }

    [Fact]
    public void ANativeFigurineLockedToSomebodyElseIsRefused()
    {
        var (b, pet, figurine, shrinker) = LockedFigurine(lockToShrinker: true);

        b.Client.HandleDoubleClick(figurine.Uid.Value);

        Assert.False(figurine.IsDeleted);
        Assert.True(pet.HasOwner(shrinker.Uid));
        Assert.True(PetStorage.IsParked(pet));
    }

    [Fact]
    public void AGmMayStillUseALockedNativeFigurine()
    {
        var (b, pet, figurine, _) = LockedFigurine(lockToShrinker: true);
        b.Owner.PrivLevel = PrivLevel.GM;

        b.Client.HandleDoubleClick(figurine.Uid.Value);

        Assert.True(figurine.IsDeleted);
        Assert.True(pet.HasOwner(b.Owner.Uid));
    }

    [Fact]
    public void AnUnlockedNativeFigurineStillRestoresForItsHolder()
    {
        // No LINK: Use_Figurine hands the creature to whoever uses it.
        var (b, pet, figurine, _) = LockedFigurine(lockToShrinker: false);

        b.Client.HandleDoubleClick(figurine.Uid.Value);

        Assert.True(figurine.IsDeleted);
        Assert.True(pet.HasOwner(b.Owner.Uid));
        Assert.False(PetStorage.IsParked(pet));
    }

    // --- restore refused by the ownership hand-over ---------------------------

    [Fact]
    public void AFollowersUpdateReturnOneDoesNotStopARestore()
    {
        // Use_Figurine asks FollowersUpdate with fCheckOnly (no trigger,
        // CCharUse.cpp:1133) and NPC_PetSetOwner ignores the trigger's result
        // (CCharNPCPet.cpp:633), so a script's RETURN 1 cannot keep the pet parked.
        var (b, pet, figurine, _) = LockedFigurine(lockToShrinker: false);
        GameClient.ServerOptionFlags |= OptionFlags.PetSlots;
        Character.OnFollowersUpdate = (owner, _, adding, _) => adding && owner == b.Owner;

        var restored = PetFigurine.Restore(b.Owner, figurine, b.World, b.Owner.Position);

        Assert.NotNull(restored);
        Assert.True(pet.HasOwner(b.Owner.Uid));
        Assert.False(PetStorage.IsParked(pet));
    }

    // --- the attack order (CCharNPCPet.cpp:442-450) ---------------------------

    [Fact]
    public void AnAttackOrderRecordsTheOwnerAsTheAggressor()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        var victim = Being(b.World, new Point3D(103, 100, 0, 0), player: true);

        ApplyPetTarget(b.Client, pet, "attack", victim.Uid);

        Assert.Equal(PetAIMode.Attack, pet.PetAIMode);
        Assert.Equal(victim.Uid, pet.FightTarget);
        // OnAttackedBy(pSrc = the owner): HARMEDBY | IRRITATEDBY | AGGREIVED on the victim.
        Assert.NotNull(victim.Memory_FindObjTypes(b.Owner.Uid, MemoryType.Aggreived));
        Assert.NotNull(victim.Memory_FindObjTypes(b.Owner.Uid, MemoryType.HarmedBy));
    }

    [Fact]
    public void AnOrderedAttackDoesNotMakeTheVictimRetaliateAgainstTheOwner()
    {
        // fCommandPet=true skips OnHarmedBy (CCharFight.cpp:377).
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        var victim = Being(b.World, new Point3D(103, 100, 0, 0));

        ApplyPetTarget(b.Client, pet, "kill", victim.Uid);

        Assert.Equal(victim.Uid, pet.FightTarget);
        Assert.NotEqual(b.Owner.Uid, victim.FightTarget);
    }

    [Fact]
    public void AnAttackOrderOnAStoneTargetIsRefused()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        var victim = Being(b.World, new Point3D(103, 100, 0, 0), player: true);
        victim.SetStatFlag(StatFlag.Stone);

        ApplyPetTarget(b.Client, pet, "attack", victim.Uid);

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
        Assert.False(pet.FightTarget.IsValid);
        Assert.False(pet.TryGetTag("ATTACK_TARGET", out _));
        Assert.Null(victim.Memory_FindObjTypes(b.Owner.Uid, MemoryType.Aggreived));
    }

    // --- the old owner's memories go with a transfer (CCharNPCPet.cpp:559) ----

    [Fact]
    public void ATransferDropsTheOldOwnersPetAndFriendMemories()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        var friend = Being(b.World, new Point3D(98, 100, 0, 0), player: true);
        var newOwner = Being(b.World, new Point3D(99, 100, 0, 0), player: true);
        newOwner.MaxFollower = 5;
        pet.AddFriend(friend);
        Assert.NotNull(pet.Memory_FindObjTypes(b.Owner.Uid, MemoryType.IPet));
        Assert.NotNull(pet.Memory_FindObjTypes(friend.Uid, MemoryType.Friend));

        Assert.True(pet.TryAssignOwnership(newOwner, newOwner, enforceFollowerCap: true));

        Assert.Null(pet.Memory_FindObjTypes(b.Owner.Uid, MemoryType.IPet));
        Assert.Null(pet.Memory_FindObjTypes(friend.Uid, MemoryType.Friend));
        Assert.NotNull(pet.Memory_FindObjTypes(newOwner.Uid, MemoryType.IPet));
        Assert.True(pet.IsStatFlag(StatFlag.Pet));
    }

    [Fact]
    public void AReleaseDropsTheFriendMemories()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        var friend = Being(b.World, new Point3D(98, 100, 0, 0), player: true);
        pet.AddFriend(friend);

        pet.ClearOwnership(clearFriends: true);

        Assert.Null(pet.Memory_FindObjTypes(friend.Uid, MemoryType.Friend));
        Assert.Null(pet.Memory_FindObjTypes(b.Owner.Uid, MemoryType.IPet));
    }

    [Fact]
    public void ReassigningTheSameOwnerKeepsTheFriendMemories()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        var friend = Being(b.World, new Point3D(98, 100, 0, 0), player: true);
        pet.AddFriend(friend);

        Assert.True(pet.TryAssignOwnership(b.Owner, b.Owner));

        Assert.NotNull(pet.Memory_FindObjTypes(friend.Uid, MemoryType.Friend));
        Assert.NotNull(pet.Memory_FindObjTypes(b.Owner.Uid, MemoryType.IPet));
    }

    // --- @NPCActFollow Flee / MoveAway (CCharNPCAct.cpp:1377-1437) ------------

    private static Character FollowingPet(Bench b)
    {
        // Two tiles east of the owner, told to follow.
        var pet = PetOf(b, new Point3D(102, 100, 0, 0));
        pet.PetAIMode = PetAIMode.Follow;
        return pet;
    }

    [Fact]
    public void AScriptedMoveAwayStepsThePetAwayFromItsOwner()
    {
        var b = Setup();
        var pet = FollowingPet(b);
        b.Ai.OnNpcActFollow = (_, _, args) =>
        {
            args.MaxDistance = 5;
            args.MoveAway = true;
            return NpcAI.FollowTriggerResult.Continue;
        };

        Tick(b.Ai, pet);

        Assert.Equal(3, pet.Position.GetDistanceTo(b.Owner.Position));
    }

    [Fact]
    public void AScriptedFleeStepsThePetAwayFromItsOwner()
    {
        var b = Setup();
        var pet = FollowingPet(b);
        b.Ai.OnNpcActFollow = (_, _, args) =>
        {
            args.MaxDistance = 5;
            args.Flee = true;
            return NpcAI.FollowTriggerResult.Continue;
        };

        Tick(b.Ai, pet);

        Assert.Equal(3, pet.Position.GetDistanceTo(b.Owner.Position));
    }

    [Fact]
    public void AFleeAlreadyFarEnoughTakesNoStep()
    {
        // fFlee with dist >= maxDistance: give up, no step (CCharNPCAct.cpp:1415-1420).
        var b = Setup();
        var pet = FollowingPet(b);
        b.Ai.OnNpcActFollow = (_, _, args) =>
        {
            args.Flee = true;   // MaxDistance stays 1
            return NpcAI.FollowTriggerResult.Continue;
        };

        Tick(b.Ai, pet);

        Assert.Equal(2, pet.Position.GetDistanceTo(b.Owner.Position));
    }

    [Fact]
    public void AnOrdinaryFollowStillCloses()
    {
        var b = Setup();
        var pet = FollowingPet(b);
        b.Ai.OnNpcActFollow = (_, _, _) => NpcAI.FollowTriggerResult.Continue;

        Tick(b.Ai, pet);

        Assert.Equal(1, pet.Position.GetDistanceTo(b.Owner.Position));
    }

    // --- dead bonded pets (CCharNPCPet.cpp:154-160) ---------------------------

    private static Character DeadBondedPet(Bench b, Point3D at)
    {
        var pet = PetOf(b, at);
        pet.IsBonded = true;
        pet.SetStatFlag(StatFlag.Dead);
        pet.Hits = 0;
        return pet;
    }

    [Fact]
    public void ADeadBondedPetTakesAPassiveCommand()
    {
        var b = Setup();
        var pet = DeadBondedPet(b, new Point3D(101, 100, 0, 0));

        b.Client.HandleSpeech(0, 0, 0, "all follow me");

        Assert.Equal(PetAIMode.Follow, pet.PetAIMode);
    }

    [Theory]
    [InlineData("all stay")]
    [InlineData("reviewpet stop")]
    public void ADeadBondedPetCanBeToldToStay(string command)
    {
        var b = Setup();
        var pet = DeadBondedPet(b, new Point3D(101, 100, 0, 0));
        pet.PetAIMode = PetAIMode.Follow;

        b.Client.HandleSpeech(0, 0, 0, command);

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    [Fact]
    public void ADeadBondedPetIgnoresAnAttackOrder()
    {
        var b = Setup();
        var pet = DeadBondedPet(b, new Point3D(101, 100, 0, 0));
        var victim = Being(b.World, new Point3D(103, 100, 0, 0));

        ApplyPetTarget(b.Client, pet, "attack", victim.Uid);

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
        Assert.False(pet.FightTarget.IsValid);
    }

    [Fact]
    public void ADeadBondedPetCannotBeTransferred()
    {
        var b = Setup();
        var pet = DeadBondedPet(b, new Point3D(101, 100, 0, 0));
        var other = Being(b.World, new Point3D(99, 100, 0, 0), player: true);
        other.MaxFollower = 5;

        ApplyPetTarget(b.Client, pet, "transfer", other.Uid);

        Assert.True(pet.HasOwner(b.Owner.Uid));
    }

    [Fact]
    public void ADeadBondedPetFollowsItsOwner()
    {
        var b = Setup();
        var pet = DeadBondedPet(b, new Point3D(104, 100, 0, 0));
        pet.PetAIMode = PetAIMode.Follow;

        Tick(b.Ai, pet);

        Assert.Equal(103, pet.X);
        Assert.True(pet.IsDead);
    }

    [Fact]
    public void ADeadUnbondedNpcStillDoesNotAct()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(104, 100, 0, 0));
        pet.PetAIMode = PetAIMode.Follow;
        pet.SetStatFlag(StatFlag.Dead);

        Tick(b.Ai, pet);

        Assert.Equal(104, pet.X);
    }

    // --- berserk creatures (CCharNPCPet.cpp:85, :391) -------------------------

    [Fact]
    public void ABerserkPetIgnoresItsOwnersCommand()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        pet.NpcBrain = NpcBrainType.Berserk;

        b.Client.HandleSpeech(0, 0, 0, "all follow me");

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    [Fact]
    public void ABerserkPetIgnoresAnAttackTargetFromItsOwner()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        pet.NpcBrain = NpcBrainType.Berserk;
        var victim = Being(b.World, new Point3D(103, 100, 0, 0));

        ApplyPetTarget(b.Client, pet, "attack", victim.Uid);

        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
        Assert.False(pet.FightTarget.IsValid);
    }

    [Fact]
    public void ABerserkPetStillObeysAGm()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));
        pet.NpcBrain = NpcBrainType.Berserk;
        b.Owner.PrivLevel = PrivLevel.GM;

        b.Client.HandleSpeech(0, 0, 0, "all follow me");

        Assert.Equal(PetAIMode.Follow, pet.PetAIMode);
    }

    [Fact]
    public void AnOrdinaryPetStillObeysItsOwner()
    {
        var b = Setup();
        var pet = PetOf(b, new Point3D(101, 100, 0, 0));

        b.Client.HandleSpeech(0, 0, 0, "all follow me");

        Assert.Equal(PetAIMode.Follow, pet.PetAIMode);
    }
}
