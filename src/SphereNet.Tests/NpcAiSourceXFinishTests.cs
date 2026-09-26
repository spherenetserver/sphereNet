using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Combat;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.MapData.Tiles;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// NPC AI behaviours checked against Source-X: the thrown rock's wind-up, grazing
/// through the region's grass resource, retaliation before the armour, the pet's
/// crime, an NPC's own attack as a crime, the flee heading and merged spellbooks.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcAiSourceXFinishTests : IDisposable
{
    private readonly string _defFile =
        Path.Combine(Path.GetTempPath(), $"sphnet_npcfinish_{Guid.NewGuid():N}.scp");

    public void Dispose()
    {
        try { File.Delete(_defFile); } catch (IOException) { }
    }

    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private void LoadDefs(string script)
    {
        var lf = LoggerFactory.Create(_ => { });
        File.WriteAllText(_defFile, script);
        var resources = new ResourceHolder(lf.CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(_defFile) ?? ""
        };
        resources.LoadResourceFile(_defFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    // ------------------------------------------------------------ 1. throw

    [Fact]
    public void AThrowIsAimedAtWhereTheTargetStandsWhenTheWindupEnds()
    {
        // Skill_Act_Throwing (CCharSkill.cpp:3363-3475): START spends 4 + rand(6)
        // stamina and waits 3 s; SUCCESS reads the target's position again, so a
        // target that stepped away (still within 14) is hit where it now stands.
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        ai.Extras |= NpcAiExtraFlags.CombatExtras; // a THROWOBJ tag alone arms a thrower
        long clock = 1_000_000;
        ai.NowMs = () => clock;
        var thrower = world.CreateCharacter();
        thrower.NpcBrain = NpcBrainType.Monster;
        thrower.Hits = thrower.MaxHits = 100;
        thrower.Stam = thrower.MaxStam = 100;
        thrower.SetTag("THROWOBJ", "0x1363");
        world.PlaceCharacter(thrower, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(104, 100, 0, 0));

        int shots = 0, damage = -1;
        ai.OnNpcThrowShot = (_, t, shot) => { shots++; damage = shot.Damage; Assert.Same(target, t); };
        Invoke(ai, "ActFight", thrower, target, 100);
        Assert.Equal(0, shots);
        Assert.InRange((int)thrower.Stam, 91, 96);
        Assert.Equal(NpcAI.NpcSpecialKind.Throw, ai.PendingSpecial(thrower));

        world.MoveCharacter(target, new Point3D(110, 100, 0, 0)); // 10 tiles off now
        clock += NpcAI.SpecialWindupMs;
        Invoke(ai, "ActFight", thrower, target, 100);

        Assert.Equal(1, shots);
        Assert.True(damage > 0); // THROWOBJ: stam/4 + rand(stam/4) off the spent stamina
    }

    [Fact]
    public void APendingSpecialIsDroppedWhenItsTargetIsGone()
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        long clock = 1_000_000;
        ai.NowMs = () => clock;
        var dragon = world.CreateCharacter();
        dragon.NpcBrain = NpcBrainType.Dragon;
        dragon.Hits = dragon.MaxHits = 300;
        dragon.Stam = dragon.MaxStam = 100;
        world.PlaceCharacter(dragon, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(103, 100, 0, 0));
        int breaths = 0;
        ai.OnNpcBreath = (_, _, _) => breaths++;

        Invoke(ai, "ActFight", dragon, target, 100);
        Assert.Equal(NpcAI.NpcSpecialKind.Breath, ai.PendingSpecial(dragon));
        dragon.FightTarget = Serial.Invalid; // m_Fight_Targ_UID no longer names anyone
        clock += NpcAI.SpecialWindupMs;
        Invoke(ai, "RunTickBody", dragon); // the NPC tick resolves it before the brain

        Assert.Equal(0, breaths);
        Assert.Equal(NpcAI.NpcSpecialKind.None, ai.PendingSpecial(dragon));
    }

    // ------------------------------------------------------------ 2. grazing

    private const string GrazeDefs = """
        [TYPEDEFS]
        t_grass 97

        [TYPEDEF t_grass]
        TERRAIN = 0003 0006
        TERRAIN = 07d 08c

        [REGIONRESOURCE mr_grass]
        DEFNAME=mr_grass
        AMOUNT=20
        REAP=0f36
        REGEN=60*60*10

        [REGIONTYPE r_default_grass t_grass]
        DEFNAME=r_default_grass
        RESOURCES=1.0 mr_grass

        [CHARDEF 0cc]
        DEFNAME=c_test_horse
        FOODTYPE=15 t_grass
        """;

    private (GameWorld World, NpcAI Ai, Character Horse, MapDataManager Map) GrazeRig(
        ushort landTile, bool withGrassResource = true)
    {
        LoadDefs(GrazeDefs);
        var world = TestHarness.CreateWorld();
        var md = new MapDataManager("");
        md.AddSyntheticMap(0, 512, 512, landZ: 0, landTile: landTile);
        world.MapData = md;
        if (withGrassResource)
            TestHarness.AttachLoadedRegionTypes(world);
        var ai = new NpcAI(world, new SphereConfig());
        var horse = world.CreateCharacter();
        horse.NpcBrain = NpcBrainType.Animal;
        horse.CharDefIndex = 0x0CC;
        horse.Hits = horse.MaxHits = 50;
        world.PlaceCharacter(horse, new Point3D(100, 100, 0, 0));
        return (world, ai, horse, md);
    }

    private static bool Graze(NpcAI ai, Character horse) =>
        (bool)Invoke(ai, "TryGraze", horse, false)!;

    [Fact]
    public void GrazingEatsTheRegionsGrassResourceOnATGrassTerrainTile()
    {
        // NPC_Food (CCharNPCAct.cpp:2610-2627): CheckNaturalResource(ptMe, IT_GRASS,
        // true) - the tile's top is t_grass by [TYPEDEF t_grass] TERRAIN=, and the
        // area's REGIONTYPE r_default_grass supplies mr_grass (AMOUNT 20). A bite
        // takes 15 of it, a tenth of that as food.
        var (world, ai, horse, _) = GrazeRig(landTile: 0x0080);
        var bites = new System.Collections.Generic.List<(Item Bit, int Food)>();
        ai.OnNpcEatAnim = (_, bit, food) => bites.Add((bit, food));

        Assert.True(Graze(ai, horse));
        var bit = Assert.Single(bites).Bit;
        Assert.Equal(1, bites[0].Food);               // 15 / 10
        Assert.Equal(ItemType.Grass, bit.ItemType);
        Assert.Equal(5, SphereNet.Game.Skills.GatheringEngine.NaturalResourceAmount(bit));
        Assert.True(bit.TryGetTag("NOSAVE", out _));
        Assert.False(bit.IsDeleted);                  // the bit stays and wears down

        Assert.True(Graze(ai, horse));                // the last 5
        Assert.Same(bit, bites[1].Bit);
        Assert.Equal(0, bites[1].Food);
        Assert.False(Graze(ai, horse));               // grazed bare
        Assert.Equal(2, bites.Count);
    }

    [Fact]
    public void GrazingIgnoresTheTileNameAndNeedsTheGrassTypeAndResource()
    {
        // A land tile whose tiledata name says "grass" but which [TYPEDEF t_grass]
        // does not list feeds nothing (it used to be the name test).
        var (_, ai, horse, md) = GrazeRig(landTile: 0x0010);
        md.SetSyntheticLandTile(0x0010, new LandTileData { Name = "grass" });
        int bites = 0;
        ai.OnNpcEatAnim = (_, _, _) => bites++;
        Assert.False(Graze(ai, horse));

        // A t_grass tile in an area with no grass resource feeds nothing either.
        var (_, ai2, horse2, _) = GrazeRig(landTile: 0x0004, withGrassResource: false);
        ai2.OnNpcEatAnim = (_, _, _) => bites++;
        Assert.False(Graze(ai2, horse2));
        Assert.Equal(0, bites);

        // And the same tile with the resource does.
        var (_, ai3, horse3, _) = GrazeRig(landTile: 0x0004);
        ai3.OnNpcEatAnim = (_, _, _) => bites++;
        Assert.True(Graze(ai3, horse3));
        Assert.Equal(1, bites);
    }

    // ------------------------------------------------------------ 3. retaliation

    [Fact]
    public void AnNpcTurnsOnAnAttackerWhoseBlowIsFullyAbsorbed()
    {
        // OnTakeDamage calls OnAttackedBy (CCharFight.cpp:684) before the armour and
        // before @GetHit: a blow reduced to nothing is still an attack the victim
        // answers (OnHarmedBy -> Fight_Attack).
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            var world = TestHarness.CreateWorld();
            var attacker = world.CreateCharacter();
            attacker.IsPlayer = true;
            attacker.PrivLevel = PrivLevel.GM; // the swing lands
            attacker.Str = attacker.Dex = 100;
            attacker.Hits = attacker.MaxHits = 100;
            world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
            var npc = world.CreateCharacter();
            npc.NpcBrain = NpcBrainType.Animal;
            npc.Hits = npc.MaxHits = 100;
            world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
            CombatEngine.OnHitDamage = _ => 0; // @GetHit ARGN1=0

            int dealt = CombatEngine.ResolveAttack(attacker, npc, null);

            Assert.Equal(0, dealt);
            Assert.Equal(100, npc.Hits);
            Assert.Equal(attacker.Uid, npc.FightTarget);
            Assert.NotNull(npc.Memory_FindObjTypes(attacker.Uid, MemoryType.HarmedBy));
        }
        finally
        {
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    // ------------------------------------------------------------ 4. pet crime

    [Fact]
    public void APetsUnprovokedAttackIsNoticedAgainstItsOwner_ButNotItsSelfDefence()
    {
        // OnAttackedBy (CCharFight.cpp:347-370): only when the attacker struck first
        // (it holds no AGGREIVED memory of the victim) does a player victim notice
        // the crime - against the pet AND its owner. There is no per-swing owner
        // flag anywhere else.
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(90, 90, 0, 0));
        var pet = world.CreateCharacter();
        pet.NpcBrain = NpcBrainType.Animal;
        pet.NpcMaster = owner.Uid;
        pet.Hits = pet.MaxHits = 50;
        world.PlaceCharacter(pet, new Point3D(100, 100, 0, 0));
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.Hits = victim.MaxHits = 50;
        world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));
        int ownerNoticed = 0, petNoticed = 0;
        CrimeWitnessService.OnSeeCrime = (_, criminal, _) =>
        {
            if (criminal == owner) ownerNoticed++;
            if (criminal == pet) petNoticed++;
            return false;
        };

        victim.OnAttackedBy(pet);
        Assert.Equal(1, ownerNoticed);
        Assert.Equal(1, petNoticed);
        Assert.False(owner.IsCriminal); // noticed, not flagged: no @SeeCrime ARGN1

        // The other way round: the player struck the pet first, so the pet's blows
        // back are self-defence and nobody's crime.
        var victim2 = world.CreateCharacter();
        victim2.IsPlayer = true;
        victim2.Hits = victim2.MaxHits = 50;
        world.PlaceCharacter(victim2, new Point3D(100, 101, 0, 0));
        pet.OnAttackedBy(victim2);
        ownerNoticed = petNoticed = 0;
        victim2.OnAttackedBy(pet);
        Assert.Equal(0, ownerNoticed);
        Assert.Equal(0, petNoticed);
    }

    // ------------------------------------------------------------ 5. NPC attack crime

    [Fact]
    public void AnNpcStartingAFightOnAnInnocentIsACrimeForWitnesses()
    {
        // Fight_Attack (CCharFight.cpp:1474-1477) runs for an NPC too: a target that
        // is NOTO_GOOD to it and holds no AGGREIVED/HARMEDBY memory of it makes the
        // attack a crime as far as the witnesses see (CheckCrimeSeen, SKILL_NONE).
        bool savedGate = Character.AttackingIsACrimeEnabled;
        try
        {
            Character.AttackingIsACrimeEnabled = true;
            var world = TestHarness.CreateWorld();
            var ai = new NpcAI(world, new SphereConfig());
            var berserk = world.CreateCharacter();
            berserk.NpcBrain = NpcBrainType.Berserk;
            berserk.Hits = berserk.MaxHits = 100;
            berserk.Stam = berserk.MaxStam = 100;
            world.PlaceCharacter(berserk, new Point3D(100, 100, 0, 0));
            var target = world.CreateCharacter();
            target.IsPlayer = true;
            target.Hits = target.MaxHits = 100;
            world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
            var witness = world.CreateCharacter();
            witness.IsPlayer = true;
            witness.Hits = witness.MaxHits = 100;
            world.PlaceCharacter(witness, new Point3D(100, 106, 0, 0));
            int seen = 0;
            CrimeWitnessService.OnSeeCrime = (w, criminal, mark) =>
            {
                if (w == witness && criminal == berserk && mark == target) seen++;
                return false;
            };

            Invoke(ai, "ActBerserk", berserk);

            Assert.Equal(target.Uid, berserk.FightTarget);
            Assert.Equal(1, seen);

            // Hitting back someone who struck first is no crime.
            var berserk2 = world.CreateCharacter();
            berserk2.NpcBrain = NpcBrainType.Monster;
            world.PlaceCharacter(berserk2, new Point3D(120, 120, 0, 0));
            var aggressor = world.CreateCharacter();
            aggressor.IsPlayer = true;
            world.PlaceCharacter(aggressor, new Point3D(121, 120, 0, 0));
            aggressor.Memory_AddObjTypes(berserk2.Uid, MemoryType.Aggreived);
            seen = 0;
            CrimeWitnessService.OnSeeCrime = (_, criminal, _) => { if (criminal == berserk2) seen++; return false; };
            ai.NpcAttackCrimeCheck(berserk2, aggressor);
            Assert.Equal(0, seen);
        }
        finally
        {
            Character.AttackingIsACrimeEnabled = savedGate;
        }
    }

    // ------------------------------------------------------------ 6. flee heading

    [Fact]
    public void AFleeingNpcTurnsThreeToFiveDirectionsFromItsEnemy()
    {
        // NPC_Act_Follow's flee step (CCharNPCAct.cpp:1427-1435):
        // GetDirTurn(dirToEnemy, 4 + 1 - GetValFast(3)) - the opposite heading or
        // one of its two neighbours, at random. The enemy is due west (6), so the
        // step goes north-east, east or south-east.
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var npc = world.CreateCharacter();
        npc.NpcBrain = NpcBrainType.Animal;
        npc.Hits = npc.MaxHits = 50;
        npc.Stam = npc.MaxStam = 50;
        var start = new Point3D(100, 100, 0, 0);
        world.PlaceCharacter(npc, start);
        var enemy = new Point3D(97, 100, 0, 0);

        var seen = new System.Collections.Generic.HashSet<(int, int)>();
        for (int i = 0; i < 200; i++)
        {
            world.MoveCharacter(npc, start);
            npc.NextNpcActionTime = 0;
            Assert.True((bool)Invoke(ai, "FleeAway", npc, enemy)!);
            seen.Add((npc.X - start.X, npc.Y - start.Y));
        }

        Assert.Equal(new System.Collections.Generic.HashSet<(int, int)> { (1, -1), (1, 0), (1, 1) }, seen);
    }

    // ------------------------------------------------------------ 7. spellbooks

    [Fact]
    public void AllCarriedSpellbooksMergeAndAZeroSizeBookAddsNothing()
    {
        // NPC_GetAllSpellbookSpells / NPC_AddSpellsFromBook (CCharNPCAct_Magic.cpp:
        // 100-144): every worn book and every book in the top of the pack adds
        // spells TDATA3+1 .. TDATA3+TDATA4 - on top of a scripted list; TDATA4=0
        // is an empty window.
        LoadDefs("""
            [ITEMDEF 0efa]
            DEFNAME=i_test_book
            TYPE=t_spellbook
            TDATA3=0
            TDATA4=64

            [ITEMDEF 0e3b]
            DEFNAME=i_test_book_empty
            TYPE=t_spellbook
            TDATA3=0
            TDATA4=0
            """);
        var world = TestHarness.CreateWorld();
        var npc = world.CreateCharacter();
        npc.Hits = npc.MaxHits = 50;
        world.PlaceCharacter(npc, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        npc.Backpack = pack;
        npc.Equip(pack, Layer.Pack);
        npc.NpcSpellAdd(SpellType.Fireball); // scripted SPELLS list

        var worn = world.CreateItem();
        worn.BaseId = 0x0EFA;
        worn.ItemType = ItemType.Spellbook;
        worn.More1 = 0x1;                 // spell 1
        npc.Equip(worn, Layer.OneHanded);

        var packed = world.CreateItem();
        packed.BaseId = 0x0EFA;
        packed.ItemType = ItemType.Spellbook;
        packed.More1 = 0x2;               // spell 2
        packed.More2 = 0x1;               // spell 33
        npc.Backpack!.AddItem(packed);

        var empty = world.CreateItem();
        empty.BaseId = 0x0E3B;
        empty.ItemType = ItemType.Spellbook;
        empty.More1 = 0x8;                // spell 4 - but the book holds none
        npc.Backpack!.AddItem(empty);

        NpcAI.EnsureNpcSpellsFromBook(npc);

        Assert.Contains(SpellType.Fireball, npc.NpcSpells);
        Assert.Contains((SpellType)1, npc.NpcSpells);
        Assert.Contains((SpellType)2, npc.NpcSpells);
        Assert.Contains((SpellType)33, npc.NpcSpells);
        Assert.DoesNotContain((SpellType)4, npc.NpcSpells);
    }
}
