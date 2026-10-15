using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What snooping and stealing say, and to whom, against Source-X. CheckCrimeSeen
/// (CCharFight.cpp:128-172) shows each witness who noticed "You notice X attempting
/// to peek into Y's backpack" (or "stealing" / "attempting to steal" for a theft) over
/// the thief; the mark is never a witness. SphereNet sent SNOOPING_ATTEMPTING to the
/// snooper as a bare fragment, never told a witness anything, and greeted every theft
/// from a person with STEALING_PICKPOCKET - the training-dummy line ("Just dclick this
/// to practice stealing"). A stolen item also has to leave every screen it was on
/// (CItemContainer::OnInsert -> RemoveFromView); the mark's open pack kept showing it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class ThiefNoticeTests
{
    private sealed class Sink : IActiveSkillSink
    {
        public Character Self { get; }
        public Random Random { get; } = new(7);
        public GameWorld World { get; }
        public List<string> Said { get; } = new();
        public Sink(Character self, GameWorld world) { Self = self; World = world; }
        public void SysMessage(string text) => Said.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Said.Add(text);
        public void Emote(string text) => Said.Add(text);
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => null;
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, string name, int x)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Name = name;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.Name = "backpack";
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    [Fact]
    public void AWitnessIsToldWhatItSawAndTheMarkIsNot()
    {
        var world = CreateWorld();
        var thief = Player(world, "Thief", 100);
        var mark = Player(world, "Mark", 101);
        var witness = Player(world, "Witness", 102);
        thief.SetSkill(SkillType.Snooping, 0);
        var told = new List<(Character Witness, Character Over, string Text)>();
        CrimeWitnessService.OnWitnessNotice = (w, c, text) => told.Add((w, c, text));

        CrimeWitnessService.CheckCrimeSeen(world, thief, mark, SkillType.Snooping, new Random(1), isSnoop: true,
            item: mark.Backpack, action: ServerMessages.Get(Msg.SnoopingAttempting));

        var line = Assert.Single(told);
        Assert.Same(witness, line.Witness);
        Assert.Same(thief, line.Over);
        Assert.Equal("You notice Thief attempting to peek into Mark's backpack", line.Text);
    }

    [Fact]
    public void TheSnooperIsNotHandedTheWitnessFragment()
    {
        var world = CreateWorld();
        var thief = Player(world, "Thief", 100);
        var mark = Player(world, "Mark", 101);
        thief.SetSkill(SkillType.Snooping, 1000);
        var sink = new Sink(thief, world);

        ActiveSkillEngine.Snooping(sink, mark.Backpack);

        Assert.DoesNotContain(ServerMessages.Get(Msg.SnoopingAttempting), sink.Said);
    }

    [Fact]
    public void StealingFromAPersonIsNotTheTrainingDummyLine()
    {
        var world = CreateWorld();
        var thief = Player(world, "Thief", 100);
        var mark = Player(world, "Mark", 101);
        thief.SetSkill(SkillType.Stealing, 1000);
        var gears = world.CreateItem();
        gears.BaseId = 0x1053;
        mark.Backpack!.AddItem(gears);
        var sink = new Sink(thief, world);

        ActiveSkillEngine.Stealing(sink, gears);

        Assert.DoesNotContain(sink.Said, s => s == ServerMessages.Get(Msg.StealingPickpocket) ||
            s.Contains("practice stealing", StringComparison.Ordinal));
    }

    [Fact]
    public void AStolenItemIsTakenOffEveryScreen()
    {
        var world = CreateWorld();
        var thief = Player(world, "Thief", 100);
        var mark = Player(world, "Mark", 101);
        thief.SetSkill(SkillType.Stealing, 1000);
        var gears = world.CreateItem();
        gears.BaseId = 0x1053;
        mark.Backpack!.AddItem(gears);
        var removed = new List<byte[]>();
        ObjBase.BroadcastNearby = (_, _, pkt, _) => removed.Add(pkt.Build().Span.ToArray());

        bool stolen = false;
        for (int i = 0; i < 20 && !stolen; i++)
            stolen = ActiveSkillEngine.Stealing(new Sink(thief, world), gears);

        Assert.True(stolen);
        Assert.Contains(removed, p => p[0] == 0x1D &&
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(1)) == gears.Uid.Value);
    }
}
