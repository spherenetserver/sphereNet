using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Who sees you loot a corpse (port plan İŞ-31 / PLAN-406).
///
/// Source-X CheckCorpseCrime does TWO things when the act is criminal
/// (CItemCorpse.cpp:132-133): it runs CheckCrimeSeen with the corpse's owner as the
/// mark - an overt crime, SKILL_NONE, so there is no perception contest and line of
/// sight is the whole test - and only then calls Noto_Criminal.
///
/// Only the flag was raised here. The people watching recorded no SAWCRIME, so the
/// looter did not even show grey to them; a guarded town's NPCs never called the
/// guards; and @SeeCrime never fired for a script to react to.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CorpseCrimeWitnessParityTests
{
    private static (GameWorld World, DeathEngine Deaths, Character Owner, Item Corpse) Setup()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var deaths = new DeathEngine(world);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        corpse.BaseId = 0x2006;
        corpse.SetTag("OWNER_UID", owner.Uid.Value.ToString());
        world.PlaceItem(corpse, new Point3D(100, 100, 0, 0));

        return (world, deaths, owner, corpse);
    }

    private static Character Actor(GameWorld world, Point3D at, bool player = true)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(ch, at);
        return ch;
    }

    [Fact]
    public void AWitnessInSightRemembersTheLooting()
    {
        var (world, deaths, _, corpse) = Setup();
        var looter = Actor(world, new Point3D(101, 100, 0, 0));
        var witness = Actor(world, new Point3D(103, 100, 0, 0));

        deaths.ReportCorpseCrime(looter, corpse);

        // The witness's own memory is what makes the looter grey to THEM
        // (Noto_CalcFlag / personal grey), separate from the global flag.
        Assert.True(witness.Memory_FindObjTypes(looter.Uid, MemoryType.SawCrime) != null);
        Assert.True(looter.IsCriminal);
    }

    [Fact]
    public void TheCorpsesOwnerIsTheMarkNotAWitness()
    {
        // The reference passes the owner-ghost as pCharMark, and CheckCrimeSeen
        // skips the mark: the victim does not "witness" the crime against itself.
        var (world, deaths, owner, corpse) = Setup();
        var looter = Actor(world, new Point3D(101, 100, 0, 0));

        deaths.ReportCorpseCrime(looter, corpse);

        Assert.Null(owner.Memory_FindObjTypes(looter.Uid, MemoryType.SawCrime));
    }

    [Fact]
    public void NobodyWatchingStillFlagsTheLooter()
    {
        // Noto_Criminal is unconditional in the reference - the witness pass only
        // decides who ELSE knows.
        var (world, deaths, _, corpse) = Setup();
        var looter = Actor(world, new Point3D(101, 100, 0, 0));

        deaths.ReportCorpseCrime(looter, corpse);

        Assert.True(looter.IsCriminal);
    }

    [Fact]
    public void CarvingAnInnocentsCorpseIsReportedToo()
    {
        // CheckCorpseCrime(fLooting=false), CCharUse.cpp:181.
        var (world, deaths, _, corpse) = Setup();
        var carver = Actor(world, new Point3D(101, 100, 0, 0));
        var witness = Actor(world, new Point3D(102, 100, 0, 0));

        deaths.CarveCorpse(carver, corpse);

        Assert.True(carver.IsCriminal);
        Assert.True(witness.Memory_FindObjTypes(carver.Uid, MemoryType.SawCrime) != null);
    }

    [Fact]
    public void ResolveCorpseOwnerFindsTheLivingOwnerOnly()
    {
        var (world, deaths, owner, corpse) = Setup();
        Assert.Same(owner, deaths.ResolveCorpseOwner(corpse));

        // A normal NPC is deleted on death, so its corpse has no living owner -
        // which is exactly why monster corpses are free to loot.
        world.DeleteObject(owner);
        owner.Delete();
        Assert.Null(deaths.ResolveCorpseOwner(corpse));
    }
}
