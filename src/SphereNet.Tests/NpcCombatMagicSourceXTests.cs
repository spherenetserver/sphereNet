using System.Reflection;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.AI;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// NPC fight and magery against the Source-X reference (CCharNPCAct_Fight.cpp,
/// CCharNPCAct_Magic.cpp, CCharNPCStatus.cpp) with NPCAIEXTRAS at 0, and the
/// optional behaviours that only run with their NPCAIEXTRAS bit.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class NpcCombatMagicSourceXTests
{
    private static object? Invoke(NpcAI ai, string method, params object[] args) =>
        typeof(NpcAI).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(ai, args);

    private static Item AddPack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    private static (GameWorld World, NpcAI Ai, Character Caster, Character Enemy) Duel(int distance = 5)
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var caster = world.CreateCharacter();
        caster.NpcBrain = NpcBrainType.Monster;
        caster.Hits = caster.MaxHits = 100;
        caster.Stam = caster.MaxStam = 100;
        caster.Int = 100;
        caster.Mana = caster.MaxMana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var enemy = world.CreateCharacter();
        enemy.IsPlayer = true;
        enemy.Hits = enemy.MaxHits = 100;
        world.PlaceCharacter(enemy, new Point3D((short)(100 + distance), 100, 0, 0));
        return (world, ai, caster, enemy);
    }

    /// <summary>Run the magery step until it casts (the mana chance roll fails a
    /// few percent of the time); returns the (spell, target) cast or None.</summary>
    private static (SpellType Spell, Character? Target) CastOnce(NpcAI ai, Character caster, Character enemy, int tries = 200)
    {
        SpellType cast = SpellType.None;
        Character? castOn = null;
        ai.OnNpcCastSpell = (_, t, s) => { cast = s; castOn = t; };
        for (int i = 0; i < tries && cast == SpellType.None; i++)
        {
            caster.Mana = caster.MaxMana;
            Invoke(ai, "TryNpcCastSpell", caster, enemy, caster.Position.GetDistanceTo(enemy.Position));
        }
        return (cast, castOn);
    }

    // ---- NPCNoCastTill is in tenths of the server clock (SERV.TIME) ----

    [Fact]
    public void NpcNoCastTill_IsComparedWithTheServerClockInTenths()
    {
        var (world, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.MagicArrow);
        world.SetGameClockMs(600_000); // SERV.TIME = 6000
        caster.SetTag("NPCNoCastTill", "6050");

        Assert.Equal(SpellType.None, CastOnce(ai, caster, enemy, 50).Spell);

        world.SetGameClockMs(606_000); // SERV.TIME = 6060, past the pause
        Assert.Equal(SpellType.MagicArrow, CastOnce(ai, caster, enemy).Spell);
    }

    [Fact]
    public void NpcFightMayCast_NeedsFiveMana()
    {
        var (_, ai, caster, _) = Duel();
        caster.Mana = 4;
        Assert.False((bool)Invoke(ai, "NpcFightMayCast", caster)!);
        caster.Mana = 5;
        Assert.True((bool)Invoke(ai, "NpcFightMayCast", caster)!);
    }

    // ---- The default spell choice is the Source-X walk + NPC_FightCast ----

    [Fact]
    public void Default_AHealIsCastOnlyUnderTheHealThreshold()
    {
        var (_, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.Heal);

        Assert.Equal(SpellType.None, CastOnce(ai, caster, enemy, 50).Spell); // full health

        caster.Hits = 30; // NPCHEALTHRESHOLD default 30: GetStatPercent <= 30
        var (spell, target) = CastOnce(ai, caster, enemy);
        Assert.Equal(SpellType.Heal, spell);
        Assert.Same(caster, target);
    }

    [Fact]
    public void Default_HarmfulSpellsAreCastEvenAtAReflectingTarget()
    {
        // NPC_FightCast accepts every SPELLFLAG_HARM spell (CCharNPCAct_Magic.cpp:434-449);
        // steering round Magic Reflection is a SmartCaster refinement.
        var (_, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.EnergyBolt);
        enemy.SetStatFlag(StatFlag.Reflection);
        var (spell, target) = CastOnce(ai, caster, enemy);
        Assert.Equal(SpellType.EnergyBolt, spell);
        Assert.Same(enemy, target);
    }

    [Fact]
    public void Default_ASummonIsAimedAtTheEnemyNotTheCaster()
    {
        // pTarg stays the enemy and m_Act_p its position (CCharNPCAct_Magic.cpp:265-267).
        var (_, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.SummonDaemon);
        var (spell, target) = CastOnce(ai, caster, enemy);
        Assert.Equal(SpellType.SummonDaemon, spell);
        Assert.Same(enemy, target);
    }

    [Fact]
    public void Default_PlayerOnlySpellsNeverQualify()
    {
        var (_, ai, caster, enemy) = Duel();
        ai.ResolveNpcSpellFlags = _ => SpellFlag.Harm | SpellFlag.TargChar | SpellFlag.PlayerOnly;
        caster.NpcSpellAdd(SpellType.MagicArrow);
        Assert.Equal(SpellType.None, CastOnce(ai, caster, enemy, 50).Spell);
    }

    [Fact]
    public void Default_NpcActCastRef1RetargetsAndHealThresholdReadsBack()
    {
        var (world, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.Heal);
        var friend = world.CreateCharacter();
        friend.Hits = 80;
        friend.MaxHits = 100;
        world.PlaceCharacter(friend, new Point3D(101, 101, 0, 0));
        // REF1 = the friend, LOCAL.HealThreshold = 90: the friend at 80% now suits.
        ai.OnNpcActCast = (_, _, spell, _) =>
            new NpcAI.NpcCastDecision(false, spell, friend, Retargeted: true, HealThreshold: 90);

        var (cast, target) = CastOnce(ai, caster, enemy);

        Assert.Equal(SpellType.Heal, cast);
        Assert.Same(friend, target);
    }

    [Fact]
    public void Default_NpcActCastReturn1RevertsToMelee()
    {
        var (_, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.MagicArrow);
        ai.OnNpcActCast = (_, t, s, _) => new NpcAI.NpcCastDecision(true, s, t);
        Assert.Equal(SpellType.None, CastOnce(ai, caster, enemy, 50).Spell);
    }

    [Fact]
    public void Default_CastingRevealsTheCaster()
    {
        var (_, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.MagicArrow);
        caster.SetStatFlag(StatFlag.Hidden);
        Assert.Equal(SpellType.MagicArrow, CastOnce(ai, caster, enemy).Spell);
        Assert.False(caster.IsStatFlag(StatFlag.Hidden)); // Reveal(), CCharNPCAct_Magic.cpp:263
    }

    [Fact]
    public void Default_ACasterTooCloseStepsBackBeforeCasting()
    {
        // With mana to spare a caster nearer than 4 tiles moves to keep 5
        // (NPC_Act_Follow(false, 5, true), CCharNPCAct_Magic.cpp:255-259).
        var (_, ai, caster, enemy) = Duel(distance: 2);
        caster.NpcSpellAdd(SpellType.MagicArrow);
        int before = caster.Position.GetDistanceTo(enemy.Position);
        Assert.Equal(SpellType.MagicArrow, CastOnce(ai, caster, enemy).Spell);
        Assert.True(caster.Position.GetDistanceTo(enemy.Position) >= before);
    }

    [Fact]
    public void NoInventedLichSpellsAndNoSavedScanTag()
    {
        var (world, ai, caster, enemy) = Duel();
        caster.BodyId = 0x0018;
        AddPack(world, caster);
        Assert.Equal(SpellType.None, CastOnce(ai, caster, enemy, 20).Spell);
        Assert.Empty(caster.NpcSpells);
        Assert.False(caster.TryGetTag("SPELLS_LOADED", out _));
    }

    // ---- SmartCaster ----

    [Fact]
    public void SmartCaster_DispelsASummonedAttackerThatIsNotTheTarget()
    {
        var (world, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.Dispel);
        caster.NpcSpellAdd(SpellType.MagicArrow);
        var summon = world.CreateCharacter();
        summon.Hits = summon.MaxHits = 50;
        summon.SetTag("SUMMON_DURATION", "600");
        world.PlaceCharacter(summon, new Point3D(102, 100, 0, 0));
        caster.CombatState.AddAttacker(summon.Uid);

        var (spell, target) = ai.ChooseBestSpell(caster, enemy, 5);

        Assert.Equal(SpellType.Dispel, spell);
        Assert.Same(summon, target);
    }

    [Fact]
    public void SmartCaster_AnswersAnEnemyHealWithPoison()
    {
        var (_, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.Poison);
        caster.NpcSpellAdd(SpellType.MagicArrow);
        enemy.BeginCast(SpellType.GreaterHeal, enemy.Uid, enemy.Position);

        var (spell, target) = ai.ChooseBestSpell(caster, enemy, 5);

        Assert.Equal(SpellType.Poison, spell);
        Assert.Same(enemy, target);
    }

    [Fact]
    public void SmartCaster_RanksAnySchoolByFlagsAndMana()
    {
        var (_, ai, caster, enemy) = Duel();
        ai.ResolveNpcSpellFlags = _ => SpellFlag.Harm | SpellFlag.Damage | SpellFlag.TargChar;
        ai.ResolveNpcSpellMana = s => s == SpellType.PoisonStrike ? 30 : 10;
        caster.NpcSpellAdd(SpellType.PainSpike);
        caster.NpcSpellAdd(SpellType.PoisonStrike);

        var (spell, target) = ai.ChooseBestSpell(caster, enemy, 5);

        Assert.Equal(SpellType.PoisonStrike, spell); // strongest damage spell of a necro caster
        Assert.Same(enemy, target);
    }

    [Fact]
    public void SmartCaster_ParalyzeCooldownIsNotASavedTag()
    {
        var (_, ai, caster, enemy) = Duel(distance: 6);
        caster.NpcSpellAdd(SpellType.Paralyze);
        caster.Mana = 10; // below the combo start
        var (spell, _) = ai.ChooseBestSpell(caster, enemy, 6);
        Assert.Equal(SpellType.Paralyze, spell);
        Assert.False(caster.TryGetTag("PARA_CD", out _));
        var mem = Invoke(ai, "FightMemory", caster)!;
        Assert.True((long)mem.GetType().GetField("ParalyzeReadyAt")!.GetValue(mem)! > Environment.TickCount64);
    }

    [Fact]
    public void SmartCaster_OnlyWithItsBit()
    {
        // SmartCaster never aims a harmful spell at a reflecting enemy; the
        // Source-X walk does.
        var (_, ai, caster, enemy) = Duel();
        caster.NpcSpellAdd(SpellType.EnergyBolt);
        enemy.SetStatFlag(StatFlag.Reflection);
        ai.Extras |= NpcAiExtraFlags.SmartCaster;
        ai.ResolveNpcSpellFlags = s => s == SpellType.EnergyBolt
            ? SpellFlag.Harm | SpellFlag.Damage | SpellFlag.TargChar : null;
        // SmartCaster finds nothing for a reflecting target and falls back to the
        // Source-X walk, which does cast it.
        Assert.Equal(SpellType.EnergyBolt, CastOnce(ai, caster, enemy).Spell);
        var (choice, _) = ai.ChooseBestSpell(caster, enemy, 5);
        Assert.Equal(SpellType.None, choice);
    }

    // ---- Fight: motivation, flee, breath/throw, extras ----

    [Fact]
    public void AMonsterKeepsFightingAtMotivationZero()
    {
        var (world, ai, npc, enemy) = Duel(distance: 3);
        npc.SetStatFlag(StatFlag.Hidden); // hidden melee ambusher at range: motivation 0
        npc.FightTarget = enemy.Uid;
        Invoke(ai, "ActMonster", npc);
        Assert.Equal(enemy.Uid, npc.FightTarget);
    }

    [Fact]
    public void AFleeThatCannotStartLeavesTheFight()
    {
        // CCharNPCAct_Fight.cpp:268-276: war off, attacker deleted, no target.
        var (_, ai, npc, enemy) = Duel(distance: 3);
        npc.SetStatFlag(StatFlag.War);
        npc.FightTarget = enemy.Uid;
        npc.CombatState.AddAttacker(enemy.Uid);
        enemy.SetStatFlag(StatFlag.Hidden);

        Invoke(ai, "ActFight", npc, enemy, -50);

        Assert.False(npc.IsStatFlag(StatFlag.War));
        Assert.False(npc.FightTarget.IsValid);
        Assert.Equal(-1, npc.Attacker_GetIndex(enemy.Uid));
    }

    [Fact]
    public void AFleeRunsItsStepsWithoutRestarting()
    {
        var (_, ai, npc, enemy) = Duel(distance: 2);
        npc.FightTarget = enemy.Uid;
        Invoke(ai, "ActFight", npc, enemy, -50);
        Assert.Equal(1, npc.FleeStepsCurrent);
        Invoke(ai, "ActFight", npc, enemy, -50);
        Assert.Equal(2, npc.FleeStepsCurrent); // continued, not reset to 0
    }

    [Fact]
    public void ADefaultRockThrowerNeedsARockAndAThrowObjMustBeCarried()
    {
        var (world, ai, npc, enemy) = Duel(distance: 4);
        int throws = 0;
        ai.OnNpcThrow = (_, _, _) => throws++;
        npc.SetTag("THROWOBJ", "0x1363");
        var pack = AddPack(world, npc);

        Invoke(ai, "ActFight", npc, enemy, 100);
        Assert.Equal(0, throws); // tag without the missile

        var rock = world.CreateItem();
        rock.BaseId = 0x1363;
        pack.AddItem(rock);
        npc.Stam = npc.MaxStam;
        Invoke(ai, "ActFight", npc, enemy, 100);
        Assert.Equal(1, throws);
    }

    [Fact]
    public void LegacyRuntimeTagsAreDroppedWhenTheNpcFights()
    {
        var (_, ai, npc, enemy) = Duel(distance: 1);
        npc.SetTag("BREATH_CD", "123");
        npc.SetTag("PARA_CD", "123");
        npc.SetTag("COMBO_STEP", "2");
        npc.SetTag("LAST_TGT_LOC", "1,2,3,0");
        npc.SetTag("HIDE_PURSUIT", "3");
        Invoke(ai, "ActFight", npc, enemy, 100);
        Assert.False(npc.TryGetTag("BREATH_CD", out _));
        Assert.False(npc.TryGetTag("PARA_CD", out _));
        Assert.False(npc.TryGetTag("COMBO_STEP", out _));
        Assert.False(npc.TryGetTag("LAST_TGT_LOC", out _));
        Assert.False(npc.TryGetTag("HIDE_PURSUIT", out _));
    }

    [Fact]
    public void AllyRally_OnlyWithItsBit()
    {
        foreach (bool on in new[] { false, true })
        {
            var (world, ai, npc, enemy) = Duel(distance: 3);
            npc.BodyId = 0x11;
            npc.Karma = -1; // an evil monster hunts (Noto_IsEvil, CCharNotoriety.cpp:53)
            enemy.IsOnline = true;
            world.AddOnlinePlayer(enemy);
            var ally = world.CreateCharacter();
            ally.NpcBrain = NpcBrainType.Monster;
            ally.BodyId = 0x11;
            ally.Karma = -1;
            ally.Hits = ally.MaxHits = 100;
            world.PlaceCharacter(ally, new Point3D(99, 100, 0, 0));
            world.OnTick();
            if (on) ai.Extras |= NpcAiExtraFlags.AllyRally;

            Invoke(ai, "ActMonster", npc);

            Assert.Equal(enemy.Uid, npc.FightTarget);
            Assert.Equal(on, ally.FightTarget == enemy.Uid);
        }
    }

    [Fact]
    public void ReacquireThrottle_OnlyWithFightRescan()
    {
        var (_, ai, npc, _) = Duel(distance: 60);
        npc.NextNpcReacquireTime = 0;
        // Nobody within sight: the scan finds nothing.
        Invoke(ai, "ActMonster", npc);
        Assert.Equal(0, npc.NextNpcReacquireTime);

        ai.Extras |= NpcAiExtraFlags.FightRescan;
        Invoke(ai, "ActMonster", npc);
        Assert.True(npc.NextNpcReacquireTime > 0);
    }

    [Fact]
    public void LowHpRetreat_OnlyWithItsBit()
    {
        var (_, ai, npc, enemy) = Duel(distance: 1);
        npc.Hits = 10;
        Point3D start = npc.Position;
        for (int i = 0; i < 30; i++)
        {
            npc.NextAttackTime = long.MaxValue; // no swing, isolate the retreat
            Invoke(ai, "ActFight", npc, enemy, 100);
        }
        Assert.Equal(start, npc.Position);

        ai.Extras |= NpcAiExtraFlags.LowHpRetreat;
        for (int i = 0; i < 60 && npc.Position == start; i++)
        {
            npc.NextAttackTime = long.MaxValue;
            Invoke(ai, "ActFight", npc, enemy, 100);
        }
        Assert.NotEqual(start, npc.Position);
    }

    [Fact]
    public void LosRecovery_SwitchesToAVisibleAttacker()
    {
        var (world, ai, npc, enemy) = Duel(distance: 5);
        var other = world.CreateCharacter();
        other.IsPlayer = true;
        other.Hits = other.MaxHits = 100;
        world.PlaceCharacter(other, new Point3D(102, 100, 0, 0));
        npc.CombatState.AddAttacker(other.Uid);
        npc.FightTarget = enemy.Uid;

        bool spent = (bool)Invoke(ai, "TryLosRecovery", npc, enemy)!;

        Assert.True(spent);
        Assert.Equal(other.Uid, npc.FightTarget);
    }

    [Fact]
    public void SurroundTileReservation_KeepsASecondAttackerOffTheSameTile()
    {
        var (world, ai, a, _) = Duel();
        var b = world.CreateCharacter();
        world.PlaceCharacter(b, new Point3D(90, 90, 0, 0));
        Invoke(ai, "ReserveTile", a, (short)110, (short)110);
        Assert.True((bool)Invoke(ai, "IsTileReservedByOther", b, (short)110, (short)110)!);
        Assert.False((bool)Invoke(ai, "IsTileReservedByOther", a, (short)110, (short)110)!);
    }

    [Fact]
    public void WeaponUseScore_PicksTheBetterPackWeapon()
    {
        var (world, ai, npc, _) = Duel();
        npc.SetSkill(SkillType.Swordsmanship, 800);
        var pack = AddPack(world, npc);
        var junk = world.CreateItem();
        junk.ItemType = ItemType.Normal;
        pack.AddItem(junk);
        Assert.Null(ai.FindBestPackWeapon(npc)); // nothing beats bare hands

        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        pack.AddItem(sword);
        Assert.Same(sword, ai.FindBestPackWeapon(npc));
    }

    [Fact]
    public void BandageHeal_OnlyWithItsBitAndBandages()
    {
        var (world, ai, npc, enemy) = Duel(distance: 1);
        npc.SetSkill(SkillType.Healing, 800);
        npc.Hits = 50;
        var pack = AddPack(world, npc);
        var bandage = world.CreateItem();
        bandage.ItemType = ItemType.Bandage;
        bandage.Amount = 5;
        pack.AddItem(bandage);
        int started = 0;
        ai.OnNpcBandage = (_, patient, b) => { Assert.Same(npc, patient); Assert.Same(bandage, b); started++; return true; };

        npc.NextAttackTime = long.MaxValue;
        Invoke(ai, "ActFight", npc, enemy, 100);
        Assert.Equal(0, started);

        ai.Extras |= NpcAiExtraFlags.BandageHeal;
        Invoke(ai, "ActFight", npc, enemy, 100);
        Assert.Equal(1, started);
        Invoke(ai, "ActFight", npc, enemy, 100);
        Assert.Equal(1, started); // busy while the treatment runs

        npc.Hits = 90; // above 78%, not poisoned: TryBandage declines
        Assert.False(ai.TryBandage(npc, npc));
    }

    [Fact]
    public void BandageHeal_PetTreatsItsOwner()
    {
        var world = TestHarness.CreateWorld();
        var ai = new NpcAI(world, new SphereConfig());
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Hits = 40; owner.MaxHits = 100;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pet = world.CreateCharacter();
        pet.NpcMaster = owner.Uid;
        pet.Hits = pet.MaxHits = 50;
        pet.PetAIMode = PetAIMode.Follow;
        pet.SetSkill(SkillType.Healing, 800);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));
        var pack = AddPack(world, pet);
        var bandage = world.CreateItem();
        bandage.ItemType = ItemType.Bandage;
        pack.AddItem(bandage);
        Character? treated = null;
        ai.OnNpcBandage = (_, patient, _) => { treated = patient; return true; };

        pet.NextNpcActionTime = 0;
        ai.OnTickAction(pet);
        Assert.Null(treated); // off without its bit

        ai.Extras |= NpcAiExtraFlags.BandageHeal;
        pet.NextNpcActionTime = 0;
        ai.OnTickAction(pet);
        Assert.Same(owner, treated);
    }
}
