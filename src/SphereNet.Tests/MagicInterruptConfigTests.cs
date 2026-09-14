using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.Skills.Information;
using SphereNet.Game.World;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Magic, and what interrupts it — PLAN-302's second packet (port plan İŞ-70).
///
/// Same acceptance criterion as the first packet: each key is asserted by MOVING it and
/// watching the behaviour. Two of the four turned out to be doing something already, and
/// doing it differently from the reference.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MagicInterruptConfigTests
{
    private readonly ITestOutputHelper _out;
    public MagicInterruptConfigTests(ITestOutputHelper output) => _out = output;

    private sealed class Sink : IActiveSkillSink
    {
        public Character Self { get; }
        public Random Random { get; set; }
        public GameWorld World { get; }
        public List<string> Messages { get; } = [];
        public Dictionary<ItemType, Item> Pack { get; } = [];

        public Sink(Character self, GameWorld world, int seed = 1)
        { Self = self; World = world; Random = new Random(seed); }

        public void SysMessage(string text) => Messages.Add(text);
        public void Emote(string text) => Messages.Add(text);
        public void ObjectMessage(ObjBase obj, string text) => Messages.Add(text);
        public void Sound(ushort soundId) { }
        public void Animation(ushort animId) { }
        public Item? FindBackpackItem(ItemType type) => Pack.GetValueOrDefault(type);
        public void ConsumeAmount(Item item, ushort amount = 1) { }
        public void DeliverItem(Item item) { }
    }

    private static (GameWorld World, Character Ch) Stage()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BaseId = 0x0190;
        ch.Str = 60; ch.Dex = 60; ch.Int = 60;
        ch.MaxHits = 100; ch.Hits = 100;
        ch.MaxStam = 100; ch.Stam = 100;
        ch.SetSkill(SkillType.Lockpicking, 1000);
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return (world, ch);
    }

    private static Item Lock(GameWorld world, Character near, ItemType type, uint complexity = 0)
    {
        var it = world.CreateItem();
        it.BaseId = 0x0E3C;
        it.ItemType = type;
        it.More1 = complexity;
        world.PlaceItem(it, near.Position);
        return it;
    }

    private static Sink WithPick(GameWorld world, Character ch)
    {
        var sink = new Sink(ch, world);
        var pick = world.CreateItem();
        pick.ItemType = ItemType.Lockpick;
        ch.Backpack!.AddItem(pick);
        sink.Pack[ItemType.Lockpick] = pick;
        return sink;
    }

    // ---- MEDITATIONMOVEMENTABORT ----------------------------------------

    [Fact]
    public void ByDefaultAMeditatingCharacterMayWalk()
    {
        var (world, ch) = Stage();
        var engine = new MovementEngine(world);
        ch.SetStatFlag(StatFlag.Meditation);
        Assert.True(ch.IsStatFlag(StatFlag.Meditation));

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        _out.WriteLine($"after a step, meditating={ch.IsStatFlag(StatFlag.Meditation)}");

        // The default is the surprising half. Upstream only fails the skill when
        // MEDITATIONMOVEMENTABORT is set (CCharAct.cpp:2495); this engine cancelled it
        // on every step, which is the stricter rule and not the reference one.
        Assert.False(MovementEngine.MeditationMovementAbort);
        Assert.True(ch.IsStatFlag(StatFlag.Meditation));
    }

    [Fact]
    public void TurningTheKeyOnMakesAStepCancelIt()
    {
        var (world, ch) = Stage();
        var engine = new MovementEngine(world);
        MovementEngine.MeditationMovementAbort = true;
        ch.SetStatFlag(StatFlag.Meditation);

        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        _out.WriteLine($"with the key on, meditating={ch.IsStatFlag(StatFlag.Meditation)}");

        Assert.False(ch.IsStatFlag(StatFlag.Meditation));
    }

    // ---- MAGICUNLOCKDOOR -------------------------------------------------

    [Fact]
    public void ZeroMeansAMagicDoorWantsItsKey()
    {
        var (world, ch) = Stage();
        var sink = WithPick(world, ch);
        var door = Lock(world, ch, ItemType.DoorLocked);
        Item.MagicUnlockDoor = 0;

        Assert.False(ActiveSkillEngine.Lockpicking(sink, door));
        _out.WriteLine(string.Join(" | ", sink.Messages));

        // Not "you failed" - a different message, because it is a different answer:
        // no amount of skill opens this door (CItem.cpp:5418).
        Assert.Equal(ItemType.DoorLocked, door.ItemType);
        Assert.Contains(sink.Messages, m => m.Length > 0);
    }

    [Fact]
    public void MinusOneRemovesTheSpecialRuleEntirely()
    {
        var (world, ch) = Stage();
        var sink = WithPick(world, ch);
        var door = Lock(world, ch, ItemType.DoorLocked);
        Item.MagicUnlockDoor = -1;
        ch.PrivLevel = PrivLevel.GM;      // a GM always passes the skill roll

        Assert.True(ActiveSkillEngine.Lockpicking(sink, door));
        _out.WriteLine($"door is now {door.ItemType}");

        // With the rule off, a magically locked door picks like any other lock.
        Assert.Equal(ItemType.Door, door.ItemType);
    }

    [Fact]
    public void TheChanceIsRolledBeforeTheSkillIsConsulted()
    {
        var (world, ch) = Stage();
        var door = Lock(world, ch, ItemType.DoorLocked);
        Item.MagicUnlockDoor = 900;
        ch.PrivLevel = PrivLevel.GM;      // would otherwise succeed at every skill roll

        // A grandmaster GM still loses to the one-in-900 gate, because the gate is not a
        // skill check - it runs first and refuses outright (CItem.cpp:5428).
        var sink = WithPick(world, ch);
        int refusals = 0;
        for (int seed = 0; seed < 20; seed++)
        {
            sink.Random = new Random(seed);
            if (!ActiveSkillEngine.Lockpicking(sink, door)) refusals++;
        }
        _out.WriteLine($"{refusals} of 20 attempts refused before the skill was asked");

        Assert.True(refusals >= 18, $"the gate let {20 - refusals} of 20 through");
    }

    [Fact]
    public void AnOrdinaryChestIsNotTouchedByTheDoorRule()
    {
        var (world, ch) = Stage();
        var sink = WithPick(world, ch);
        var chest = Lock(world, ch, ItemType.ContainerLocked);
        Item.MagicUnlockDoor = 0;         // would refuse a DOOR outright
        ch.PrivLevel = PrivLevel.GM;

        Assert.True(ActiveSkillEngine.Lockpicking(sink, chest));

        // The rule is about magically locked DOORS. Applying it to chests would make
        // every container in the world unpickable the moment a shard set 0.
        Assert.Equal(ItemType.Container, chest.ItemType);
    }

    // ---- the difficulty the lock actually names --------------------------

    [Fact]
    public void TheLockSaysHowHardItIsInsteadOfADiceRoll()
    {
        var (world, ch) = Stage();
        var sink = WithPick(world, ch);
        ch.PrivLevel = PrivLevel.Player;
        ch.SetSkill(SkillType.Lockpicking, 500);

        var trivial = Lock(world, ch, ItemType.ContainerLocked, complexity: 0);
        var brutal = Lock(world, ch, ItemType.ContainerLocked, complexity: 1000);

        int trivialWins = 0, brutalWins = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            sink.Random = new Random(seed);
            var t = Lock(world, ch, ItemType.ContainerLocked, complexity: 0);
            if (ActiveSkillEngine.Lockpicking(sink, t)) trivialWins++;
            sink.Random = new Random(seed);
            var b = Lock(world, ch, ItemType.ContainerLocked, complexity: 1000);
            if (ActiveSkillEngine.Lockpicking(sink, b)) brutalWins++;
        }
        _out.WriteLine($"complexity 0 -> {trivialWins}/40, complexity 1000 -> {brutalWins}/40");

        // Upstream asks the lock (m_dwLockComplexity / 10, CItem.cpp:5450) rather than
        // inventing a number. A fixed Random.Next(60) made a padlock and a vault door
        // equally hard, which is the sort of invented value that looks like tuning.
        Assert.True(trivialWins > brutalWins,
            $"an easy lock ({trivialWins}) should beat a hard one ({brutalWins})");
        Assert.Equal(ItemType.ContainerLocked, brutal.ItemType);
        Assert.Equal(ItemType.ContainerLocked, trivial.ItemType);
    }

    [Fact]
    public void HoldingTheKeyMakesTheLockTrivial()
    {
        var (world, ch) = Stage();
        var sink = WithPick(world, ch);
        ch.PrivLevel = PrivLevel.Player;
        // 50.0 skill against a complexity-1000 lock is hopeless on merit (the S-curve
        // is flat at that distance) and a certainty at difficulty zero. Picking the two
        // ends rather than the middle keeps the claim about the KEY, not about the die.
        ch.SetSkill(SkillType.Lockpicking, 500);

        int withoutKey = 0, withKey = 0;
        for (int seed = 0; seed < 40; seed++)
        {
            // Each attempt trains the character (UseQuick awards experience), so the
            // skill is put back between them - otherwise the measurement slowly turns
            // into a different measurement.
            ch.SetSkill(SkillType.Lockpicking, 500);
            var bare = Lock(world, ch, ItemType.ContainerLocked, complexity: 1000);
            sink.Random = new Random(seed);
            if (ActiveSkillEngine.Lockpicking(sink, bare)) withoutKey++;

            ch.SetSkill(SkillType.Lockpicking, 500);

            var keyed = Lock(world, ch, ItemType.ContainerLocked, complexity: 1000);
            var key = world.CreateItem();
            key.ItemType = ItemType.Key;
            key.Link = keyed.Uid;
            ch.Backpack!.AddItem(key);
            sink.Random = new Random(seed);
            if (ActiveSkillEngine.Lockpicking(sink, keyed)) withKey++;
            key.Delete();
        }
        _out.WriteLine($"skill 50.0 on a complexity-1000 lock: without key {withoutKey}/40, with key {withKey}/40");

        // Upstream returns "trivial" rather than refusing when the key is in the pack
        // (CItem.cpp:5408), so having the key beats having the skill. It is still a
        // roll - trivial is difficulty zero, not automatic success - which is why this
        // compares the two rather than demanding a certainty.
        // The claim is the GULF, not either number on its own. Both sides go through
        // the skill engine's own die, and the adjusted skill picks up whatever stat
        // bonus the loaded skill definitions grant - so pinning an exact count would be
        // pinning the rest of the suite's fixtures rather than this behaviour.
        Assert.True(withKey - withoutKey >= 25,
            $"with key {withKey}/40, without {withoutKey}/40 - the key made little difference");
        Assert.Equal(500, ch.GetSkill(SkillType.Lockpicking));
    }

    // ---- NORESROBE -------------------------------------------------------

    [Fact]
    public void TheResurrectionRobeIsItsOwnSettingNotTheShrouds()
    {
        var (world, ch) = Stage();
        var death = new DeathEngine(world);

        DeathEngine.EnableDeathShroud = false;   // a shard that wants invisible ghosts
        DeathEngine.NoResRobe = false;

        var robe = death.EnsureResurrectionRobe(ch);
        _out.WriteLine($"shroud off, NoResRobe off -> robe={(robe != null)}");

        // The two used to share one flag, so turning ghosts invisible also resurrected
        // everyone naked. Upstream asks only m_fNoResRobe here (CCharSpell.cpp:503).
        Assert.NotNull(robe);
        Assert.NotNull(ch.GetEquippedItem(Layer.Robe));
    }

    [Fact]
    public void TurningTheKeyOnWithholdsTheRobe()
    {
        var (world, ch) = Stage();
        var death = new DeathEngine(world);
        DeathEngine.NoResRobe = true;

        Assert.Null(death.EnsureResurrectionRobe(ch));
        Assert.Null(ch.GetEquippedItem(Layer.Robe));
    }
}
