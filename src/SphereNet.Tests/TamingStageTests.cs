using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>Animal Taming as Source-X stages it (CCharSkill.cpp:2270-2401): START at
/// Skill_Start, 2-5 strokes each said aloud, the creature judged at every stage.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class TamingStageTests
{
    private sealed class Sink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(4);
        public List<string> Messages { get; } = [];
        public List<string> Over { get; } = [];
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Over.Add(text);
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private static (GameWorld, Character, Character, Sink) Setup(short animalX = 102)
    {
        var world = TestHarness.CreateWorld();
        var tamer = world.CreateCharacter();
        tamer.IsPlayer = true;
        tamer.Name = "Tamer";
        world.PlaceCharacter(tamer, new Point3D(100, 100, 0, 0));
        var horse = world.CreateCharacter();
        horse.NpcBrain = NpcBrainType.Animal;
        horse.BodyId = 0x00C8;
        horse.Name = "Horse";
        horse.SetSkill(SkillType.Taming, 290);
        world.PlaceCharacter(horse, new Point3D(animalX, 100, 0, 0));
        return (world, tamer, horse, new Sink(tamer, world));
    }

    /// <summary>START: TAMING/10 and 2 to 5 strokes to come.</summary>
    [Fact]
    public void StartGivesTheDifficultyAndTheStrokes()
    {
        var (_, tamer, horse, sink) = Setup();
        Assert.True(ActiveSkillEngine.HasStartStage(SkillType.Taming));
        Assert.Equal(29, ActiveSkillEngine.StartStage(sink, SkillType.Taming, horse, null));
        Assert.InRange(tamer.SkillStrokesLeft, 2, 5);
    }

    /// <summary>Beyond RANGE (10) is DEFMSG_TAMING_REACH, not the line-of-sight line.</summary>
    [Fact]
    public void TooFarIsReach()
    {
        var (_, _, horse, sink) = Setup(animalX: 115);
        Assert.True(ActiveSkillEngine.TamingStart(sink, horse) < 0);
        Assert.Equal(ServerMessages.Get(Msg.TamingReach), sink.Messages.Single());
    }

    /// <summary>A stroke says one of DEFMSG_TAMING_1..4 aloud and counts down; with
    /// none left, or in GM mode, the strokes are done.</summary>
    [Fact]
    public void AStrokeIsSaidAloud()
    {
        var (_, tamer, horse, sink) = Setup();
        var spoken = new List<byte>();
        var saved = Character.BroadcastNearby;
        Character.BroadcastNearby = (_, _, pkt, _) => spoken.Add(pkt.Build().Span[0]);
        try
        {
            tamer.SkillStrokesLeft = 2;
            Assert.Equal(1, ActiveSkillEngine.TamingStroke(sink, horse));
            Assert.Equal(1, tamer.SkillStrokesLeft);
            Assert.Contains((byte)0x1C, spoken);   // ASCII speech

            tamer.SkillStrokesLeft = 0;
            Assert.Equal(0, ActiveSkillEngine.TamingStroke(sink, horse));
        }
        finally { Character.BroadcastNearby = saved; }
    }

    /// <summary>A creature this tamer tamed before takes them back with
    /// DEFMSG_TAMING_REMEMBER said over it (ObjMessage, :2386).</summary>
    [Fact]
    public void ATamedBeforeCreatureRemembersOverItself()
    {
        var (_, tamer, horse, sink) = Setup();
        var memory = horse.Memory_AddObjTypes(tamer.Uid, MemoryType.Speak);
        memory.More1 = (memory.More1 & 0xFFFF0000u) | Character.NpcMemActTamed;
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        try
        {
            Assert.True(ActiveSkillEngine.Taming(sink, horse, 29));
            Assert.Equal([ServerMessages.GetFormatted(Msg.TamingRemember, "Horse")], sink.Over);
        }
        finally { Character.OnSkillUseQuick = null; }
    }
}
