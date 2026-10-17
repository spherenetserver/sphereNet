using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>Hiding and Detecting Hidden as Source-X stages them (CCharSkill.cpp:1700-1755,
/// 2468-2518).</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class HidingStageTests
{
    private sealed class Sink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(2);
        public List<string> Messages { get; } = [];
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private static (Character, Sink) Setup()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (ch, new Sink(ch, world));
    }

    /// <summary>START: rand(70), judged when the skill starts.</summary>
    [Fact]
    public void HidingStartsWithItsDifficulty()
    {
        var (_, sink) = Setup();
        Assert.True(ActiveSkillEngine.HasStartStage(SkillType.Hiding));
        Assert.InRange(ActiveSkillEngine.StartStage(sink, SkillType.Hiding, null, null), 0, 69);
    }

    /// <summary>A failed hide is Reveal(): its hook runs, a concealment ends.</summary>
    [Fact]
    public void AFailedHideReveals()
    {
        var (ch, sink) = Setup();
        ch.SetStatFlag(StatFlag.Invisible);
        int asked = 0;
        var saved = Character.OnRevealing;
        Character.OnRevealing = _ => { asked++; return true; };
        Character.OnSkillUseQuick = (_, _, _, _) => 0;
        try
        {
            Assert.False(ActiveSkillEngine.Hiding(sink, 20));
            Assert.Equal(1, asked);
            Assert.False(ch.IsStatFlag(StatFlag.Invisible));
        }
        finally
        {
            Character.OnRevealing = saved;
            Character.OnSkillUseQuick = null;
        }
    }

    /// <summary>With REVEALF_DETECTINGHIDDEN off, a successful look succeeds and does
    /// nothing (CCharSkill.cpp:1716).</summary>
    [Fact]
    public void DetectingWithTheEffectOffSucceeds()
    {
        var (_, sink) = Setup();
        var saved = Character.ActiveRevealFlags;
        Character.ActiveRevealFlags = RevealFlags.None;
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        try
        {
            Assert.True(ActiveSkillEngine.DetectHidden(sink, 10));
        }
        finally
        {
            Character.ActiveRevealFlags = saved;
            Character.OnSkillUseQuick = null;
        }
    }

    /// <summary>Meditation's START says DEFMSG_MEDITATION_TRY when the skill starts and
    /// answers rand(100); at full mana it is at peace (DEFMSG_MEDITATION_PEACE_1).</summary>
    [Fact]
    public void MeditationTriesAtStart()
    {
        var (ch, sink) = Setup();
        ch.MaxMana = 50;
        ch.Mana = 10;
        Assert.InRange(ActiveSkillEngine.StartStage(sink, SkillType.Meditation, null, null), 0, 99);
        Assert.Equal(SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.MeditationTry), sink.Messages.Single());

        ch.Mana = 50;
        Assert.True(ActiveSkillEngine.MeditationStart(sink) < 0);
        Assert.Equal(SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.MeditationPeace1), sink.Messages.Last());
    }

    /// <summary>A pack's @Start that sets ACTDIFF back to 0 over the peace refusal lets
    /// the skill reach SUCCESS at full mana: at peace (PEACE_2), a success.</summary>
    [Fact]
    public void ASuccessAtFullManaIsPeace()
    {
        var (ch, sink) = Setup();
        ch.MaxMana = 50;
        ch.Mana = 50;
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        try
        {
            Assert.True(ActiveSkillEngine.Meditation(sink, 0));
            Assert.Equal(SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.MeditationPeace2), sink.Messages.Single());
            Assert.False(ch.IsStatFlag(StatFlag.Meditation));
        }
        finally { Character.OnSkillUseQuick = null; }
    }
}
