using System;
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

        // A non-guard NPC witnesses the theft inside a guarded region → guards
        // are called and the thief is flagged globally criminal.
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));

        CrimeWitnessService.CheckCrimeSeen(world, thief, null, SkillType.Stealing, new Random(1));

        Assert.True(thief.IsStatFlag(StatFlag.Criminal));
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
    public void KillingCriminal_GrantsNoKarma()
    {
        var world = CreateWorld();
        var death = new DeathEngine(world);
        var victim = MakePlayer(world, 100); victim.Karma = -500;
        victim.SetCriminal(120_000); // arm the criminal timer → IsCriminal
        var killer = MakePlayer(world, 101); killer.Karma = 0;

        death.ProcessDeath(victim, killer);

        // Source-X: killing a criminal yields no karma — neither loss nor the
        // +50 gain the old code awarded for the victim's negative karma.
        Assert.Equal(0, killer.Karma);
    }
}
