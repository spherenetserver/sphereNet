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
        public void SysMessage(string text) { }
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
}
