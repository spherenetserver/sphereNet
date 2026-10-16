using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>Tracking as Source-X Cmd_Skill_Tracking and Skill_Tracking run it
/// (CClientUse.cpp:1090-1252, CCharSkill.cpp:1289-1343), and Begging's START line.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TrackingSkillTests
{
    private sealed class Sink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(7);
        public List<string> Messages { get; } = [];
        public List<(ObjBase Target, string Text)> Overhead { get; } = [];
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Overhead.Add((target, text));
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private static Character Place(GameWorld world, int x, bool player, NpcBrainType brain, string name)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.NpcBrain = brain;
        ch.Name = name;
        ch.BodyId = (ushort)(player ? 0x0190 : 0x00CD);
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    /// <summary>The Humans search is the human brain group: townsfolk of any trade and
    /// players alike (GetNPCBrainGroup). Only humans-brained NPCs who were not players
    /// were listed.</summary>
    [Fact]
    public void HumansAreTheBrainGroup_TownsfolkAndPlayers()
    {
        var world = TestHarness.CreateWorld();
        var tracker = Place(world, 100, true, NpcBrainType.Human, "Tracker");
        tracker.SetSkill(SkillType.Tracking, 1000);
        var player = Place(world, 105, true, NpcBrainType.Human, "Wanderer");
        var banker = Place(world, 106, false, NpcBrainType.Banker, "Banker");
        Place(world, 107, false, NpcBrainType.Animal, "Rabbit");

        var found = ActiveSkillEngine.FindTrackingTargets(new Sink(tracker, world),
            ActiveSkillEngine.TrackingCategory.Humans, 30);

        Assert.Equal([player, banker], found);
    }

    /// <summary>There is no success roll on the search: what is in reach is listed, and
    /// the Skill_UseQuick is only for the gain. A failed roll used to refuse the list.</summary>
    [Fact]
    public void TheListDoesNotHangOnASkillRoll()
    {
        var world = TestHarness.CreateWorld();
        var tracker = Place(world, 100, true, NpcBrainType.Human, "Tracker");
        Place(world, 104, false, NpcBrainType.Animal, "Rabbit");
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => -1;
        try
        {
            var sink = new Sink(tracker, world);
            Assert.True(ActiveSkillEngine.Tracking(sink, ActiveSkillEngine.TrackingCategory.Animals, out var targets));
            Assert.Single(targets);
            Assert.Empty(sink.Messages);
        }
        finally { Character.OnSkillUseQuickDetailed = null; }
    }

    /// <summary>A stroke says over the tracker where the quarry is; out of reach, the
    /// tracking ends.</summary>
    [Fact]
    public void AStrokeSaysWhereTheQuarryIs_AndEndsOutOfReach()
    {
        var world = TestHarness.CreateWorld();
        var tracker = Place(world, 100, true, NpcBrainType.Human, "Tracker");
        var rabbit = Place(world, 105, false, NpcBrainType.Animal, "Rabbit");
        var sink = new Sink(tracker, world);

        Assert.True(ActiveSkillEngine.TrackingStroke(sink, rabbit, 20));
        var (target, text) = Assert.Single(sink.Overhead);
        Assert.Same(tracker, target);
        Assert.Equal(ServerMessages.GetFormatted(Msg.TrackingResult1, "Rabbit", ServerMessages.Get(Msg.MapDir2)), text);

        Assert.False(ActiveSkillEngine.TrackingStroke(sink, rabbit, 4));
    }

    /// <summary>Begging's START line is said when the skill starts, and begging asks no
    /// reach of its mark (Skill_Begging, CCharSkill.cpp:2935-2946); a 3-tile reach failed
    /// it without a word.</summary>
    [Fact]
    public void BeggingHasNoReach_AndItsLineNamesTheMark()
    {
        var world = TestHarness.CreateWorld();
        var beggar = Place(world, 100, true, NpcBrainType.Human, "Beggar");
        var mark = Place(world, 110, false, NpcBrainType.Human, "Merchant");
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        try
        {
            Assert.True(ActiveSkillEngine.Begging(new Sink(beggar, world), mark));
            Assert.Equal(ServerMessages.GetFormatted(Msg.BeggingStart, "Merchant"),
                ActiveSkillEngine.BeggingStartLine(beggar, mark));
            Assert.Null(ActiveSkillEngine.BeggingStartLine(beggar, beggar));
        }
        finally { Character.OnSkillUseQuickDetailed = null; }
    }
}
