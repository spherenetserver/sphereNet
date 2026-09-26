using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Clients;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>Source-X parity for character notoriety, movement stamina/shove, regen,
/// ghost bodies, stat caps, carry weight and config defaults.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CharacterNotorietyMovementParityTests
{
    private static GameWorld PlainWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static GameWorld MapWorld()
    {
        var map = new SphereNet.MapData.MapDataManager("");
        map.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 512, 512);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Character Player(GameWorld world, int x, int stam = 50)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Name = "p" + x;
        ch.Dex = 50;
        ch.MaxStam = 50;
        ch.Stam = (short)stam;
        world.PlaceCharacter(ch, new Point3D((short)x, 100, 0, 0));
        return ch;
    }

    // ---- notoriety ----

    [Fact]
    public void HealerAndBanker_AreNotForcedYellow_OnlyInvulIs()
    {
        var world = PlainWorld();
        var viewer = Player(world, 100);
        var healer = world.CreateCharacter();
        healer.NpcBrain = NpcBrainType.Healer;
        healer.Karma = 1000;
        world.PlaceCharacter(healer, new Point3D(101, 100, 0, 0));

        // CCharNotoriety.cpp:152-153, 281: brain does not matter, STATF_INVUL does.
        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, healer));
        healer.SetStatFlag(StatFlag.Invul);
        Assert.Equal(7, GameClient.ComputeNotoriety(world, viewer, healer));
    }

    [Fact]
    public void PersonalGrey_AppliesToNpcSubjectsToo()
    {
        var world = PlainWorld();
        var viewer = Player(world, 100);
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Human;
        npc.Karma = 1000;
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
        Assert.Equal(1, GameClient.ComputeNotoriety(world, viewer, npc));

        // MEMORY_SAWCRIME | MEMORY_AGGREIVED, any subject (CCharNotoriety.cpp:265-271).
        viewer.Memory_AddObjTypes(npc.Uid, MemoryType.SawCrime);
        Assert.Equal(4, GameClient.ComputeNotoriety(world, viewer, npc));
    }

    // ---- movement ----

    [Fact]
    public void ZeroStamina_BlocksWalking_WithFatigueMessage()
    {
        var world = MapWorld();
        var ch = Player(world, 100, stam: 0);
        var engine = new MovementEngine(world);
        string? msg = null;
        engine.OnSysMessage = (_, m) => msg = m;

        Assert.False(engine.TryMove(ch, Direction.East, running: false, sequence: 1));
        Assert.Equal(100, ch.X);
        Assert.Equal(SphereNet.Game.Messages.ServerMessages.Get(SphereNet.Game.Messages.Msg.MsgFatigue), msg);

        // A ghost is not stopped by an empty stamina pool.
        ch.SetStatFlag(StatFlag.Dead);
        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 2));
    }

    [Fact]
    public void RunBitSetsAndClearsTheRunningFlag()
    {
        var world = MapWorld();
        var ch = Player(world, 100);
        var engine = new MovementEngine(world);

        Assert.True(engine.TryMove(ch, Direction.East, running: true, sequence: 1));
        Assert.True(ch.IsStatFlag(StatFlag.Fly));
        Assert.True(engine.TryMove(ch, Direction.East, running: false, sequence: 2));
        Assert.False(ch.IsStatFlag(StatFlag.Fly));
    }

    [Fact]
    public void OverweightRunner_IsNotForcedToWalk_ButPaysStamina()
    {
        var world = MapWorld();
        var ch = Player(world, 100);
        ch.Str = 1; // carry limit 43 stones
        var pack = world.CreateItem();
        pack.BaseId = 0x0E75;
        pack.ItemType = ItemType.Container;
        ch.Equip(pack, Layer.Pack);
        var ingots = world.CreateItem();
        ingots.BaseId = 0x1BF2;
        ingots.Amount = 1;
        ingots.SetTag("OVERRIDE.WEIGHT", "500");
        pack.AddItem(ingots);
        var engine = new MovementEngine(world);
        int before = ch.Stam;

        Assert.True(engine.TryMove(ch, Direction.East, running: true, sequence: 1));
        Assert.True(ch.IsStatFlag(StatFlag.Fly)); // the run bit is honoured
        Assert.True(ch.Stam <= before);
    }

    [Fact]
    public void ShovingAVisiblePlayer_CostsTenStaminaAndNeedsFullStamina()
    {
        var world = MapWorld();
        var mover = Player(world, 100);
        Player(world, 101);
        var engine = new MovementEngine(world);

        Assert.True(engine.TryMove(mover, Direction.East, running: false, sequence: 1));
        Assert.Equal(40, mover.Stam); // 50 - 10 (CCharAct.cpp:4627)

        // Not at full stamina: the next push is refused (:4661-4662).
        Player(world, 102);
        Assert.False(engine.TryMove(mover, Direction.East, running: false, sequence: 2));
    }

    [Fact]
    public void ShovingAHiddenPlayer_IsFree_AndRevealsTheHiddenOne()
    {
        var world = MapWorld();
        var mover = Player(world, 100);
        var hidden = Player(world, 101);
        hidden.SetStatFlag(StatFlag.Hidden);
        var engine = new MovementEngine(world);

        Assert.True(engine.TryMove(mover, Direction.East, running: false, sequence: 1));
        Assert.Equal(50, mover.Stam);                     // no stamina (:4628-4629)
        Assert.False(hidden.IsStatFlag(StatFlag.Hidden)); // the one walked into is revealed (:4679)
    }

    [Fact]
    public void Blockers_MoreThanFiveZAway_OrInsubstantial_AreIgnored()
    {
        var world = MapWorld();
        var mover = Player(world, 100, stam: 20); // not full: a real push would be refused
        var ghostly = Player(world, 101);
        ghostly.SetStatFlag(StatFlag.Insubstantial);
        var engine = new MovementEngine(world);

        Assert.True(engine.TryMove(mover, Direction.East, running: false, sequence: 1));
        Assert.Equal(20, mover.Stam);
    }

    [Fact]
    public void PersonalSpaceArgn1_ChangesThePushCost()
    {
        var world = MapWorld();
        var mover = Player(world, 100);
        Player(world, 101);
        Character.OnPersonalSpace = (_, _, args) => { args.StaminaRequired = 3; return false; };
        var engine = new MovementEngine(world);

        Assert.True(engine.TryMove(mover, Direction.East, running: false, sequence: 1));
        Assert.Equal(47, mover.Stam);
    }

    [Fact]
    public void Counselor_PaysForAShove_OnlyGmIsExempt()
    {
        var world = MapWorld();
        var mover = Player(world, 100);
        mover.PrivLevel = PrivLevel.Counsel;
        Player(world, 101);
        var engine = new MovementEngine(world);

        Assert.True(engine.TryMove(mover, Direction.East, running: false, sequence: 1));
        Assert.Equal(40, mover.Stam);
    }

    // ---- ghost bodies ----

    [Theory]
    [InlineData(0x0190, 0x0192)]
    [InlineData(0x0191, 0x0193)]
    [InlineData(0x025D, 0x025F)]
    [InlineData(0x025E, 0x0260)]
    [InlineData(0x029A, 0x02B6)]
    [InlineData(0x029B, 0x02B7)]
    public void GhostBody_FollowsRaceAndGender(int living, int ghost)
    {
        // CCharAct.cpp:4447-4469; with no pack loaded the stock graphics answer.
        Assert.Equal((ushort)ghost, Character.ResolveGhostBody((ushort)living));
    }

    // ---- regen ----

    [Fact]
    public void Regen_PoolAboveItsLimit_DropsToTheLimit()
    {
        var world = PlainWorld();
        var ch = Player(world, 100);
        ch.Str = 50;
        ch.MaxHits = 50;
        ch.SetHitsRaw(80); // e.g. a bonus that has just been taken off
        Assert.Equal(80, ch.Hits);

        ch.OnTick();

        // iMod = -1 then UpdateStatVal caps at the limit (CCharStat.cpp:531-534,
        // CCharAct.cpp:766-772) - without OF_StatAllowValOverMax.
        Assert.Equal(50, ch.Hits);
    }

    [Fact]
    public void Regen_PoolAboveItsLimit_DecaysByOne_WithStatAllowValOverMax()
    {
        var world = PlainWorld();
        var ch = Player(world, 100);
        ch.Str = 50;
        ch.MaxHits = 50;
        ch.SetHitsRaw(80);
        GameClient.ServerOptionFlags |= OptionFlags.StatAllowValueOverMax;

        ch.OnTick();

        Assert.Equal(79, ch.Hits);
    }

    // ---- stat caps / weight ----

    [Fact]
    public void StatCaps_FallBackToSourceXClassDefaults_AndHonourOverrideTags()
    {
        var world = PlainWorld();
        var player = Player(world, 100);
        player.SkillClass = 9999; // no such class
        Assert.Equal(100, SkillEngine.StatCapStr(player));   // CSkillClassDef::Init
        player.SetTag("OVERRIDE.STATCAP_0", "130");
        Assert.Equal(130, SkillEngine.StatCapStr(player));   // Stat_GetLimit tag

        var npc = world.CreateCharacter();
        Assert.Equal(100, SkillEngine.StatCapInt(npc));      // NPC limit 100
        Assert.Equal(10000, SkillEngine.GetSkillSumMax(player));
    }

    [Fact]
    public void MaxWeight_HumanStrongBack_AddsSixtyStones()
    {
        var world = PlainWorld();
        var ch = Player(world, 100);
        ch.Str = 100;
        int plain = ch.MaxWeight;
        Assert.Equal(40 + 350, plain);

        Character.RacialFlags = 0x0001; // RACIALF_HUMAN_STRONGBACK
        Assert.Equal(plain + 60, ch.MaxWeight);
    }

    // ---- config ----

    [Fact]
    public void ConfigDefaults_MatchTheSourceXConstructor()
    {
        var cfg = new SphereConfig();
        Assert.Equal(50, cfg.ManaLossPercent);
        Assert.False(cfg.ReagentsRequired);
        Assert.False(cfg.MonsterFear);
        Assert.Equal(2, cfg.ArcheryMinDist);
        Assert.Equal(15, cfg.ArcheryMaxDist);
        Assert.True(cfg.EquippedCast);
        Assert.True(cfg.HelpingCriminalsIsACrime);
        Assert.Equal(30, cfg.AttackerTimeout);
        Assert.Equal(31, cfg.MapViewRadar);
        Assert.Equal(24, cfg.MapViewSizeMax);
        Assert.Equal(31, cfg.DistanceYell);
        Assert.Equal(1, cfg.PacketDeathAnimation);
        Assert.Equal(1, cfg.ToolTipMode);
        Assert.Equal(20, cfg.SavePeriodMinutes);
        Assert.Equal(600, cfg.ClientLinger);
        Assert.Equal(50, cfg.MaxPacketsPerTick);
        Assert.Equal(100, cfg.SnoopCriminal);
    }
}
