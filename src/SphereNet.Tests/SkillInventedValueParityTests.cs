using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Messages;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Skill values and side effects that SphereNet had invented and Source-X does not
/// have: every expectation here is read off the cited reference line. The skill
/// roll is observed (and decided) through the @SkillUseQuick hook, so each test
/// sees the exact difficulty the engine asked for.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SkillInventedValueParityTests : IDisposable
{
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_skillparity_{Guid.NewGuid():N}.scp");

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private sealed class Sink : IActiveSkillSink
    {
        public Character Self { get; }
        public Random Random { get; }
        public GameWorld World { get; }
        public List<string> Messages { get; } = new();
        public List<ushort> Sounds { get; } = new();
        public List<Item> Consumed { get; } = new();
        public List<Item> Delivered { get; } = new();
        public Dictionary<ItemType, Item> Pack { get; } = new();

        public Sink(Character self, GameWorld world, int seed = 7)
        {
            Self = self; World = world; Random = new Random(seed);
        }

        public void SysMessage(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase target, string text) => Messages.Add(text);
        public void Emote(string text) => Messages.Add(text);
        public void Sound(ushort soundId) => Sounds.Add(soundId);
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => Pack.TryGetValue(type, out var i) ? i : null;
        public void ConsumeAmount(Item item, ushort amount = 1) => Consumed.Add(item);
        public void DeliverItem(Item item) => Delivered.Add(item);
    }

    /// <summary>Record every Skill_UseQuick (skill, difficulty) and answer it.</summary>
    private static List<(SkillType Skill, int Diff)> Rolls(Func<SkillType, int> outcome)
    {
        var rolls = new List<(SkillType, int)>();
        Character.OnSkillUseQuick = (_, skill, diff, _) =>
        {
            rolls.Add(((SkillType)skill, diff));
            return outcome((SkillType)skill);
        };
        return rolls;
    }

    private static Character Place(GameWorld world, short x = 100, short y = 100, bool player = true)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.BodyId = 0x0190;
        ch.Str = 100; ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    private static Item Pack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    // ------------------------------------------------------------- Begging

    [Fact]
    public void BeggingRollsTheTargetsIntAndGivesNothing()
    {
        // Skill_Begging START returns pChar->Stat_GetAdjusted(STAT_INT) and SUCCESS
        // returns 0 with nothing handed over (CCharSkill.cpp:2946/2958). Any
        // character but the beggar will do - here an animal.
        var world = TestHarness.CreateWorld();
        var beggar = Place(world);
        var dog = Place(world, 101, 100, player: false);
        dog.NpcBrain = NpcBrainType.Animal;
        dog.Int = 77;
        var rolls = Rolls(_ => 1);
        var sink = new Sink(beggar, world);

        Assert.True(ActiveSkillEngine.Begging(sink, dog));
        Assert.Equal((SkillType.Begging, 77), rolls.Single());
        Assert.Empty(sink.Delivered);

        rolls.Clear();
        Assert.False(ActiveSkillEngine.Begging(sink, beggar));
        Assert.Empty(rolls);
    }

    // ------------------------------------------------------------ Stealing

    [Fact]
    public void StealingDifficultyIsCalcStealingItem()
    {
        // CResourceCalc.cpp:431-446: (stealing/5 + rand(dex/2) + IMulDiv(weight,4,10)
        // [+ dex/2 + int when worn] [+ rand(dex/2) when the thief is at war]) / 2.
        var world = TestHarness.CreateWorld();
        var thief = Place(world);
        var mark = Place(world, 101, 100);
        mark.Dex = 1;                                   // rand(0) = 0
        mark.Int = 20;
        mark.SetSkill(SkillType.Stealing, 500);         // /5 = 100
        var item = world.CreateItem();
        item.TrySetProperty("BASEWEIGHT", "100");       // 10 stones = 100 tenths -> 40

        Assert.Equal((100 + 0 + 40) / 2,
            ActiveSkillEngine.CalcStealingItem(thief, item, mark, new Random(1)));

        mark.Equip(item, Layer.Ring);
        Assert.Equal((100 + 40 + 0 + 20) / 2,
            ActiveSkillEngine.CalcStealingItem(thief, item, mark, new Random(1)));
    }

    [Fact]
    public void AHeavyItemIsStolenOnTheRollNotRefusedBySkillOverTen()
    {
        // Only CanMoveItem / CanCarry gate the item (CCharSkill.cpp:4264); the old
        // skill/10 stone cap refused a 20-stone item to anyone under 200.0 skill.
        var world = TestHarness.CreateWorld();
        var thief = Place(world);
        Pack(world, thief);
        var mark = Place(world, 101, 100);
        var markPack = Pack(world, mark);
        var loot = world.CreateItem();
        loot.TrySetProperty("BASEWEIGHT", "200");
        markPack.AddItem(loot);
        var rolls = Rolls(_ => 0);
        var sink = new Sink(thief, world);

        ActiveSkillEngine.Stealing(sink, loot);

        Assert.DoesNotContain(ServerMessages.Get(Msg.StealingHeavy), sink.Messages);
        Assert.Contains(rolls, r => r.Skill == SkillType.Stealing);
    }

    // ------------------------------------------------------------ Snooping

    [Fact]
    public void SnoopingIsAllOrNothingAndCostsKarma()
    {
        // (Skill_GetAdjusted(SNOOPING) < rand(1000)) ? 100 : 0 (CCharSkill.cpp:4157)
        // and Noto_Karma(-4) win or lose (:4162), doubled for a good character by
        // Calc_KarmaScale (CResourceCalc.cpp:397).
        var world = TestHarness.CreateWorld();
        var snoop = Place(world);
        snoop.SetSkill(SkillType.Snooping, 1000);        // never below rand(1000)
        snoop.Karma = 100;
        var mark = Place(world, 101, 100);
        var markPack = Pack(world, mark);
        var rolls = Rolls(_ => 1);

        ActiveSkillEngine.Snooping(new Sink(snoop, world), markPack);

        Assert.Equal((SkillType.Snooping, 0), rolls.Single());
        Assert.Equal(92, snoop.Karma);
    }

    [Fact]
    public void SnoopingReachDefaultsToOneTile()
    {
        var world = TestHarness.CreateWorld();
        var snoop = Place(world);
        var mark = Place(world, 102, 100);
        var markPack = Pack(world, mark);
        var rolls = Rolls(_ => 1);
        var sink = new Sink(snoop, world);

        Assert.False(ActiveSkillEngine.Snooping(sink, markPack));
        Assert.Empty(rolls);
        Assert.Contains(ServerMessages.Get(Msg.SnoopingReach), sink.Messages);
    }

    [Theory]
    [InlineData(100, -4, 92)]       // good: losses count double
    [InlineData(-10, -4, -14)]
    [InlineData(-9998, -4, -10000)] // bounded by MINKARMA
    public void SkillKarmaFollowsNotoKarma(short karma, int change, short expected)
    {
        var ch = new Character { Karma = karma };
        ActiveSkillEngine.ApplySkillKarma(ch, change);
        Assert.Equal(expected, ch.Karma);
    }

    [Fact]
    public void SnoopingKarmaLossSaysSo()
    {
        // Noto_Karma(-4, INT32_MIN, true) -> Noto_ChangeDeltaMsg: "You have lost a
        // bit of karma." for the -8 a good character takes (CCharNotoriety.cpp:547,
        // 455: 8 / (300/8) = degree 0).
        var world = TestHarness.CreateWorld();
        var snoop = Place(world);
        snoop.Karma = 100;
        var mark = Place(world, 101, 100);
        var markPack = Pack(world, mark);
        Rolls(_ => 1);
        var sink = new Sink(snoop, world);

        ActiveSkillEngine.Snooping(sink, markPack);

        Assert.Equal(92, snoop.Karma);
        Assert.Contains(ServerMessages.GetFormatted(Msg.MsgNotoChange0,
            ServerMessages.Get(Msg.MsgNotoChangeLost), ServerMessages.Get(Msg.MsgNotoChange1),
            ServerMessages.Get(Msg.NotoKarma)), sink.Messages);
    }

    [Theory]
    [InlineData(-8, "msg_noto_change_1")]
    [InlineData(-37, "msg_noto_change_2")]
    [InlineData(-200, "msg_noto_change_6")]
    [InlineData(5000, "msg_noto_change_8")]
    public void NotoChangeDegreeIsDeltaOverThirtySeven(int delta, string degreeKey)
    {
        string expected = ServerMessages.GetFormatted(Msg.MsgNotoChange0,
            ServerMessages.Get(delta < 0 ? Msg.MsgNotoChangeLost : Msg.MsgNotoChangeGain),
            ServerMessages.Get(degreeKey), "karma");
        Assert.Equal(expected, ActiveSkillEngine.NotoChangeDeltaMessage(delta, "karma"));
        Assert.Null(ActiveSkillEngine.NotoChangeDeltaMessage(0, "karma"));
    }

    [Theory]
    [InlineData(-500, -600)]
    [InlineData(-950, -1000)]   // stopped at the -1000 floor
    [InlineData(-5000, -1000)]  // upstream sets the change to reach the floor, literally (:526-529)
    [InlineData(100, -100)]     // a good character loses double
    public void StealingKarmaFloorIsMinusOneThousand(short karma, short expected)
    {
        // Noto_Karma(-100, -1000, true) (CCharSkill.cpp:4343) - scaled by
        // Calc_KarmaScale only for a positive karma.
        var ch = new Character { Karma = karma };
        ActiveSkillEngine.ApplySkillKarma(ch, -100, -1000);
        Assert.Equal(expected, ch.Karma);
    }

    [Fact]
    public void AGroundItemIsTakenEvenWhenTheStealingRollFails()
    {
        // Skill_Stealing: "stealing off the ground should always succeed, it's just a
        // matter of getting caught" - with no mark the goods are delivered on FAIL as
        // well, and lose ATTR_OWNED (CCharSkill.cpp:4311-4331).
        var world = TestHarness.CreateWorld();
        var thief = Place(world);
        var pack = Pack(world, thief);
        thief.Backpack = pack;
        var loot = world.CreateItem();
        loot.SetAttr(ObjAttributes.Owned);
        world.PlaceItem(loot, new Point3D(101, 100, 0, 0));
        var rolls = Rolls(_ => 0);
        var sink = new Sink(thief, world);

        Assert.False(ActiveSkillEngine.Stealing(sink, loot));

        Assert.Equal((SkillType.Stealing, 1), rolls.Single());
        Assert.Equal(pack.Uid, loot.ContainedIn);
        Assert.False(loot.IsAttr(ObjAttributes.Owned));
    }

    [Fact]
    public void AFailedTheftFromAMarkLeavesTheItemWithTheMark()
    {
        var world = TestHarness.CreateWorld();
        var thief = Place(world);
        thief.Backpack = Pack(world, thief);
        var mark = Place(world, 101, 100);
        var markPack = Pack(world, mark);
        var loot = world.CreateItem();
        markPack.AddItem(loot);
        Rolls(_ => 0);

        Assert.False(ActiveSkillEngine.Stealing(new Sink(thief, world), loot));
        Assert.Equal(markPack.Uid, loot.ContainedIn);
    }

    // --------------------------------------------------------- Lockpicking

    [Fact]
    public void AFailedPickFiresItsDamageTrigger()
    {
        // FAIL: pPick->OnTakeDamage(1, this, DAMAGE_HIT_BLUNT) (CCharSkill.cpp:2447);
        // for an IT_LOCKPICK that is only its @Damage (CItem.cpp:5826-5832).
        var world = TestHarness.CreateWorld();
        var picker = Place(world);
        var chest = world.CreateItem();
        chest.ItemType = ItemType.ContainerLocked;
        chest.More2 = 500;
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));
        var pick = world.CreateItem();
        pick.ItemType = ItemType.Lockpick;
        var damaged = new List<(Item Item, int Dmg, Character? Src, SphereNet.Game.Combat.DamageType Type)>();
        SphereNet.Game.Combat.CombatEngine.OnItemDamaged = (item, dmg, src, type) =>
        {
            damaged.Add((item, dmg, src, type));
            return false;
        };
        var sink = new Sink(picker, world);
        sink.Pack[ItemType.Lockpick] = pick;

        Rolls(_ => 0);
        Assert.False(ActiveSkillEngine.Lockpicking(sink, chest));
        Assert.Equal((pick, 1, (Character?)picker, SphereNet.Game.Combat.DamageType.HitBlunt), damaged.Single());
        Assert.False(pick.IsDeleted);

        damaged.Clear();
        Rolls(_ => 1);
        Assert.True(ActiveSkillEngine.Lockpicking(sink, chest));
        Assert.Empty(damaged);
    }

    [Fact]
    public void LockComplexityIsMore2AndAFailedPickIsNotSpent()
    {
        // m_dwLockComplexity is MORE2 (CItem.h:182) and Use_LockPick returns it /10
        // (CItem.cpp:5449); a failed pick only takes OnTakeDamage(1), which spends
        // nothing (CCharSkill.cpp:2444-2448).
        var world = TestHarness.CreateWorld();
        var picker = Place(world);
        var chest = world.CreateItem();
        chest.ItemType = ItemType.ContainerLocked;
        chest.More1 = 0x40001234;                        // the lock code
        chest.More2 = 500;                               // complexity -> 50
        world.PlaceItem(chest, new Point3D(101, 100, 0, 0));
        var pick = world.CreateItem();
        pick.ItemType = ItemType.Lockpick;
        var rolls = Rolls(_ => 0);

        for (int seed = 0; seed < 12; seed++)
        {
            var sink = new Sink(picker, world, seed);
            sink.Pack[ItemType.Lockpick] = pick;
            Assert.False(ActiveSkillEngine.Lockpicking(sink, chest));
            Assert.Empty(sink.Consumed);
        }
        Assert.All(rolls, r => Assert.Equal((SkillType.Lockpicking, 50), r));
    }

    // ---------------------------------------------------------- RemoveTrap

    [Fact]
    public void RemoveTrapWorksOnlyAnIdleTrapAndDisablesItForFiveMinutes()
    {
        // IsType(IT_TRAP) only (CCharSkill.cpp:2902); SUCCESS is
        // SetTrapState(IT_TRAP_INACTIVE, ITEMID_NOTHING, 5*60) (:2921).
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var trap = world.CreateItem();
        trap.BaseId = 0x1125;
        trap.ItemType = ItemType.TrapActive;
        world.PlaceItem(trap, new Point3D(101, 100, 0, 0));
        var rolls = Rolls(_ => 1);
        var sink = new Sink(ch, world);

        Assert.False(ActiveSkillEngine.RemoveTrap(sink, trap));
        Assert.Empty(rolls);
        Assert.Contains(ServerMessages.Get(Msg.RemovetrapsWitem), sink.Messages);

        trap.ItemType = ItemType.Trap;
        Assert.True(ActiveSkillEngine.RemoveTrap(sink, trap));
        Assert.Equal(ItemType.TrapInactive, trap.ItemType);
    }

    [Fact]
    public void ABotchedDisarmSetsTheTrapOff()
    {
        // FAIL: Use_Item(pTrap) (CCharSkill.cpp:2917) -> Use_Trap arms it and deals
        // its own damage, default 2 (CItem.cpp:5502-5508).
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var trap = world.CreateItem();
        trap.BaseId = 0x1125;
        trap.ItemType = ItemType.Trap;
        world.PlaceItem(trap, new Point3D(101, 100, 0, 0));
        Rolls(_ => 0);

        Assert.False(ActiveSkillEngine.RemoveTrap(new Sink(ch, world), trap));
        Assert.Equal(ItemType.TrapActive, trap.ItemType);
        Assert.True(ch.Hits < 100);
    }

    // ------------------------------------------------------ Hiding/Stealth

    [Fact]
    public void StealthHasNoEngineStage()
    {
        // SKILL_STEALTH: Skill_Stage returns 0 at every stage (CCharSkill.cpp:3674);
        // the pack's [SKILL 47] does the rest.
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var rolls = Rolls(_ => 1);
        var sink = new Sink(ch, world);

        Assert.True(ActiveSkillEngine.Stealth(sink));
        Assert.Equal((SkillType.Stealth, 0), rolls.Single());
        Assert.Equal(0, ch.StepStealth);
        Assert.Empty(sink.Messages);
    }

    [Fact]
    public void HidingAndStealthDoNotRefuseWarMode()
    {
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        ch.SetStatFlag(StatFlag.War);
        var rolls = Rolls(_ => 1);

        Assert.True(ActiveSkillEngine.Hiding(new Sink(ch, world)));
        Assert.True(ActiveSkillEngine.Stealth(new Sink(ch, world)));
        Assert.Equal(2, rolls.Count);
    }

    // ----------------------------------------------------------- Poisoning

    [Fact]
    public void PoisoningCoatsFromMore2WithRand60AndPowderSound()
    {
        // START rand(60) (CCharSkill.cpp:2176), Sound(0x247) (:2193), MOREZ =
        // m_dwSkillQuality/10 (:2207). An axe is not on the list (:2204-2209).
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var pack = Pack(world, ch);
        var potion = world.CreateItem();
        potion.ItemType = ItemType.Potion;
        potion.SetTag("POTION_SPELL", "Poison");
        potion.More2 = 600;
        potion.Quality = 10;
        pack.AddItem(potion);
        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        pack.AddItem(sword);
        var axe = world.CreateItem();
        axe.ItemType = ItemType.WeaponAxe;
        pack.AddItem(axe);
        var rolls = Rolls(_ => 1);

        var sink = new Sink(ch, world);
        Assert.False(ActiveSkillEngine.Poisoning(sink, axe, potion));
        Assert.Empty(rolls);

        Assert.True(ActiveSkillEngine.Poisoning(sink, sword, potion));
        Assert.InRange(rolls.Single().Diff, 0, 59);
        Assert.Contains((ushort)0x247, sink.Sounds);
        Assert.Equal(60, sword.MoreP.Z);
    }

    // ------------------------------------------------------------- Healing

    [Fact]
    public void HealingHealsTheEffectAmountAndBloodiesOnlyOnSuccess()
    {
        // uHealValue = m_Act_Effect, 1 when unset (CCharSkill.cpp:2881-2884); the
        // bloody bandage is made in the SUCCESS stage only (:2846).
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        ch.Hits = 10;
        var bandage = world.CreateItem();
        bandage.ItemType = ItemType.Bandage;

        Rolls(_ => 0);
        var failSink = new Sink(ch, world);
        failSink.Pack[ItemType.Bandage] = bandage;
        Assert.False(ActiveSkillEngine.Healing(failSink, ch));
        Assert.Single(failSink.Consumed);
        Assert.Empty(failSink.Delivered);
        Assert.Equal(10, ch.Hits);

        Rolls(_ => 1);
        var okSink = new Sink(ch, world);
        okSink.Pack[ItemType.Bandage] = bandage;
        Assert.True(ActiveSkillEngine.Healing(okSink, ch));
        Assert.Equal(11, ch.Hits);
        var bloody = Assert.Single(okSink.Delivered);
        Assert.Contains(bloody.BaseId, new ushort[] { 0x0E20, 0x0E22 });

        ch.ActionEffect = 7;
        var effSink = new Sink(ch, world);
        effSink.Pack[ItemType.Bandage] = bandage;
        Assert.True(ActiveSkillEngine.Healing(effSink, ch));
        Assert.Equal(18, ch.Hits);
    }

    [Fact]
    public void VeterinaryHasNoSpeciesGate()
    {
        // SKILL_VETERINARY and SKILL_HEALING share Skill_Healing (CCharSkill.cpp:3718).
        var world = TestHarness.CreateWorld();
        var vet = Place(world);
        var human = Place(world, 101, 100, player: false);
        human.NpcBrain = NpcBrainType.Human;
        human.Hits = 10;
        var bandage = world.CreateItem();
        bandage.ItemType = ItemType.Bandage;
        Rolls(_ => 1);
        var sink = new Sink(vet, world);
        sink.Pack[ItemType.Bandage] = bandage;

        Assert.True(ActiveSkillEngine.Healing(sink, human, SkillType.Veterinary));
        Assert.Equal(11, human.Hits);
    }

    // ---------------------------------------------------------------- Bard

    [Fact]
    public void MusicianshipPlaysAgainstRand90()
    {
        // Skill_Musicianship START: Use_PlayMusic(..., rand(90)) (CCharSkill.cpp:1787).
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        lute.UsesRemaining = 1;
        var rolls = Rolls(_ => 1);
        var sink = new Sink(ch, world);
        sink.Pack[ItemType.Musical] = lute;

        Assert.True(ActiveSkillEngine.Musicianship(sink));
        Assert.All(rolls, r => Assert.InRange(r.Diff, 0, 89));
        Assert.Equal(2, rolls.Count);           // Use_PlayMusic, then the skill itself
        // Instruments take no wear (only a SKF_GATHER success damages a tool).
        Assert.Empty(sink.Consumed);
        Assert.Equal(1, lute.UsesRemaining);
    }

    [Fact]
    public void PeacemakingSettlesTheFirstCreatureInEarshot()
    {
        // SUCCESS: radius Peacemaking/100 + 2; a creature out-peacing the bard
        // ignores the song, anything else stops fighting (CCharSkill.cpp:1850-1893).
        var world = TestHarness.CreateWorld();
        var bard = Place(world);
        bard.SetSkill(SkillType.Peacemaking, 500);       // 7 tiles
        var brute = Place(world, 103, 100, player: false);
        var foe = Place(world, 150, 100, player: false);   // out of earshot
        brute.SetStatFlag(StatFlag.War);
        brute.FightTarget = foe.Uid;
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        Rolls(_ => 1);
        var sink = new Sink(bard, world);
        sink.Pack[ItemType.Musical] = lute;

        Assert.True(ActiveSkillEngine.Peacemaking(sink));
        Assert.False(brute.IsStatFlag(StatFlag.War));
        Assert.False(brute.FightTarget.IsValid);

        brute.SetStatFlag(StatFlag.War);
        brute.SetSkill(SkillType.Peacemaking, 900);
        var sink2 = new Sink(bard, world);
        sink2.Pack[ItemType.Musical] = lute;
        Assert.True(ActiveSkillEngine.Peacemaking(sink2));
        Assert.True(brute.IsStatFlag(StatFlag.War));
        Assert.Contains(sink2.Messages, m => m.Contains(ServerMessages.Get(Msg.PeacemakingIgnore)));
    }

    [Fact]
    public void PeacemakingWithNobodyInRangeFails()
    {
        var world = TestHarness.CreateWorld();
        var bard = Place(world);                         // skill 0 -> radius 2
        Place(world, 110, 100, player: false);
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        Rolls(_ => 1);
        var sink = new Sink(bard, world);
        sink.Pack[ItemType.Musical] = lute;

        Assert.False(ActiveSkillEngine.Peacemaking(sink));
    }

    [Fact]
    public void ProvocationRefusesPetsAndPlayers()
    {
        // DEFMSG_PROVOCATION_UPSET for a pet/conjured/dead/invulnerable party,
        // DEFMSG_PROVOCATION_PLAYER for a player (CCharSkill.cpp:2013-2025).
        var world = TestHarness.CreateWorld();
        var bard = Place(world);
        var a = Place(world, 101, 100, player: false);
        var b = Place(world, 102, 100, player: false);
        var rolls = Rolls(_ => 1);

        a.SetStatFlag(StatFlag.Pet);
        var sink = new Sink(bard, world);
        Assert.False(ActiveSkillEngine.Provocation(sink, a, b));
        Assert.Contains(ServerMessages.Get(Msg.ProvocationUpset), sink.Messages);

        a.ClearStatFlag(StatFlag.Pet);
        b.IsPlayer = true;
        var sink2 = new Sink(bard, world);
        Assert.False(ActiveSkillEngine.Provocation(sink2, a, b));
        Assert.Contains(ServerMessages.Get(Msg.ProvocationPlayer), sink2.Messages);
        Assert.Empty(rolls);
    }

    [Fact]
    public void AFailedProvocationTurnsTheCreatureOnTheBard()
    {
        // FAIL: pCharProv->Fight_Attack(this) (CCharSkill.cpp:2092-2096).
        var world = TestHarness.CreateWorld();
        var bard = Place(world);
        var a = Place(world, 101, 100, player: false);
        var b = Place(world, 102, 100, player: false);
        a.BodyId = 0x0001; b.BodyId = 0x00C9;
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        Rolls(s => s == SkillType.Provocation ? 0 : 1);
        var sink = new Sink(bard, world);
        sink.Pack[ItemType.Musical] = lute;

        Assert.False(ActiveSkillEngine.Provocation(sink, a, b));
        Assert.Equal(bard.Uid, a.FightTarget);
    }

    [Fact]
    public void ProvokingAGoodCreatureIsACrimeReadOffTheRealNotoriety()
    {
        // SUCCESS: pCharProv->Noto_GetFlag(this) == NOTO_GOOD ->
        // CheckCrimeSeen(SKILL_PROVOCATION, nullptr, pCharProv) and the provocation
        // is refused (CCharSkill.cpp:2135-2139). The notoriety is the engine's own -
        // no host resolver is wired here - and a witness who sees it remembers the
        // crime (OnNoticeCrime -> MEMORY_SAWCRIME).
        Assert.Null(Character.ResolveNotoFlag);
        var world = TestHarness.CreateWorld();
        var bard = Place(world);
        var good = Place(world, 101, 100, player: false);   // human NPC, karma 0: NOTO_GOOD
        good.NpcBrain = NpcBrainType.Human;
        good.BodyId = 0x0190;
        var evil = Place(world, 102, 100, player: false);
        evil.NpcBrain = NpcBrainType.Monster;
        evil.BodyId = 0x0001;
        evil.Karma = -100;
        var witness = Place(world, 100, 101, player: false);
        witness.NpcBrain = NpcBrainType.Human;
        witness.Dex = 100; witness.Int = 100;               // 400 + 200*50: always sees
        var lute = world.CreateItem();
        lute.ItemType = ItemType.Musical;
        Rolls(_ => 1);
        var sink = new Sink(bard, world);
        sink.Pack[ItemType.Musical] = lute;

        Assert.False(ActiveSkillEngine.Provocation(sink, good, evil));

        Assert.NotNull(witness.Memory_FindObjTypes(bard.Uid, MemoryType.SawCrime));
        Assert.NotEqual(evil.Uid, good.FightTarget);
    }

    // ----------------------------------------------------------- Skill gain

    [Fact]
    public void DecayTakesTheHighestDownLockedSkillTieToTheLaterIndex()
    {
        // Skill_Decay skips a candidate only while the pick is HIGHER than it
        // (CCharSkill.cpp:334), so the highest wins and a tie goes to the later index.
        var ch = new Character();
        ch.SetSkill(SkillType.Anatomy, 300); ch.SetSkillLock(SkillType.Anatomy, 1);
        ch.SetSkill(SkillType.Begging, 500); ch.SetSkillLock(SkillType.Begging, 1);
        ch.SetSkill(SkillType.Fishing, 500); ch.SetSkillLock(SkillType.Fishing, 1);
        ch.SetSkill(SkillType.Magery, 900);  // UP: never decays

        SkillEngine.TrySkillDecay(ch);

        Assert.Equal(300, ch.GetSkill(SkillType.Anatomy));
        Assert.Equal(500, ch.GetSkill(SkillType.Begging));
        Assert.Equal(499, ch.GetSkill(SkillType.Fishing));
        Assert.Equal(900, ch.GetSkill(SkillType.Magery));
    }

    [Fact]
    public void TheSkillSumCapBindsPlayersOnly()
    {
        // if ( IsPlayer() ) { if (Skill_GetSum() >= Skill_GetSumMax()) iDifficulty = 0; }
        // (CCharSkill.cpp:380-386)
        TestHarness.SeedSkillAdvRates();
        SkillEngine.SkillSumMaxOverride = 100;
        var npc = new Character { IsPlayer = false };
        npc.SetSkill(SkillType.Tactics, 100);
        var player = new Character { IsPlayer = true };
        player.SetSkill(SkillType.Tactics, 100);

        for (int i = 0; i < 4000 && npc.GetSkill(SkillType.Tactics) == 100; i++)
            SkillEngine.GainExperience(npc, SkillType.Tactics, 50);
        for (int i = 0; i < 4000; i++)
            SkillEngine.GainExperience(player, SkillType.Tactics, 50);

        Assert.True(npc.GetSkill(SkillType.Tactics) > 100);
        Assert.Equal(100, player.GetSkill(SkillType.Tactics));
    }

    // ---------------------------------------------------------- Animal lore

    [Theory]
    [InlineData(7, 1, 2, 4)]     // rounds, where a plain division gave 3
    [InlineData(-7, 1, 2, -4)]
    [InlineData(20, 8, 40, 4)]
    public void IMulDivRoundsLikeTheReference(int a, int b, int c, int expected)
        => Assert.Equal(expected, InfoSkillEngine.IMulDiv(a, b, c));

    [Fact]
    public void FoodBandsAreMeasuredAgainstTheCreaturesOwnMaxFood()
    {
        // Food_GetLevelMessage: IMulDiv(food, 8, Stat_GetMaxAdjusted(STAT_FOOD))
        // (CCharStatus.cpp:837); no food stat reads "unaffected" (:834).
        var ch = new Character();
        ch.SetTag("MAXFOOD", "40");
        ch.Food = 20;
        Assert.Equal(ServerMessages.Get(Msg.MsgFoodLvl5),
            InfoSkillEngine.GetFoodLevelMessage(ch, ownerOwned: false));
    }

    // ------------------------------------------------------------ Gathering

    private (GameWorld World, GatheringEngine Engine, Character Miner, ResourceHolder Res) GatherRig()
    {
        var lf = LoggerFactory.Create(_ => { });
        File.WriteAllText(_defFile, """
            [ITEMDEF 019b9]
            DEFNAME=i_ore_parity
            NAME=Parity Ore
            TYPE=t_ore

            [REGIONRESOURCE mr_parity_ore]
            DEFNAME=mr_parity_ore
            REAP=i_ore_parity
            AMOUNT=1000
            REAPAMOUNT=1
            SKILL=0.0
            REGEN=600

            [REGIONTYPE r_parity_rock t_rock]
            DEFNAME=r_parity_rock
            RESOURCES=100.0 mr_parity_ore
            """);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
        var world = TestHarness.CreateWorld();
        var miner = world.CreateCharacter();
        Character.OnSkillUseQuickDetailed = (Character _, int _, ref int _, int _) => 1;
        return (world, new GatheringEngine(world), miner, resources);
    }

    [Fact]
    public void GatheringUsesNoTableFromElsewhereInTheWorld()
    {
        // CheckNaturalResource reads only the AREA at the tile (or the background
        // region when that area has no events) (CWorldMap.cpp:83-111): a table
        // defined somewhere is not a table here.
        var rig = GatherRig();
        var tile = new Point3D(100, 100, 0, 0);

        Assert.False(rig.Engine.TryGatherForSink(rig.Miner, SkillType.Mining, tile).Handled);

        var eventful = new Region { Name = "eventful", MapIndex = 0 };
        eventful.AddRect(90, 90, 110, 110);
        eventful.AddEvent(new ResourceId(ResType.Events, 1234));
        rig.World.AddRegion(eventful);
        Assert.False(rig.Engine.TryGatherForSink(rig.Miner, SkillType.Mining, tile).Handled);
    }

    [Fact]
    public void AnAreaWithoutEventsFallsBackToTheBackgroundRegion()
    {
        var rig = GatherRig();
        var tile = new Point3D(100, 100, 0, 0);
        var background = new Region { Name = "background", MapIndex = 0 };
        background.AddRect(0, 0, 10, 10);
        background.AddRegionType(rig.Res.ResolveDefName("r_parity_rock"));
        rig.World.AddRegion(background);
        var bare = new Region { Name = "bare", MapIndex = 0 };
        bare.AddRect(90, 90, 110, 110);
        rig.World.AddRegion(bare);

        Assert.True(rig.Engine.TryGatherForSink(rig.Miner, SkillType.Mining, tile).Handled);
    }

    [Fact]
    public void AGatheringToolWearsOnlyOnSuccessAndOnlyWithDamageTools()
    {
        // Skill_Stage SUCCESS: with EF_DamageTools, on a SKF_GATHER skill, the weapon
        // in hand takes 1 at a 25% chance (CCharSkill.cpp:3931-3961). Nothing else.
        var rig = GatherRig();
        TestHarness.AttachLoadedRegionTypes(rig.World);
        var mining = new SphereNet.Scripting.Definitions.SkillDef(ResourceId.Invalid);
        mining.LoadFromKey("FLAGS", "skf_gather");
        DefinitionLoader.SetSkillDef((int)SkillType.Mining, mining);
        Assert.True(SkillEngine.HasFlag(SkillType.Mining, SkillFlag.Gather));
        var miner = Place(rig.World);
        var pick = rig.World.CreateItem();
        pick.ItemType = ItemType.WeaponMacePick;
        pick.HitsCur = pick.HitsMax = 50;
        miner.Equip(pick, Layer.OneHanded);
        var rock = new Point3D(101, 100, 0, 0);

        // The wear is not part of the SUCCESS stage itself: Skill_Done runs it after
        // @SkillSuccess / @Success (CCharSkill.cpp:3945-3961), so the skill driver
        // calls DamageGatherToolOnSuccess once the triggers are done. Mining alone
        // (what this test used to count on) never wears the tool.
        ActiveSkillEngine.DamageToolsEnabled = true;
        for (int i = 0; i < 20; i++)
        {
            var s = new Sink(miner, rig.World, i);
            Assert.True(ActiveSkillEngine.Mining(s, rock, rig.Engine, rig.World), string.Join("|", s.Messages));
        }
        Assert.Equal(50, pick.HitsCur);

        ActiveSkillEngine.DamageToolsEnabled = false;
        for (int i = 0; i < 20; i++)
            ActiveSkillEngine.DamageGatherToolOnSuccess(miner, SkillType.Mining,
                ActiveSkillEngine.NewSkillSuccessLocals(), new Random(i));
        Assert.Equal(50, pick.HitsCur);                // EF_DamageTools off

        ActiveSkillEngine.DamageToolsEnabled = true;
        for (int i = 0; i < 20; i++)
            ActiveSkillEngine.DamageGatherToolOnSuccess(miner, SkillType.Mining,
                ActiveSkillEngine.NewSkillSuccessLocals(), new Random(i));
        Assert.InRange(pick.HitsCur, 30, 49);          // some swings, never more than 1 each
    }

    [Fact]
    public void GatherToolWearReadsTheSuccessLocals()
    {
        // Skill_Done seeds LOCAL.ITEMDAMAGECHANCE=25 / ITEMDAMAGEAMOUNT=1 for
        // @SkillSuccess / @Success and reads them back for the wear: the chance
        // clamped 0-100, the amount bounded to the tool's hit points
        // (CCharSkill.cpp:3930-3961).
        var world = TestHarness.CreateWorld();
        var mining = new SphereNet.Scripting.Definitions.SkillDef(ResourceId.Invalid);
        mining.LoadFromKey("FLAGS", "skf_gather");
        DefinitionLoader.SetSkillDef((int)SkillType.Mining, mining);
        ActiveSkillEngine.DamageToolsEnabled = true;
        var miner = Place(world);
        var pick = world.CreateItem();
        pick.ItemType = ItemType.WeaponMacePick;
        pick.HitsCur = pick.HitsMax = 50;
        miner.Equip(pick, Layer.OneHanded);

        var seeded = ActiveSkillEngine.NewSkillSuccessLocals();
        Assert.Equal(25, seeded.GetInt("ITEMDAMAGECHANCE"));
        Assert.Equal(1, seeded.GetInt("ITEMDAMAGEAMOUNT"));

        var never = ActiveSkillEngine.NewSkillSuccessLocals();
        never.SetInt("ITEMDAMAGECHANCE", 0);
        for (int i = 0; i < 20; i++)
            ActiveSkillEngine.DamageGatherToolOnSuccess(miner, SkillType.Mining, never, new Random(i));
        Assert.Equal(50, pick.HitsCur);

        var always = ActiveSkillEngine.NewSkillSuccessLocals();
        always.SetInt("ITEMDAMAGECHANCE", 500);        // clamped to 100
        always.SetInt("ITEMDAMAGEAMOUNT", 7);
        ActiveSkillEngine.DamageGatherToolOnSuccess(miner, SkillType.Mining, always, new Random(1));
        // The amount is handed to CItem::OnTakeDamage, which takes ONE hit point from
        // a weapon per call whatever the amount (--m_wHitsCur, CItem.cpp:5931); the
        // amount only has to be above 0 (:5805).
        Assert.Equal(49, pick.HitsCur);

        var none = ActiveSkillEngine.NewSkillSuccessLocals();
        none.SetInt("ITEMDAMAGECHANCE", 100);
        none.SetInt("ITEMDAMAGEAMOUNT", 0);
        ActiveSkillEngine.DamageGatherToolOnSuccess(miner, SkillType.Mining, none, new Random(1));
        Assert.Equal(49, pick.HitsCur);
    }

    // -------------------------------------------------------------- Camping

    [Fact]
    public void KindlingOnTheGroundBecomesTheCampfire()
    {
        // Use_Kindling (CCharUse.cpp:272-296): top-level only, rand(30), and the
        // pile itself turns into a MOVE_NEVER campfire of amount 1 burning
        // (4 + amount) minutes.
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var kindling = world.CreateItem();
        kindling.ItemType = ItemType.Kindling;
        kindling.BaseId = 0x0DE1;
        kindling.Amount = 3;
        world.PlaceItem(kindling, new Point3D(101, 100, 0, 0));
        var rolls = Rolls(_ => 1);
        ch.Act = kindling.Uid;

        Assert.True(new SkillHandlers(world).UseSkill(ch, SkillType.Camping));

        Assert.InRange(rolls.Single().Diff, 0, 29);
        Assert.Equal(0x0DE3, kindling.BaseId);
        Assert.Equal(1, kindling.MoreP.Z);             // m_itLight.m_pattern = LIGHT_LARGE (:291)
        Assert.Equal(1, kindling.Amount);
        Assert.True(kindling.IsAttr(ObjAttributes.Move_Never));
        Assert.False(kindling.IsDeleted);
        long burn = kindling.DecayTime - Environment.TickCount64;
        Assert.InRange(burn, 6 * 60_000L, 7 * 60_000L);
    }

    [Fact]
    public void KindlingInAPackOrAMissedRollIsLeftAlone()
    {
        var world = TestHarness.CreateWorld();
        var ch = Place(world);
        var pack = Pack(world, ch);
        var kindling = world.CreateItem();
        kindling.ItemType = ItemType.Kindling;
        kindling.BaseId = 0x0DE1;
        kindling.Amount = 2;
        pack.AddItem(kindling);
        var rolls = Rolls(_ => 0);
        ch.Act = kindling.Uid;
        var handlers = new SkillHandlers(world);

        Assert.False(handlers.UseSkill(ch, SkillType.Camping));
        Assert.Empty(rolls);

        pack.RemoveItem(kindling);
        world.PlaceItem(kindling, new Point3D(101, 100, 0, 0));
        Assert.False(handlers.UseSkill(ch, SkillType.Camping));
        Assert.Single(rolls);
        Assert.Equal(0x0DE1, kindling.BaseId);
        Assert.Equal(2, kindling.Amount);
        Assert.False(kindling.IsDeleted);
    }
}
