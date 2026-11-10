using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Mounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Network.Packets.Outgoing;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Behaviour SphereNet had that Source-X does not, or lacked where Source-X has it:
/// <list type="bullet">
/// <item>no stat or skill loss on a murderer's resurrection (Spell_Resurrection,
/// CCharSpell.cpp:441-533), and an antimagic refusal gives nothing back;</item>
/// <item>a started skill at difficulty 0 is never rolled (Skill_Start, CCharSkill.cpp:
/// 4566-4572);</item>
/// <item>mounting has no fight or freeze gate (Horse_Mount, CCharAct.cpp:3951);</item>
/// <item>a dead owner's pet keeps its order;</item>
/// <item>0x11 puts ML max weight and race right after the weight (send.cpp:253-283);</item>
/// <item>hit points at zero kill on the character's own tick (CCharAct.cpp:6097);</item>
/// <item>0x75 renames an owned pet, never an item (Event_SetName, CClientEvent.cpp:2217);</item>
/// <item>a trash can empties itself (CItemContainer.cpp:1025-1029, 1415-1418).</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXInventedBehaviourTests
{
    private static Character Player(GameWorld world, short x = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BodyId = 0x0190;
        ch.Str = 50; ch.Dex = 40; ch.Int = 30;
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    // ---- resurrection ----

    [Fact]
    public void AMurderersResurrection_CostsNoStatsOrSkills()
    {
        var world = TestHarness.CreateWorld();
        var ch = Player(world);
        ch.Kills = 1000;
        Assert.True(ch.IsMurderer);
        ch.SetSkill(SkillType.Magery, 800);

        ch.Kill();
        ch.Resurrect();

        Assert.False(ch.IsDead);
        Assert.Equal(50, ch.Str);
        Assert.Equal(40, ch.Dex);
        Assert.Equal(30, ch.Int);
        Assert.Equal(800, ch.GetSkill(SkillType.Magery));
    }

    [Fact]
    public void ARezRefusedByAntimagic_LeavesTheGhostAGhost_AndFiresNoTrigger()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var region = new SphereNet.Game.World.Regions.Region
        { Name = "am_rez", Flags = RegionFlag.NoMagic, MapIndex = 0 };
        region.AddRect(90, 90, 110, 110);
        world.AddRegion(region);

        var ch = Player(world);
        var client = TestHarness.CreateClient(logs, world, new AccountManager(logs), 31001);
        TestHarness.AttachCharacter(client, ch);
        var triggers = new TriggerDispatcher();
        int rezTriggers = 0;
        triggers.RegisterCharEvent("EVENTSPLAYER", "Resurrect", (_, _) => { rezTriggers++; return TriggerResult.Default; });
        client.SetEngines(triggerDispatcher: triggers);

        ch.Kill();
        ch.BodyId = 0x0192; // ghost
        ch.OBody = 0x0190;

        client.OnResurrect();

        Assert.True(ch.IsDead);
        Assert.Equal(0x0192, ch.BodyId);
        Assert.Equal(0, rezTriggers);
    }

    // ---- skill start roll ----

    [Fact]
    public void AStartedSkillAtDifficultyZero_NeverFails()
    {
        var world = TestHarness.CreateWorld();
        var ch = Player(world);
        ch.SetSkill(SkillType.Stealth, 0);

        for (int i = 0; i < 200; i++)
            Assert.True(SkillEngine.UseStart(ch, SkillType.Stealth, 0, allowGain: false));
        Assert.False(SkillEngine.UseStart(ch, SkillType.Stealth, -1, allowGain: false));
    }

    // ---- mounting ----

    [Fact]
    public void ARiderInAFight_OrFrozen_StillMounts()
    {
        var world = TestHarness.CreateWorld();
        var rider = Player(world);
        var enemy = Player(world, 105);
        rider.FightTarget = enemy.Uid;
        rider.SetStatFlag(StatFlag.Freeze);

        var horse = world.CreateCharacter();
        horse.BodyId = 0x00C8;
        horse.Hits = horse.MaxHits = 50;
        world.PlaceCharacter(horse, new Point3D(100, 100, 0, 0));
        Assert.True(horse.TryAssignOwnership(rider, rider));

        Assert.True(new MountEngine(world).TryMount(rider, horse));
        Assert.True(rider.IsMounted);
    }

    // ---- a dead owner's pet ----

    [Fact]
    public void APetToldToStay_StaysWhenItsOwnerDies()
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig()) { Flags = NpcAIFlags.None };
        var owner = Player(world);
        var pet = world.CreateCharacter();
        pet.BodyId = 0x00C8;
        pet.Str = 100; pet.Hits = pet.MaxHits = 100;
        pet.Dex = 100; pet.Stam = pet.MaxStam;
        world.PlaceCharacter(pet, new Point3D(104, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner));
        pet.PetAIMode = PetAIMode.Stay;

        owner.Kill();
        var at = pet.Position;
        for (int i = 0; i < 60; i++)
        {
            pet.NextNpcActionTime = 0;
            ai.OnTickAction(pet);
        }

        Assert.Equal(at.X, pet.X);
        Assert.Equal(at.Y, pet.Y);
        Assert.Equal(PetAIMode.Stay, pet.PetAIMode);
    }

    // ---- 0x11 ----

    [Fact]
    public void StatusFull_ML_PutsMaxWeightAndRaceRightAfterTheWeight()
    {
        var packet = new PacketStatusFull(0x01020304, "Tester",
            50, 60, 70, 80, 90, 10, 20, 30, 40,
            1000, 5, 0x0102, 0, 0, 0, expansionLevel: 5,
            statCap: 0x00E1, followers: 2, maxFollowers: 5,
            maxWeight: 0x1234, race: PacketStatusFull.RaceFromBody(0x025D)).Build();
        var b = packet.Span;

        // cmd(1) len(2) serial(4) name(30) hits(2) maxhits(2) rename(1) type(1)
        // sex(1) str dex int stam maxstam mana maxmana(14) gold(4) armor(2) = 64
        Assert.Equal(0x01, b[64]); Assert.Equal(0x02, b[65]);      // weight
        Assert.Equal(0x12, b[66]); Assert.Equal(0x34, b[67]);      // max weight
        Assert.Equal(2, b[68]);                                    // race: elf
        Assert.Equal(0x00, b[69]); Assert.Equal(0xE1, b[70]);      // stat cap
        Assert.Equal(2, b[71]); Assert.Equal(5, b[72]);            // followers
    }

    // ---- death on the character's tick ----

    [Fact]
    public void HitsSetToZero_KillsOnTheCharactersTick()
    {
        var world = TestHarness.CreateWorld();
        var ch = Player(world);
        Assert.True(ch.TrySetProperty("HITS", "0"));
        Assert.False(ch.IsDead);

        ch.OnTick();

        Assert.True(ch.IsDead);
    }

    // ---- 0x75 rename ----

    [Fact]
    public void Rename_AnOwnerNamesTheirPet_NobodyElsesAndNoItem()
    {
        using var logs = TestHarness.CreateLoggerFactory();
        var world = TestHarness.CreateWorld();
        var owner = Player(world);
        var stranger = Player(world, 101);
        var pet = world.CreateCharacter();
        pet.Name = "a horse";
        pet.Hits = pet.MaxHits = 50;
        world.PlaceCharacter(pet, new Point3D(102, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner));
        var item = world.CreateItem();
        item.Name = "a box";
        world.PlaceItem(item, new Point3D(100, 101, 0, 0));

        var ownerClient = TestHarness.CreateClient(logs, world, new AccountManager(logs), 31002);
        TestHarness.AttachCharacter(ownerClient, owner);
        var strangerClient = TestHarness.CreateClient(logs, world, new AccountManager(logs), 31003);
        TestHarness.AttachCharacter(strangerClient, stranger);

        strangerClient.HandleRename(pet.Uid.Value, "Stolen");
        Assert.Equal("a horse", pet.Name);

        ownerClient.HandleRename(pet.Uid.Value, "release");   // a pet-command verb
        Assert.Equal("a horse", pet.Name);

        ownerClient.HandleRename(pet.Uid.Value, "Thunder");
        Assert.Equal("Thunder", pet.Name);

        stranger.PrivLevel = PrivLevel.GM;
        strangerClient.HandleRename(item.Uid.Value, "Renamed");
        Assert.Equal("a box", item.Name);
    }

    // ---- trash can ----

    [Fact]
    public void ATrashCan_EmptiesWhenItsTimerRunsOut()
    {
        var world = TestHarness.CreateWorld();
        var can = world.CreateItem();
        can.ItemType = ItemType.TrashCan;
        world.PlaceItem(can, new Point3D(100, 100, 0, 0));
        var junk = world.CreateItem();
        Assert.True(can.TryAddItem(junk));

        can.SetTimeout(Environment.TickCount64 - 1);
        can.OnTick();

        Assert.True(junk.IsDeleted);
        Assert.False(can.IsDeleted);
        Assert.Equal(0, can.ContentCount);
    }
}
