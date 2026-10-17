using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;

namespace SphereNet.Tests;

/// <summary>Snooping and Stealing as Source-X stages them (CCharSkill.cpp:4108-4345):
/// START at Skill_Start, the target judged again after the DELAY.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ThiefStageTests
{
    private sealed class Sink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(11);
        public List<string> Messages { get; } = [];
        public List<Item> Opened { get; } = [];
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) { }
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
        public void OpenContainer(Item container) => Opened.Add(container);
    }

    // A mark is an NPC: an offline player is out of sight, and CanTouch says so.
    private static Character Person(GameWorld world, short x, string name)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = name == "Thief";
        ch.NpcBrain = NpcBrainType.Human;
        ch.Name = name;
        ch.Str = 100;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    /// <summary>START: nobody snoops a higher rank (DEFMSG_SNOOPING_CANT); the
    /// difficulty is 0 or 100.</summary>
    [Fact]
    public void SnoopingStartRefusesAHigherRankAndRollsAllOrNothing()
    {
        var world = TestHarness.CreateWorld();
        var thief = Person(world, 100, "Thief");
        var mark = Person(world, 101, "Mark");
        var sink = new Sink(thief, world);

        int diff = ActiveSkillEngine.StartStage(sink, SkillType.Snooping, mark.Backpack, null);
        Assert.Contains(diff, new[] { 0, 100 });

        mark.PrivLevel = PrivLevel.Counsel;
        Assert.True(ActiveSkillEngine.SnoopingStart(sink, mark.Backpack) < 0);
        Assert.Equal(ServerMessages.Get(Msg.SnoopingCant), sink.Messages.Last());
    }

    /// <summary>The reach is the mark's distance, by the skill's RANGE (1 by default):
    /// DEFMSG_SNOOPING_REACH.</summary>
    [Fact]
    public void SnoopingReachIsToTheMark()
    {
        var world = TestHarness.CreateWorld();
        var thief = Person(world, 100, "Thief");
        var mark = Person(world, 103, "Mark");
        var sink = new Sink(thief, world);

        Assert.True(ActiveSkillEngine.SnoopingStart(sink, mark.Backpack) < 0);
        Assert.Equal(ServerMessages.Get(Msg.SnoopingReach), sink.Messages.Single());
    }

    /// <summary>A pet's or a friend's things are no crime (IsTakeCrime), nor a GM's
    /// reach into anything.</summary>
    [Fact]
    public void IsTakeCrimeSparesOnesOwnPetsAndGms()
    {
        var world = TestHarness.CreateWorld();
        var thief = Person(world, 100, "Thief");
        var pet = Person(world, 101, "Pet");
        pet.IsPlayer = false;
        pet.NpcBrain = NpcBrainType.Animal;
        pet.SetStatFlag(StatFlag.Pet);
        pet.SetTag("OWNER_UID", thief.Uid.Value.ToString());
        var mark = Person(world, 101, "Mark");

        Assert.False(ActiveSkillEngine.IsTakeCrime(thief, pet.Backpack!, out _));
        Assert.True(ActiveSkillEngine.IsTakeCrime(thief, mark.Backpack!, out var who));
        Assert.Same(mark, who);
    }

    /// <summary>A character picked for Stealing means a random thing from their pack,
    /// kept as ACT (CCharSkill.cpp:4194-4212); it said "nothing".</summary>
    [Fact]
    public void StealingFromACharacterPicksFromTheirPack()
    {
        var world = TestHarness.CreateWorld();
        var thief = Person(world, 100, "Thief");
        var mark = Person(world, 101, "Mark");
        var gem = world.CreateItem();
        gem.BaseId = 0x0F26;
        mark.Backpack!.AddItem(gem);
        var sink = new Sink(thief, world);

        int diff = ActiveSkillEngine.StealingStart(sink, mark);
        Assert.True(diff >= 0, string.Join(" | ", sink.Messages));
        Assert.Equal(gem.Uid, thief.Act);
    }

    /// <summary>Taking what is no crime is DEFMSG_STEALING_NONEED, a safe area
    /// DEFMSG_STEALING_STOP, a corpse's contents DEFMSG_STEALING_CORPSE.</summary>
    [Fact]
    public void StealingRefusals()
    {
        var world = TestHarness.CreateWorld();
        var thief = Person(world, 100, "Thief");
        var sink = new Sink(thief, world);

        var loose = world.CreateItem();
        world.PlaceItem(loose, new Point3D(101, 100, 0, 0));
        Assert.True(ActiveSkillEngine.StealingStart(sink, loose) < 0);
        Assert.Equal(ServerMessages.Get(Msg.StealingNoneed), sink.Messages.Last());

        var corpse = world.CreateItem();
        corpse.ItemType = ItemType.Corpse;
        world.PlaceItem(corpse, new Point3D(101, 100, 0, 0));
        var ring = world.CreateItem();
        corpse.AddItem(ring);
        Assert.True(ActiveSkillEngine.StealingStart(sink, ring) < 0);
        Assert.Equal(ServerMessages.Get(Msg.StealingCorpse), sink.Messages.Last());

        var mark = Person(world, 101, "Mark");
        var gem = world.CreateItem();
        mark.Backpack!.AddItem(gem);
        var safe = new Region { Name = "Haven", MapIndex = 0, Flags = RegionFlag.Safe };
        safe.AddRect(0, 0, 300, 300);
        world.AddRegion(safe);
        Assert.True(ActiveSkillEngine.StealingStart(sink, gem) < 0);
        Assert.Equal(ServerMessages.Get(Msg.StealingStop), sink.Messages.Last());
    }
}
