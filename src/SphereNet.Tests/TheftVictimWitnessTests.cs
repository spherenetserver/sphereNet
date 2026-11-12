using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class TheftVictimWitnessTests
{
    private sealed class FixedRoll(int perception, int other = 0) : Random
    {
        public override int Next(int maxValue) => maxValue == 1000 ? perception : other % maxValue;
    }

    private static Character Actor(GameWorld world, int x, bool player = true)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = ch.IsOnline = player;
        ch.PrivLevel = PrivLevel.Player;
        ch.Name = player ? "Player" : "Vendor";
        ch.NpcBrain = NpcBrainType.Human;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    [Theory]
    [InlineData(SkillType.Snooping)]
    [InlineData(SkillType.Stealing)]
    public void VictimGetsBonusWhileBystanderDoesNot(SkillType skill)
    {
        var world = TestHarness.CreateWorld();
        var thief = Actor(world, 100);
        thief.SetSkill(skill, 400);
        var victim = Actor(world, 101);
        var bystander = Actor(world, 102);

        bool seen = CrimeWitnessService.CheckCrimeSeen(world, thief, victim, skill,
            new FixedRoll(700), isSnoop: skill == SkillType.Snooping);

        // Base chance 600/1000, victim 600 + 600/3 = 800/1000.
        Assert.True(seen);
        Assert.NotNull(victim.Memory_FindObjTypes(thief.Uid, MemoryType.SawCrime));
        Assert.Null(bystander.Memory_FindObjTypes(thief.Uid, MemoryType.SawCrime));
        Assert.Equal(4, GameClient.ComputeNotoriety(world, victim, thief));
        Assert.False(thief.IsCriminal); // personal grey, unless @SeeCrime requests otherwise
    }

    [Theory]
    [InlineData(40, true)]
    [InlineData(51, false)]
    public void VictimPerceptionHasFivePercentFloor(int roll, bool expected)
    {
        var world = TestHarness.CreateWorld();
        var thief = Actor(world, 100);
        thief.SetSkill(SkillType.Stealing, 1500);
        var victim = Actor(world, 101);

        Assert.Equal(expected, CrimeWitnessService.CheckCrimeSeen(world, thief, victim,
            SkillType.Stealing, new FixedRoll(roll)));
    }

    [Fact]
    public void SnoopVictimRemembersIrritationEvenWhenCrimeChanceIsZero()
    {
        var world = TestHarness.CreateWorld();
        var thief = Actor(world, 100);
        var victim = Actor(world, 101);
        CrimeWitnessService.SnoopCriminalChance = 0;

        Assert.True(CrimeWitnessService.CheckCrimeSeen(world, thief, victim, SkillType.Snooping,
            new FixedRoll(0), isSnoop: true));

        Assert.NotNull(victim.Memory_FindObjTypes(thief.Uid, MemoryType.IrritatedBy));
        Assert.Null(victim.Memory_FindObjTypes(thief.Uid, MemoryType.SawCrime));
    }

    [Fact]
    public void SeeSnoopVetoSuppressesVictimNoticeAndReaction()
    {
        var world = TestHarness.CreateWorld();
        var thief = Actor(world, 100);
        var victim = Actor(world, 101, player: false);
        int calls = 0;
        CrimeWitnessService.OnSeeSnoop = (w, c, mark) =>
        {
            Assert.Same(victim, w);
            Assert.Same(thief, c);
            Assert.Same(victim, mark);
            calls++;
            return true;
        };

        Assert.False(CrimeWitnessService.CheckCrimeSeen(world, thief, victim, SkillType.Snooping,
            new FixedRoll(0), isSnoop: true));

        Assert.Equal(1, calls);
        Assert.Null(victim.Memory_FindObjTypes(thief.Uid, MemoryType.IrritatedBy | MemoryType.SawCrime));
        Assert.NotEqual((SkillType)NpcAction.Flee, victim.Action);
    }

    [Fact]
    public void VictimAndBystanderReceiveDifferentPossessiveMessages()
    {
        var world = TestHarness.CreateWorld();
        var thief = Actor(world, 100);
        var victim = Actor(world, 101);
        victim.Name = "Victim";
        var bystander = Actor(world, 102);
        var item = world.CreateItem();
        item.Name = "ruby";
        var notices = new Dictionary<Character, string>();
        CrimeWitnessService.OnWitnessNotice = (w, _, text) => notices[w] = text;

        CrimeWitnessService.CheckCrimeSeen(world, thief, victim, SkillType.Stealing,
            new FixedRoll(0), item: item, action: "stealing");

        Assert.Equal(ServerMessages.GetFormatted(Msg.MsgYounotice2, thief.Name, "stealing",
            ServerMessages.Get(Msg.MsgYounoticeYour), "", item.Name), notices[victim]);
        Assert.Equal(ServerMessages.GetFormatted(Msg.MsgYounotice2, thief.Name, "stealing",
            victim.Name, ServerMessages.Get(Msg.MsgYounoticeS), item.Name), notices[bystander]);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void NpcSnoopVictimCanSpeakAndFleeWithoutMakingThiefCriminal(int otherRoll, bool flee)
    {
        var world = TestHarness.CreateWorld();
        var thief = Actor(world, 100);
        var victim = Actor(world, 101, player: false);
        victim.DSpeech.Add(new ResourceId(ResType.Speech, 1));
        CrimeWitnessService.SnoopCriminalChance = 0;
        int speechPackets = 0;
        Character.BroadcastNearby = (_, _, _, _) => speechPackets++;

        CrimeWitnessService.CheckCrimeSeen(world, thief, victim, SkillType.Snooping,
            new FixedRoll(0, otherRoll), isSnoop: true);

        Assert.Equal(1, speechPackets);
        Assert.Equal(flee, victim.Action == (SkillType)NpcAction.Flee);
        if (flee)
        {
            Assert.Equal(thief.Uid, victim.Act);
            Assert.Equal(20, victim.FleeStepsMax);
            Assert.Equal(0, victim.FleeStepsCurrent);
        }
        Assert.False(thief.IsCriminal);
    }

    [Theory]
    [InlineData(SkillType.None)]
    [InlineData(SkillType.Provocation)]
    public void OtherCrimesKeepTheirExistingMarkExclusion(SkillType skill)
    {
        var world = TestHarness.CreateWorld();
        var thief = Actor(world, 100);
        var victim = Actor(world, 101);
        Assert.False(CrimeWitnessService.CheckCrimeSeen(world, thief, victim, skill, new FixedRoll(0)));
    }

    [Fact]
    public void OutOfRangeVictimDoesNotNoticeTheft()
    {
        var world = TestHarness.CreateWorld();
        var thief = Actor(world, 100);
        var victim = Actor(world, 150);
        Assert.False(CrimeWitnessService.CheckCrimeSeen(world, thief, victim, SkillType.Stealing, new FixedRoll(0)));
    }
}
