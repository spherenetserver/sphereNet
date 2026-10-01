using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.AI;
using SphereNet.Game.Clients;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using SphereNet.MapData;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The shared attack entry and the notoriety inputs around it, against Source-X:
/// <list type="bullet">
/// <item>CChar::OnAttackedBy decides the crime by whether the victim has an active
/// client (CCharFight.cpp:361): a victim without one - a disconnected player still
/// lingering in the world, or an NPC - leaves it to the witnesses
/// (CheckCrimeSeen).</item>
/// <item>OnAttackedBy reveals the attacker unless told not to (fShouldReveal,
/// CChar.h:1393), before the early return for an existing fight target
/// (CCharFight.cpp:341-345). Harmful spells pass !SPELLFLAG_FIELD (CCharSpell.cpp:3777),
/// the damage entry !DAMAGE_NOREVEAL (CCharFight.cpp:684), a pet attack order and
/// provocation the default.</item>
/// <item>A guarded area's RED tag is read as a number (CCharNotoriety.cpp:24).</item>
/// <item>NPC_PetGetOwnerRecursive's link limit, loop and missing-owner answers
/// (CCharNPCStatus.cpp:487), used by the NO_PVP bounce (CCharFight.cpp:674).</item>
/// </list>
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CombatNotorietyRecheckParityTests
{
    private static Character Place(GameWorld world, short x, bool player = false, short y = 100)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        if (player) ch.BodyId = 0x0190;
        ch.Str = 100; ch.Dex = 100; ch.Int = 100;
        ch.MaxHits = 100; ch.Hits = 100;
        world.PlaceCharacter(ch, new Point3D(x, y, 0, 0));
        return ch;
    }

    private static Region AddRegion(GameWorld world, RegionFlag flags, string name = "recheck")
    {
        var region = new Region { Name = name, Flags = flags, MapIndex = 0 };
        region.AddRect(90, 90, 110, 110);
        world.AddRegion(region);
        return region;
    }

    private static Character SpeakingNpc(GameWorld world, short x)
    {
        var npc = Place(world, x);
        npc.NpcBrain = NpcBrainType.Vendor;
        npc.DSpeech.Add(new ResourceId(ResType.Speech, 1));
        return npc;
    }

    // ---- C2: the crime branch follows the active client, not IsPlayer ----------

    [Fact]
    public void ALingeringPlayerVictimLeavesTheCrimeToTheWitnesses()
    {
        var world = TestHarness.CreateWorld();
        AddRegion(world, RegionFlag.Guarded);
        var attacker = Place(world, 100, player: true);
        var victim = Place(world, 101, player: true);
        victim.IsOnline = false;
        victim.SetTag("CLIENT_LINGER_UNTIL", (System.Environment.TickCount64 + 60_000).ToString());
        var witness = SpeakingNpc(world, 102);
        var called = new List<Character>();
        CrimeWitnessService.OnNpcCallGuards = (_, c) => called.Add(c);

        Assert.True(victim.OnAttackedBy(attacker));

        Assert.True(attacker.IsStatFlag(StatFlag.Criminal));
        Assert.Contains(attacker, called);
        Assert.NotNull(witness.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
    }

    [Fact]
    public void AnActivePlayerVictimDecidesTheCrimeItself()
    {
        var world = TestHarness.CreateWorld();
        AddRegion(world, RegionFlag.Guarded);
        var attacker = Place(world, 100, player: true);
        var victim = Place(world, 101, player: true);
        victim.IsOnline = true;
        var witness = SpeakingNpc(world, 102);
        bool called = false;
        CrimeWitnessService.OnNpcCallGuards = (_, _) => called = true;
        var seen = new List<(Character Witness, Character Criminal)>();
        CrimeWitnessService.OnSeeCrime = (w, c, _) => { seen.Add((w, c)); return false; };

        Assert.True(victim.OnAttackedBy(attacker));

        // OnNoticeCrime by the victim: SAWCRIME and @SeeCrime, no global flag.
        Assert.Contains((victim, attacker), seen);
        Assert.NotNull(victim.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
        Assert.False(attacker.IsStatFlag(StatFlag.Criminal));
        Assert.False(called);
        Assert.Null(witness.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
    }

    [Fact]
    public void AnActivePlayerVictimAlsoNoticesThePetsOwner()
    {
        var world = TestHarness.CreateWorld();
        var owner = Place(world, 99, player: true);
        var pet = Place(world, 100);
        pet.SetStatFlag(StatFlag.Pet);
        pet.NpcMaster = owner.Uid;
        var victim = Place(world, 101, player: true);
        victim.IsOnline = true;
        var seen = new List<Character>();
        CrimeWitnessService.OnSeeCrime = (_, c, _) => { seen.Add(c); return false; };

        Assert.True(victim.OnAttackedBy(pet));

        Assert.Contains(pet, seen);
        Assert.Contains(owner, seen);
    }

    [Fact]
    public void AnNpcVictimStillLeavesTheCrimeToTheWitnesses()
    {
        var world = TestHarness.CreateWorld();
        AddRegion(world, RegionFlag.Guarded);
        var attacker = Place(world, 100, player: true);
        var victim = Place(world, 101);
        victim.NpcBrain = NpcBrainType.Human;
        var witness = SpeakingNpc(world, 102);
        var called = new List<Character>();
        CrimeWitnessService.OnNpcCallGuards = (_, c) => called.Add(c);

        Assert.True(victim.OnAttackedBy(attacker));

        Assert.True(attacker.IsStatFlag(StatFlag.Criminal));
        Assert.Contains(attacker, called);
        Assert.NotNull(witness.Memory_FindObjTypes(attacker.Uid, MemoryType.SawCrime));
    }

    // ---- C5: OnAttackedBy reveals the attacker --------------------------------

    [Fact]
    public void OnAttackedByRevealsTheAttackerByDefault()
    {
        var world = TestHarness.CreateWorld();
        var src = Place(world, 100, player: true);
        var victim = Place(world, 101);
        src.SetStatFlag(StatFlag.Hidden);

        Assert.True(victim.OnAttackedBy(src));

        Assert.False(src.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void OnAttackedByWithoutRevealKeepsTheAttackerHidden()
    {
        var world = TestHarness.CreateWorld();
        var src = Place(world, 100, player: true);
        var victim = Place(world, 101);
        src.SetStatFlag(StatFlag.Hidden);

        Assert.True(victim.OnAttackedBy(src, shouldReveal: false));

        Assert.True(src.IsStatFlag(StatFlag.Hidden));
        Assert.True(victim.Attacker_GetIndex(src.Uid) >= 0);
    }

    [Fact]
    public void TheRevealComesBeforeTheExistingFightTargetReturn()
    {
        // :341 Reveal, then :344 "Am i already attacking the source anyhow".
        var world = TestHarness.CreateWorld();
        var src = Place(world, 100, player: true);
        var victim = Place(world, 101);
        victim.SetStatFlag(StatFlag.War);
        victim.FightTarget = src.Uid;
        src.SetStatFlag(StatFlag.Hidden);

        Assert.True(victim.OnAttackedBy(src));

        Assert.False(src.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void ADeadOrStoneVictimRefusesBeforeTheReveal()
    {
        // :337 returns false before the reveal at :341.
        var world = TestHarness.CreateWorld();
        var src = Place(world, 100, player: true);
        var victim = Place(world, 101);
        victim.SetStatFlag(StatFlag.Stone);
        src.SetStatFlag(StatFlag.Hidden);

        Assert.False(victim.OnAttackedBy(src));

        Assert.True(src.IsStatFlag(StatFlag.Hidden));
    }

    private static (SpellEngine Engine, Character Caster, Character Npc) SpellSetup(SpellFlag flags)
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef { Id = SpellType.Clumsy, Flags = flags, DurationBase = 100 });
        var engine = new SpellEngine(world, registry);
        var caster = Place(world, 100, player: true);
        var npc = Place(world, 103);
        npc.NpcBrain = NpcBrainType.Human;
        return (engine, caster, npc);
    }

    [Fact]
    public void AHarmfulSpellRevealsItsCaster()
    {
        var (engine, caster, npc) = SpellSetup(SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Curse);
        caster.SetStatFlag(StatFlag.Hidden);

        engine.ApplyDirectEffect(caster, npc, SpellType.Clumsy, 500);

        Assert.True(npc.Attacker_GetIndex(caster.Uid) >= 0);
        Assert.False(caster.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void AFieldSpellDoesNotRevealItsCaster()
    {
        var (engine, caster, npc) = SpellSetup(
            SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Curse | SpellFlag.Field);
        caster.SetStatFlag(StatFlag.Hidden);

        engine.ApplyDirectEffect(caster, npc, SpellType.Clumsy, 500);

        Assert.True(npc.Attacker_GetIndex(caster.Uid) >= 0);
        Assert.True(caster.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void ThePetAttackOrderRevealsTheOwner()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var lf = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 7842);
        var owner = Place(world, 100, player: true);
        owner.MaxFollower = 5;
        TestHarness.AttachCharacter(client, owner);
        var pet = Place(world, 101);
        pet.BodyId = 0x00C8;
        Assert.True(pet.TryAssignOwnership(owner, owner));
        pet.PetAIMode = PetAIMode.Stay;
        var victim = Place(world, 103);
        owner.SetStatFlag(StatFlag.Hidden);

        typeof(ClientItemUseHandler)
            .GetMethod("ApplyPetTarget", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client.ItemUse, [pet, "attack", victim.Uid, (short)0, (short)0, (sbyte)0]);

        Assert.Equal(victim.Uid, pet.FightTarget);
        Assert.False(owner.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void TheDamageEntryRevealsOnceAndHonoursNoReveal()
    {
        var world = TestHarness.CreateWorld();
        var src = Place(world, 100, player: true);
        var victim = Place(world, 101);
        int reveals = 0;
        Character.OnRevealing = _ => { reveals++; return true; };

        src.SetStatFlag(StatFlag.Hidden);
        Assert.True(CombatEngine.ApplyScriptDamage(victim, 5, DamageType.HitBlunt | DamageType.NoReveal, src) > 0);
        Assert.True(src.IsStatFlag(StatFlag.Hidden));
        Assert.Equal(0, reveals);

        Assert.True(CombatEngine.ApplyScriptDamage(victim, 5, DamageType.HitBlunt, src) > 0);
        Assert.False(src.IsStatFlag(StatFlag.Hidden));
        Assert.Equal(1, reveals);
    }

    // ---- C8: a guarded area's numeric RED tag ---------------------------------

    [Theory]
    [InlineData(null, true, 6)]
    [InlineData("0", true, 6)]
    [InlineData("1", true, 1)]
    [InlineData("02", true, 1)]
    [InlineData("1", false, 6)]
    public void AGuardedRedAreaTurnsTheNotorietyAround(string? red, bool guarded, int expected)
    {
        var world = TestHarness.CreateWorld();
        var region = AddRegion(world, guarded ? RegionFlag.Guarded : RegionFlag.None);
        if (red != null)
            region.SetTag("RED", red);
        var viewer = Place(world, 100, player: true);
        var subject = Place(world, 101, player: true);
        subject.Karma = 1000;
        subject.Kills = (short)(Character.MurderMinCount + 1);

        Assert.Equal(expected, GameClient.ComputeNotoriety(world, viewer, subject));
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("1", false)]
    [InlineData("02", false)]
    public void TheNpcEvilTestReadsTheRedTagAsANumber(string red, bool expectedEvil)
    {
        var world = TestHarness.CreateWorld();
        var region = AddRegion(world, RegionFlag.Guarded);
        region.SetTag("RED", red);
        var subject = Place(world, 101, player: true);
        subject.Karma = 1000;
        subject.Kills = (short)(Character.MurderMinCount + 1);

        Assert.Equal(expectedEvil, NpcAI.NotoIsEvil(subject, world));
    }

    [Fact]
    public void TheEngineOnlyRegionBitNoLongerActsAsARedArea()
    {
        // Source-X has no region flag for a red area (CRegion.h:37-58): the bit that
        // used to be read as one is just an unknown flag value now.
        var world = TestHarness.CreateWorld();
        AddRegion(world, RegionFlag.Guarded | (RegionFlag)0x10000000);
        var viewer = Place(world, 100, player: true);
        var subject = Place(world, 101, player: true);
        subject.Karma = 1000;
        subject.Kills = (short)(Character.MurderMinCount + 1);

        Assert.Equal(6, GameClient.ComputeNotoriety(world, viewer, subject));
    }

    // ---- C10: the recursive owner -------------------------------------------

    /// <summary>A chain start -> n1 -> ... -> terminal with <paramref name="links"/>
    /// owner links; the terminal is a player when asked.</summary>
    private static (Character Start, Character Terminal) Chain(GameWorld world, int links, bool playerAtTop = true)
    {
        var terminal = Place(world, 20, player: playerAtTop, y: 20);
        var next = terminal;
        Character start = terminal;
        for (int i = 0; i < links; i++)
        {
            var npc = Place(world, (short)(30 + i), y: 30);
            npc.SetStatFlag(StatFlag.Pet);
            npc.NpcMaster = next.Uid;
            next = npc;
            start = npc;
        }
        return (start, terminal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(16)]
    [InlineData(17)]
    public void ThePrimaryOwnerIsFoundUpToSeventeenLinks(int links)
    {
        var world = TestHarness.CreateWorld();
        var (start, terminal) = Chain(world, links);

        Assert.Same(terminal, start.PetGetOwnerRecursive(world));
    }

    [Fact]
    public void AnEighteenthLinkIsTooManyOwners()
    {
        var world = TestHarness.CreateWorld();
        var (start, _) = Chain(world, 18);

        Assert.Null(start.PetGetOwnerRecursive(world));
    }

    [Fact]
    public void AChainEndingAtAnNpcReturnsThatNpc()
    {
        // The loop ends when the last NPC has no owner: the answer is that NPC, even
        // at seventeen NPC links.
        var world = TestHarness.CreateWorld();
        var (start, terminal) = Chain(world, 16, playerAtTop: false);
        Assert.Same(terminal, start.PetGetOwnerRecursive(world));

        var (start17, terminal17) = Chain(world, 17, playerAtTop: false);
        Assert.Same(terminal17, start17.PetGetOwnerRecursive(world));
    }

    [Fact]
    public void AMissingOwnerEndsTheChainAtTheLastOneFound()
    {
        var world = TestHarness.CreateWorld();
        var top = Place(world, 20, y: 20);
        top.SetStatFlag(StatFlag.Pet);
        top.NpcMaster = new Serial(0x7FFF0001);     // nobody
        var pet = Place(world, 21, y: 20);
        pet.SetStatFlag(StatFlag.Pet);
        pet.NpcMaster = top.Uid;

        Assert.Same(top, pet.PetGetOwnerRecursive(world));
        Assert.Null(Place(world, 22, y: 20).PetGetOwnerRecursive(world));
    }

    [Fact]
    public void CircularOwnershipHasNoPrimaryOwner()
    {
        var world = TestHarness.CreateWorld();
        var a = Place(world, 20, y: 20);
        var b = Place(world, 21, y: 20);
        a.SetStatFlag(StatFlag.Pet); a.NpcMaster = b.Uid;
        b.SetStatFlag(StatFlag.Pet); b.NpcMaster = a.Uid;
        var self = Place(world, 22, y: 20);
        self.SetStatFlag(StatFlag.Pet); self.NpcMaster = self.Uid;

        Assert.Null(a.PetGetOwnerRecursive(world));
        Assert.Null(self.PetGetOwnerRecursive(world));
    }

    [Theory]
    [InlineData(2, 0)]
    [InlineData(16, 0)]
    [InlineData(17, 0)]
    [InlineData(18, 20)]
    public void NoPvpSeesThePlayerAtTheTopOfTheChain(int links, int expected)
    {
        var world = TestHarness.CreateWorld();
        AddRegion(world, RegionFlag.NoPvP);
        var victim = Place(world, 100, player: true);
        var (start, _) = Chain(world, links);

        Assert.Equal(expected, CombatEngine.ApplyScriptDamage(victim, 20, DamageType.HitBlunt | DamageType.Fixed, start));
    }
}
