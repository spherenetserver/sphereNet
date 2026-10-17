using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>Bandages as Source-X Skill_Healing and OnTarg_Use_Item run them
/// (CCharSkill.cpp:2724-2886, CClientTarg.cpp:2006-2043): START at Skill_Start,
/// ACTPRV is the bandage, FAIL tells the patient, and the patient picks Healing or
/// Veterinary.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VeterinaryBandageTests
{
    private sealed class Sink(Character self, GameWorld world) : IActiveSkillSink
    {
        public Character Self { get; } = self;
        public GameWorld World { get; } = world;
        public Random Random { get; } = new(3);
        public List<string> Messages { get; } = [];
        public List<string> Emotes { get; } = [];
        public List<Item> Consumed { get; } = [];
        public Item? PackBandage { get; set; }
        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) { }
        public void Emote(string text) => Emotes.Add(text);
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => type == ItemType.Bandage ? PackBandage : null;
        public void ConsumeAmount(Item item, ushort amount = 1) => Consumed.Add(item);
        public void DeliverItem(Item item) { }
    }

    private static (GameWorld World, Character Healer, Character Patient) Setup()
    {
        var world = TestHarness.CreateWorld();
        var healer = world.CreateCharacter();
        healer.IsPlayer = true;
        healer.Name = "Healer";
        world.PlaceCharacter(healer, new Point3D(100, 100, 0, 0));
        var patient = world.CreateCharacter();
        patient.IsPlayer = true;
        patient.Name = "Patient";
        patient.MaxHits = 50;
        patient.Hits = 10;
        world.PlaceCharacter(patient, new Point3D(101, 100, 0, 0));
        return (world, healer, patient);
    }

    private static Item Bandage(GameWorld world)
    {
        var b = world.CreateItem();
        b.BaseId = 0x0E21;
        b.ItemType = ItemType.Bandage;
        return b;
    }

    /// <summary>START emotes and answers the difficulty: rand(80) to heal.</summary>
    [Fact]
    public void TheStartStageAppliesTheBandage()
    {
        var (world, healer, patient) = Setup();
        var sink = new Sink(healer, world) { PackBandage = Bandage(world) };

        Assert.True(ActiveSkillEngine.HasStartStage(SkillType.Veterinary));
        int diff = ActiveSkillEngine.StartStage(sink, SkillType.Healing, patient, null);

        Assert.InRange(diff, 0, 80);
        Assert.Equal([ServerMessages.GetFormatted(Msg.HealingTo, "Patient")], sink.Emotes);
        Assert.Empty(sink.Consumed);
    }

    /// <summary>A failed bandage is spent and the patient hears DEFMSG_HEALING_ATTEMPTF
    /// (CCharSkill.cpp:2814-2821).</summary>
    [Fact]
    public void AFailedBandageTellsThePatient()
    {
        var (world, healer, patient) = Setup();
        var bandage = Bandage(world);
        var sink = new Sink(healer, world) { PackBandage = bandage };
        var told = new List<(Character, string)>();
        Character.SendOwnerMessage = (c, m) => told.Add((c, m));
        Character.OnSkillUseQuick = (_, _, _, _) => 0;
        try
        {
            Assert.False(ActiveSkillEngine.Healing(sink, patient, SkillType.Healing, null, 30));
            Assert.Equal([bandage], sink.Consumed);
            Assert.Equal([(patient, ServerMessages.GetFormatted(Msg.HealingAttemptf, "Healer",
                ServerMessages.Get(Msg.HealingWho)))], told);
            Assert.Empty(sink.Emotes);   // START already emoted
        }
        finally
        {
            Character.SendOwnerMessage = null;
            Character.OnSkillUseQuick = null;
        }
    }

    /// <summary>ACTPRV is the bandage: one that is no bandage is DEFMSG_HEALING_WITEM,
    /// whatever the pack holds.</summary>
    [Fact]
    public void TheBandageIsActPrv()
    {
        var (world, healer, patient) = Setup();
        var cloth = world.CreateItem();
        cloth.ItemType = ItemType.Cloth;
        healer.ActPrv = cloth.Uid;
        var sink = new Sink(healer, world) { PackBandage = Bandage(world) };

        Assert.True(ActiveSkillEngine.HealingStart(sink, patient) < 0);
        Assert.Equal([ServerMessages.Get(Msg.HealingWitem)], sink.Messages);
    }

    /// <summary>A bandage on a tameable animal starts Veterinary, on a person Healing;
    /// on a thing nothing at all (it used to treat the healer).</summary>
    [Fact]
    public void ThePatientPicksTheSkill()
    {
        var (world, healer, patient) = Setup();
        healer.SetSkill(SkillType.Healing, 1000);
        healer.SetSkill(SkillType.Veterinary, 1000);
        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world, new AccountManager(loggerFactory), 3201);
        TestHarness.AttachCharacter(client, healer);
        client.SetEngines(skillHandlers: new SkillHandlers(world));
        var bandage = Bandage(world);
        healer.Backpack ??= world.CreateItem();
        bandage.Amount = 10;
        healer.Backpack.AddItem(bandage);

        var horse = world.CreateCharacter();
        horse.NpcBrain = NpcBrainType.Animal;
        horse.BodyId = 0x00C8;
        horse.SetSkill(SkillType.Taming, 290);
        horse.MaxHits = 50;
        horse.Hits = 10;
        world.PlaceCharacter(horse, new Point3D(100, 101, 0, 0));

        // With no skill definitions the DELAY is 0 and the roll follows at once.
        var rolled = new List<int>();
        Character.OnSkillUseQuickDetailed = (Character _, int skill, ref int _, int _) => { rolled.Add(skill); return 0; };
        try
        {
            client.ItemUse.BandageMacro(bandage, horse.Uid);
            Assert.Equal(bandage.Uid, healer.ActPrv);
            client.ItemUse.BandageMacro(bandage, patient.Uid);
            var rock = world.CreateItem();
            world.PlaceItem(rock, new Point3D(100, 101, 0, 0));
            client.ItemUse.BandageMacro(bandage, rock.Uid);
            Assert.Equal([(int)SkillType.Veterinary, (int)SkillType.Healing], rolled);
        }
        finally { Character.OnSkillUseQuickDetailed = null; }
    }
}
