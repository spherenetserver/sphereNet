using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;

using SphereNet.Game.Clients;
using SphereNet.Game.Death;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X crime/witness parity (wiki/notoriety-crime-remaining.txt): the
// CheckCrimeSeen witness pipeline (personal SawCrime grey), and the death-credit
// fame/karma attacker-split + no-karma-for-killing-a-criminal rule.
[Collection("DefinitionLoaderSerial")]
public class CrimeWitnessParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character MakePlayer(GameWorld world, int x, int y = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true; ch.BodyId = 0x0190;
        world.PlaceCharacter(ch, new Point3D((short)x, (short)y, 0, 0));
        return ch;
    }

    // ---- CheckCrimeSeen witness pipeline ----

    [Fact]
    public void CheckCrimeSeen_WitnessInRange_RecordsSawCrime_AndShowsGrey()
    {
        var world = CreateWorld();
        var thief = MakePlayer(world, 100);
        thief.SetSkill(SkillType.Stealing, 0); // low skill → witness always wins the contest
        var witness = MakePlayer(world, 101);

        bool seen = CrimeWitnessService.CheckCrimeSeen(world, thief, null, SkillType.Stealing, new Random(1));

        Assert.True(seen);
        Assert.NotNull(witness.Memory_FindObjTypes(thief.Uid, MemoryType.SawCrime));
        Assert.Equal(4, GameClient.ComputeNotoriety(world, witness, thief)); // personal grey
        Assert.Equal(1, GameClient.ComputeNotoriety(world, thief, witness));  // witness still innocent to thief
    }

    [Fact]
    public void CheckCrimeSeen_NoWitness_NotSeen_NoGlobalFlag()
    {
        var world = CreateWorld();
        var thief = MakePlayer(world, 100);
        thief.SetSkill(SkillType.Stealing, 0);

        bool seen = CrimeWitnessService.CheckCrimeSeen(world, thief, null, SkillType.Stealing, new Random(1));

        Assert.False(seen);
        Assert.False(thief.IsStatFlag(StatFlag.Criminal)); // an unseen theft has no consequence
    }

    [Fact]
    public void CheckCrimeSeen_WitnessOutOfRange_NotSeen()
    {
        var world = CreateWorld();
        var thief = MakePlayer(world, 100);
        thief.SetSkill(SkillType.Stealing, 0);
        MakePlayer(world, 100, 200); // far beyond WitnessRange

        bool seen = CrimeWitnessService.CheckCrimeSeen(world, thief, null, SkillType.Stealing, new Random(1));

        Assert.False(seen);
    }

    [Fact]
    public void CheckCrimeSeen_NpcWitnessInGuardedRegion_FlagsCriminalGlobally()
    {
        var world = CreateWorld();
        var region = new SphereNet.Game.World.Regions.Region
        { Name = "guardzone", Flags = RegionFlag.Guarded, MapIndex = 0 };
        region.AddRect(0, 0, 6000, 4000);
        world.AddRegion(region);

        var thief = MakePlayer(world, 100);
        thief.SetSkill(SkillType.Stealing, 0);

        // A non-guard NPC that can speak witnesses the theft inside a guarded
        // region → the thief is flagged globally and the guards are called.
        var npc = MakeSpeakingNpc(world, 101);
        var called = new List<Character>();
        CrimeWitnessService.OnNpcCallGuards = (w, c) => called.Add(c);

        CrimeWitnessService.CheckCrimeSeen(world, thief, null, SkillType.Stealing, new Random(1));

        Assert.True(thief.IsStatFlag(StatFlag.Criminal));
        Assert.Contains(thief, called);
    }

    private static Character MakeSpeakingNpc(GameWorld world, int x)
    {
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;
        npc.DSpeech.Add(new ResourceId(ResType.Speech, 1));
        world.PlaceCharacter(npc, new Point3D((short)x, 100, 0, 0));
        return npc;
    }

    [Fact]
    public void NpcWitness_OutsideGuardedArea_StillFlagsCriminal_ButCallsNoGuards()
    {
        // OnNoticeCrime (CCharFight.cpp:79-91): a speaking NPC calls Noto_Criminal in
        // ANY region; only the guard call needs a guarded area.
        var world = CreateWorld();
        var thief = MakePlayer(world, 100);
        MakeSpeakingNpc(world, 101);
        bool called = false;
        CrimeWitnessService.OnNpcCallGuards = (_, _) => called = true;

        CrimeWitnessService.CheckCrimeSeen(world, thief, null, null, new Random(1));

        Assert.True(thief.IsStatFlag(StatFlag.Criminal));
        Assert.False(called);
    }

    [Fact]
    public void MuteNpcWitness_OnlyRemembersTheCrime()
    {
        var world = CreateWorld();
        var thief = MakePlayer(world, 100);
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        CrimeWitnessService.CheckCrimeSeen(world, thief, null, null, new Random(1));

        Assert.NotNull(npc.Memory_FindObjTypes(thief.Uid, MemoryType.SawCrime));
        Assert.False(thief.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void PlayerWitness_FlagsGloballyOnlyWhenSeeCrimeSetsArgn1()
    {
        var world = CreateWorld();
        var thief = MakePlayer(world, 100);
        var witness = MakePlayer(world, 101);

        CrimeWitnessService.CheckCrimeSeen(world, thief, null, null, new Random(1));
        Assert.False(thief.IsStatFlag(StatFlag.Criminal));
        Assert.NotNull(witness.Memory_FindObjTypes(thief.Uid, MemoryType.SawCrime));

        CrimeWitnessService.OnSeeCrime = (_, _, _) => true; // ARGN1 = 1
        CrimeWitnessService.CheckCrimeSeen(world, thief, null, null, new Random(1));
        Assert.True(thief.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void SnoopCriminalChance_Zero_NoticesButNeverRecordsCrime()
    {
        var world = CreateWorld();
        var thief = MakePlayer(world, 100);
        var witness = MakePlayer(world, 101);
        CrimeWitnessService.SnoopCriminalChance = 0;

        bool seen = CrimeWitnessService.CheckCrimeSeen(world, thief, null, SkillType.Snooping,
            new Random(1), isSnoop: true);

        Assert.True(seen);
        Assert.Null(witness.Memory_FindObjTypes(thief.Uid, MemoryType.SawCrime));
    }

    [Fact]
    public void MakeCriminal_IsNoOpForNpcsAndGms()
    {
        var world = CreateWorld();
        var npc = world.CreateCharacter();
        npc.MakeCriminal();
        Assert.False(npc.IsStatFlag(StatFlag.Criminal));

        var gm = MakePlayer(world, 100);
        gm.PrivLevel = PrivLevel.GM;
        gm.MakeCriminal();
        Assert.False(gm.IsStatFlag(StatFlag.Criminal));
    }

    [Fact]
    public void AttackingInnocentPlayer_VictimRecordsCrime_NoGlobalFlagWithoutWitness()
    {
        // OnAttackedBy (CCharFight.cpp:361-366): a player victim notices the crime
        // itself - SAWCRIME (personal grey) - but the attacker is not flagged
        // globally unless @SeeCrime asks for it.
        var world = CreateWorld();
        var attacker = MakePlayer(world, 100);
        var victim = MakePlayer(world, 101);

        victim.OnAttackedBy(attacker);

        Assert.NotNull(victim.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
        Assert.False(attacker.IsStatFlag(StatFlag.Criminal));
        Assert.Equal(4, GameClient.ComputeNotoriety(world, victim, attacker));
    }

    [Fact]
    public void AttackingInnocentNpc_SpeakingBystanderFlagsTheAttacker()
    {
        var world = CreateWorld();
        var attacker = MakePlayer(world, 100);
        var victim = world.CreateCharacter();
        victim.NpcBrain = NpcBrainType.Human;
        victim.Karma = 1000;
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));
        MakeSpeakingNpc(world, 102);

        victim.OnAttackedBy(attacker);

        Assert.True(attacker.IsStatFlag(StatFlag.Criminal));
    }

    // ---- death-credit fame/karma split ----

    [Fact]
    public void GankKill_SplitsFameAcrossAttackers()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100); victim.Fame = 1000; victim.Karma = 1000;
        var k1 = MakePlayer(world, 101); k1.Fame = 0;
        var k2 = MakePlayer(world, 102); k2.Fame = 0;

        victim.RecordAttack(k1.Uid, 10);
        victim.RecordAttack(k2.Uid, 10);

        death.ProcessDeath(victim, k1);

        // PC fame 1000 /10 = 100, split across 2 attackers → 50 each.
        Assert.Equal(50, k1.Fame);
        Assert.Equal(50, k2.Fame);
    }

    [Fact]
    public void SoloKill_FameNotSplit()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100); victim.Fame = 1000; victim.Karma = 1000;
        var killer = MakePlayer(world, 101); killer.Fame = 0;

        death.ProcessDeath(victim, killer);

        Assert.Equal(100, killer.Fame); // 1000/10, single attacker
    }

    [Fact]
    public void KillingEvilCriminal_StillGrantsKarma()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100); victim.Karma = -500;
        victim.SetCriminal(120_000); // arm the criminal timer → IsCriminal
        var killer = MakePlayer(world, 101); killer.Karma = 0;

        death.ProcessDeath(victim, killer);

        // Source-X Calc_KarmaKill (CResourceCalc.cpp:358-363) only cancels a LOSS
        // for a criminal-or-worse victim; the gain for its negative karma stays:
        // +500 /10 (PC) = +50. (The old test asserted 0 - an invented rule.)
        Assert.Equal(50, killer.Karma);
    }

    [Fact]
    public void KillingGoodCriminal_CostsNoKarma()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100); victim.Karma = 3000;
        victim.SetCriminal(120_000);
        var killer = MakePlayer(world, 101); killer.Karma = 0;

        death.ProcessDeath(victim, killer);

        Assert.Equal(0, killer.Karma); // the -300 loss is clamped to 0
    }

    [Fact]
    public void KarmaScale_HalvesTheGainBeforeTheSixtyFourthThreshold()
    {
        // Calc_KarmaScale (CResourceCalc.cpp:387-407): karma 6400, raw gain +150 →
        // halved to 75 → 75 < 6400/64 = 100 → dropped. The old order checked the
        // threshold on the unhalved 150 and awarded 75.
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100); victim.Karma = -1500;
        var killer = MakePlayer(world, 101); killer.Karma = 6400;

        death.ProcessDeath(victim, killer);

        Assert.Equal(6400, killer.Karma);
    }
}
