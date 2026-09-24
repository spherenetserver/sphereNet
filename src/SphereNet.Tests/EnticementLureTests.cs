using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Enticement lures a creature to the bard (Skill_Enticement,
/// CCharSkill.cpp:1969): it walks to where the bard stands, running when further
/// than the skill's range; a player or a creature at war cannot be lured. It was a
/// defence debuff the reference does not have.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class EnticementLureTests
{
    private static (GameWorld World, Character Bard, RecordingSink Sink) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var bard = world.CreateCharacter();
        bard.SetSkill(SkillType.Musicianship, 1000);
        bard.SetSkill(SkillType.Enticement, 1000);
        bard.PrivLevel = PrivLevel.GM; // deterministic rolls
        world.PlaceCharacter(bard, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        bard.Backpack = pack;
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        pack.AddItem(lute);
        return (world, bard, new RecordingSink(bard, world));
    }

    private static Character Creature(GameWorld world, int x)
    {
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Animal;
        world.PlaceCharacter(npc, new Point3D((short)x, 100, 0, 0));
        return npc;
    }

    [Fact]
    public void ANearCreatureWalksToTheBard()
    {
        var (world, bard, sink) = Setup();
        var deer = Creature(world, 102);

        Assert.True(ActiveSkillEngine.Enticement(sink, deer));

        Assert.Equal(bard.Position, deer.ActP);
        Assert.Equal(NpcAction.GoTo, (NpcAction)deer.Action);
    }

    [Fact]
    public void AFarCreatureRuns()
    {
        var (world, _, sink) = Setup();
        var deer = Creature(world, 106);

        Assert.True(ActiveSkillEngine.Enticement(sink, deer));

        Assert.Equal(NpcAction.RunTo, (NpcAction)deer.Action);
    }

    [Fact]
    public void ACreatureAtWarIsNotLured()
    {
        var (world, _, sink) = Setup();
        var wolf = Creature(world, 102);
        wolf.SetStatFlag(StatFlag.War);

        Assert.False(ActiveSkillEngine.Enticement(sink, wolf));
        Assert.Equal(SkillType.None, wolf.Action);
        Assert.Contains(sink.Messages, m => m.Contains(wolf.Name ?? ""));
    }

    private sealed class RecordingSink(Character self, GameWorld world) : IActiveSkillSink
    {
        public List<string> Messages { get; } = [];
        public Character Self { get; } = self;
        public Random Random { get; } = new(1);
        public GameWorld World { get; } = world;
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(SphereNet.Game.Objects.ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public SphereNet.Game.Objects.Items.Item? FindBackpackItem(ItemType type)
        {
            foreach (var it in Self.Backpack?.Contents ?? [])
                if (it.ItemType == type) return it;
            return null;
        }
        public void ConsumeAmount(SphereNet.Game.Objects.Items.Item item, ushort amount = 1) { }
        public void DeliverItem(SphereNet.Game.Objects.Items.Item item) { }
    }
}
