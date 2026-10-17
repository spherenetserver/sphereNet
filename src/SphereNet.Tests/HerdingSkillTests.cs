using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>Herding as Source-X Skill_Herding runs it (CCharSkill.cpp:2522-2619): the
/// animal walks to the spot, staff in GM mode teleport it, the kind is judged after
/// the roll, and a pet is refused at START as already tame.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class HerdingSkillTests
{
    private sealed class Sink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(5);
        public List<string> Messages { get; } = [];
        public List<string> Overhead { get; } = [];
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Overhead.Add(text);
        public void Emote(string text) { }
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) =>
            Self.Backpack?.Contents.FirstOrDefault(i => i.ItemType == type);
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private static (GameWorld World, Character Herder, Sink Sink) Setup()
    {
        var world = TestHarness.CreateWorld();
        var herder = world.CreateCharacter();
        herder.IsPlayer = true;
        herder.Name = "Shepherd";
        world.PlaceCharacter(herder, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        herder.Backpack = pack;
        var crook = world.CreateItem();
        crook.ItemType = ItemType.WeaponMaceCrook;
        pack.AddItem(crook);
        return (world, herder, new Sink(herder, world));
    }

    private static Character Creature(GameWorld world, NpcBrainType brain, string name)
    {
        var npc = world.CreateCharacter();
        npc.NpcBrain = brain;
        npc.Name = name;
        npc.Int = 20;
        world.PlaceCharacter(npc, new Point3D(103, 100, 0, 0));
        return npc;
    }

    /// <summary>A herded animal walks there (NPCACT_GOTO); it used to be teleported.</summary>
    [Fact]
    public void TheAnimalWalksToTheSpot()
    {
        var (world, _, sink) = Setup();
        var sheep = Creature(world, NpcBrainType.Animal, "Sheep");
        var spot = new Point3D(110, 104, 0, 0);
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        try
        {
            Assert.True(ActiveSkillEngine.Herding(sink, sheep, spot));
            Assert.Equal(new Point3D(103, 100, 0, 0), sheep.Position);
            Assert.Equal(spot, sheep.ActP);
            Assert.Equal(NpcAction.GoTo, (NpcAction)sheep.Action);
            Assert.Equal([ServerMessages.Get(Msg.HerdingSuccess)], sink.Overhead);
        }
        finally { Character.OnSkillUseQuick = null; }
    }

    /// <summary>Staff in GM mode move anyone of no higher rank, at once.</summary>
    [Fact]
    public void StaffInGmModeTeleportIt()
    {
        var (world, herder, sink) = Setup();
        herder.PrivLevel = PrivLevel.GM;
        var guard = Creature(world, NpcBrainType.Guard, "Guard");
        var spot = new Point3D(110, 104, 0, 0);
        Character.OnSkillUseQuick = (_, _, _, _) => 1;
        try
        {
            Assert.True(ActiveSkillEngine.Herding(sink, guard, spot));
            Assert.Equal(110, guard.X);
            Assert.Equal(104, guard.Y);
        }
        finally { Character.OnSkillUseQuick = null; }
    }

    /// <summary>What is not an animal is judged after the roll, at SUCCESS
    /// (DEFMSG_HERDING_PLAYER); the roll and its gain come first.</summary>
    [Fact]
    public void ANonAnimalIsRefusedAfterTheRoll()
    {
        var (world, _, sink) = Setup();
        var orc = Creature(world, NpcBrainType.Monster, "Orc");
        int rolls = 0;
        Character.OnSkillUseQuick = (_, _, _, _) => { rolls++; return 1; };
        try
        {
            Assert.False(ActiveSkillEngine.Herding(sink, orc, new Point3D(110, 104, 0, 0)));
            Assert.Equal(1, rolls);
            Assert.Equal($"Orc {ServerMessages.Get(Msg.HerdingPlayer)}", sink.Messages.Last());
        }
        finally { Character.OnSkillUseQuick = null; }
    }

    /// <summary>A pet is refused at START as already tame (DEFMSG_TAMING_TAME), with
    /// no roll.</summary>
    [Fact]
    public void APetIsAlreadyTame()
    {
        var (world, _, sink) = Setup();
        var horse = Creature(world, NpcBrainType.Animal, "Horse");
        horse.SetStatFlag(StatFlag.Pet);

        Assert.True(ActiveSkillEngine.HerdingStart(sink, horse, new Point3D(110, 104, 0, 0)) < 0);
        Assert.Equal(ServerMessages.GetFormatted(Msg.TamingTame, "Horse"), sink.Messages.Single());
    }
}
