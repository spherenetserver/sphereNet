using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Game.World.Regions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// The shared character-damage entry, checked against Source-X CChar::OnTakeDamage
/// (CCharFight.cpp:633-1062) and its callers: the damage-flag numbering
/// (game_macros.h:55-74), the protection gates and aggressor memory, the order of
/// the Hit/GetHit stages around armour, the ARGN2 hand-over, spell damage through
/// @GetHit, and Blood Oath's raw-damage reflection. Every expected number below is
/// worked out from the reference, not from the engine.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CombatDamageEntrySourceXTests
{
    private sealed class Console : SphereNet.Core.Interfaces.ITextConsole
    {
        public string GetName() => "test";
        public PrivLevel GetPrivLevel() => PrivLevel.Admin;
        public void SysMessage(string text) { }
    }

    private static Character Place(GameWorld world, short x, short hits = 100, bool player = false)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = player;
        ch.Str = 50; ch.Dex = 50; ch.Int = 50;
        ch.MaxHits = hits; ch.Hits = hits;
        world.PlaceCharacter(ch, new Point3D(x, 100, 0, 0));
        return ch;
    }

    private static string Uid(Character ch) => "0" + ch.Uid.Value.ToString("X");

    private static void AddRegion(GameWorld world, RegionFlag flags)
    {
        var region = new Region { Name = "dmg_" + flags, Flags = flags, MapIndex = 0 };
        region.AddRect(90, 90, 110, 110);
        world.AddRegion(region);
    }

    private static string WriteScript(string body)
    {
        string file = Path.Combine(Path.GetTempPath(), $"spherenet_dmgentry_{Guid.NewGuid():N}.scp");
        File.WriteAllText(file, body);
        return file;
    }

    // ---- B1: the Source-X DAMAGE_TYPE numbering, 32 bits ----

    [Fact]
    public void DamageTypeValuesAreTheReferenceNumbers()
    {
        // game_macros.h:55-74
        Assert.Equal(0x0001u, (uint)DamageType.God);
        Assert.Equal(0x0002u, (uint)DamageType.HitBlunt);
        Assert.Equal(0x0004u, (uint)DamageType.Magic);
        Assert.Equal(0x0008u, (uint)DamageType.Poison);
        Assert.Equal(0x0010u, (uint)DamageType.Fire);
        Assert.Equal(0x0020u, (uint)DamageType.Energy);
        Assert.Equal(0x0080u, (uint)DamageType.General);
        Assert.Equal(0x0100u, (uint)DamageType.Acidic);
        Assert.Equal(0x0200u, (uint)DamageType.Cold);
        Assert.Equal(0x0400u, (uint)DamageType.HitSlash);
        Assert.Equal(0x0800u, (uint)DamageType.HitPierce);
        Assert.Equal(0x2000u, (uint)DamageType.NoDisturb);
        Assert.Equal(0x4000u, (uint)DamageType.NoReveal);
        Assert.Equal(0x8000u, (uint)DamageType.NoUnparalyze);
        Assert.Equal(0x10000u, (uint)DamageType.Fixed);
        Assert.Equal(0x20000u, (uint)DamageType.Breath);
        Assert.Equal(0x40000u, (uint)DamageType.Thrown);
        Assert.Equal(0x80000u, (uint)DamageType.Reactive);
    }

    [Fact]
    public void DamageVerbGodFlagIsOneAndPassesInvulnerability()
    {
        // DAMAGE 20,01: 01 is DAMAGE_GOD, which the invulnerable bounce does not stop
        // (CCharFight.cpp:640-642).
        var world = TestHarness.CreateWorld();
        var target = Place(world, 100);
        target.SetStatFlag(StatFlag.Invul);

        Assert.True(target.TryExecuteCommand("DAMAGE", "20,01", new Console()));
        Assert.Equal(80, target.Hits);
    }

    [Fact]
    public void DamageVerbColdIsTwoHundredAndIsBounced()
    {
        // DAMAGE 20,0200: 0x200 is DAMAGE_COLD, an ordinary blow for an invulnerable target.
        var world = TestHarness.CreateWorld();
        var target = Place(world, 100);
        target.SetStatFlag(StatFlag.Invul);

        Assert.True(target.TryExecuteCommand("DAMAGE", "20,0200", new Console()));
        Assert.Equal(100, target.Hits);
    }

    [Fact]
    public void DamageVerbFixedIsTheSeventeenthBitAndSkipsResist()
    {
        // 010000 = DAMAGE_FIXED: no armour calculation at all (CCharFight.cpp:716).
        // A 16-bit type lost the bit and cut the blow by the 50% resist.
        Character.CombatFlags = (int)CombatFlags.ElementalEngine;
        var world = TestHarness.CreateWorld();
        var target = Place(world, 100);
        target.ResPhysical = 50;

        Assert.True(target.TryExecuteCommand("DAMAGE", "20,010000,0,100,0,0,0,0", new Console()));
        Assert.Equal(80, target.Hits);
    }

    // ---- B2: protection gates, aggressor memory, unparalyze ----

    [Fact]
    public void ASafeRegionBouncesDirectDamage()
    {
        var world = TestHarness.CreateWorld();
        AddRegion(world, RegionFlag.Safe);
        var source = Place(world, 100, player: true);
        var target = Place(world, 101);

        Assert.Equal(0, CombatEngine.ApplyScriptDamage(target, 20, DamageType.HitBlunt, source));
        Assert.Equal(100, target.Hits);
        // DAMAGE_GOD is never bounced (CCharFight.cpp:640).
        Assert.Equal(20, CombatEngine.ApplyScriptDamage(target, 20, DamageType.God, source));
    }

    [Fact]
    public void ANoPvpRegionBouncesPlayersAndTheirPetsButNotMonsters()
    {
        // CCharFight.cpp:662-675: a player victim in a NO_PVP region is protected from
        // a player and from a pet whose top owner is a player.
        var world = TestHarness.CreateWorld();
        AddRegion(world, RegionFlag.NoPvP);
        var victim = Place(world, 100, player: true);
        var player = Place(world, 101, player: true);
        var pet = Place(world, 102);
        pet.NpcMaster = player.Uid;
        var petOfPet = Place(world, 103);
        petOfPet.NpcMaster = pet.Uid;
        var monster = Place(world, 104);

        Assert.Equal(0, CombatEngine.ApplyScriptDamage(victim, 20, DamageType.HitBlunt, player));
        Assert.Equal(0, CombatEngine.ApplyScriptDamage(victim, 20, DamageType.HitBlunt, pet));
        Assert.Equal(0, CombatEngine.ApplyScriptDamage(victim, 20, DamageType.HitBlunt, petOfPet));
        Assert.Equal(100, victim.Hits);

        Assert.Equal(20, CombatEngine.ApplyScriptDamage(victim, 20, DamageType.HitBlunt, monster));
        Assert.Equal(20, CombatEngine.ApplyScriptDamage(victim, 20, DamageType.God, player));
        Assert.Equal(60, victim.Hits);

        // An NPC victim is not covered by NO_PVP.
        var npcVictim = Place(world, 105);
        Assert.Equal(20, CombatEngine.ApplyScriptDamage(npcVictim, 20, DamageType.HitBlunt, player));
    }

    [Fact]
    public void AStoneTargetTakesNoDamage()
    {
        var world = TestHarness.CreateWorld();
        var target = Place(world, 100);
        target.SetStatFlag(StatFlag.Stone);

        Assert.Equal(0, CombatEngine.ApplyScriptDamage(target, 20, DamageType.HitBlunt));
        Assert.Equal(100, target.Hits);
    }

    [Fact]
    public void DamageUnfreezesUnlessItCarriesNoUnparalyze()
    {
        // CCharFight.cpp:797-818.
        var world = TestHarness.CreateWorld();
        var target = Place(world, 100);

        target.SetStatFlag(StatFlag.Freeze);
        CombatEngine.ApplyScriptDamage(target, 10, DamageType.HitBlunt | DamageType.NoUnparalyze);
        Assert.True(target.IsStatFlag(StatFlag.Freeze));

        CombatEngine.ApplyScriptDamage(target, 10, DamageType.HitBlunt);
        Assert.False(target.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void DirectDamageMakesTheVictimRememberTheAggressor()
    {
        // OnTakeDamage -> OnAttackedBy (CCharFight.cpp:681): the victim is AGGREIVED at
        // whoever struck first, which is its personal right to fight back.
        var world = TestHarness.CreateWorld();
        var attacker = Place(world, 100, player: true);
        var victim = Place(world, 101, player: true);

        CombatEngine.ApplyScriptDamage(victim, 5, DamageType.HitBlunt, attacker);

        Assert.NotNull(victim.Memory_FindObjTypes(attacker.Uid, MemoryType.Aggreived));
    }

    [Fact]
    public void TheAttackerIsRevealedUnlessTheBlowIsNoReveal()
    {
        // OnAttackedBy(pSrc, false, !(uType & DAMAGE_NOREVEAL)) (CCharFight.cpp:681, :339).
        var world = TestHarness.CreateWorld();
        var attacker = Place(world, 100, player: true);
        var victim = Place(world, 101);

        attacker.SetStatFlag(StatFlag.Hidden);
        CombatEngine.ApplyScriptDamage(victim, 5, DamageType.HitBlunt | DamageType.NoReveal, attacker);
        Assert.True(attacker.IsStatFlag(StatFlag.Hidden));

        CombatEngine.ApplyScriptDamage(victim, 5, DamageType.HitBlunt, attacker);
        Assert.False(attacker.IsStatFlag(StatFlag.Hidden));
    }

    // ---- B3: Hit on the raw blow, GetHit on the reduced one, never re-reduced ----

    [Fact]
    public void SwingRunsAttackerAndWeaponHitOnRawDamageAndGetHitOnReducedDamage()
    {
        // Raw 20 (the attacker's @Hit pins it; the weapon def gives 20 plus the
        // attacker's own damage bonus), physical resist 50, weapon @Hit ARGN1=40,
        // victim @GetHit ARGN1=30. Source-X: attacker @Hit then weapon @Hit see the raw
        // blow (CCharFight.cpp:2178-2195); the victim's @GetHit sees the 40 cut to 20
        // (:717-773); its 30 is final.
        string file = WriteScript("""
            [EVENTS e_dmg_attacker]
            ON=@Hit
            TAG.HITSEEN=<ARGN1>
            ARGN1=20

            [EVENTS e_dmg_weapon]
            ON=@Hit
            TAG.WSEEN=<ARGN1>
            ARGN1=40

            [EVENTS e_dmg_victim]
            ON=@GetHit
            TAG.GETHITSEEN=<ARGN1>
            ARGN1=30
            """);
        var savedLookup = CombatEngine.WeaponDefLookup;
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(file);
            CombatEngine.OnHitDamage = ctx => stack.Dispatcher.RunHitDamageTriggers(ctx);
            CombatEngine.OnGetHit = ctx => stack.Dispatcher.RunGetHitTriggers(ctx);
            CombatEngine.WeaponDefLookup = _ => (20, 20);
            Character.CombatFlags = (int)CombatFlags.ElementalEngine;

            var world = TestHarness.CreateWorld();
            var attacker = Place(world, 100, player: true);
            attacker.PrivLevel = PrivLevel.GM; // always lands
            var target = Place(world, 101);
            target.ResPhysical = 50;
            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            attacker.Equip(sword, Layer.OneHanded);
            attacker.Events.Add(stack.Resources.ResolveDefName("e_dmg_attacker"));
            sword.Events.Add(stack.Resources.ResolveDefName("e_dmg_weapon"));
            target.Events.Add(stack.Resources.ResolveDefName("e_dmg_victim"));

            int dealt = CombatEngine.ResolveAttack(attacker, target, sword,
                (CombatFlags)Character.CombatFlags);

            attacker.TryGetTag("HITSEEN", out var hit);
            sword.TryGetTag("WSEEN", out var w);
            target.TryGetTag("GETHITSEEN", out var gh);
            // Unreduced: the 50% resist would have left 10 or 11.
            Assert.True(int.Parse(hit!) >= 20, hit);
            Assert.Equal("20", w);
            Assert.Equal("20", gh);
            Assert.Equal(30, dealt);
            Assert.Equal(70, target.Hits);
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            File.Delete(file);
        }
    }

    [Fact]
    public void DirectDamageGetHitSeesTheReducedBlowAndItsChangeIsNotReducedAgain()
    {
        // 40 raw, 50% resist: @GetHit sees 20; adding 10 gives 30, applied as is.
        string file = WriteScript("""
            [EVENTS e_dmg_direct]
            ON=@GetHit
            TAG.DSEEN=<ARGN1>
            ARGN1=<EVAL <ARGN1>+10>
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(file);
            CombatEngine.OnGetHit = ctx => stack.Dispatcher.RunGetHitTriggers(ctx);
            Character.CombatFlags = (int)CombatFlags.ElementalEngine;

            var world = TestHarness.CreateWorld();
            var target = Place(world, 100);
            target.ResPhysical = 50;
            target.Events.Add(stack.Resources.ResolveDefName("e_dmg_direct"));

            Assert.Equal(30, CombatEngine.ApplyScriptDamage(target, 40, DamageType.HitBlunt));
            Assert.True(target.TryGetTag("DSEEN", out var seen) && seen == "20");
            Assert.Equal(70, target.Hits);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void WithoutTheElementalEngineDirectDamageUsesArmourNotResists()
    {
        // COMBAT_ELEMENTAL_ENGINE off: the pre-AOS armour roll (CCharFight.cpp:733),
        // which is 0 here; the physical resist plays no part.
        Character.CombatFlags = 0;
        var world = TestHarness.CreateWorld();
        var target = Place(world, 100);
        target.ResPhysical = 50;

        Assert.Equal(40, CombatEngine.ApplyScriptDamage(target, 40, DamageType.HitBlunt));
        Assert.Equal(60, target.Hits);
    }

    // ---- B4: ARGN2 is carried from stage to stage ----

    [Fact]
    public void AnArgn2WrittenInHitReachesGetHitAndDecidesTheBlow()
    {
        // The attacker's @Hit makes the blow DAMAGE_GOD (ARGN2=01, CCharFight.cpp:2187):
        // the victim's @GetHit sees 1, and the victim's 50% resist is skipped (:716).
        string file = WriteScript("""
            [EVENTS e_n2_attacker]
            ON=@Hit
            ARGN1=20
            ARGN2=01

            [EVENTS e_n2_victim]
            ON=@GetHit
            TAG.N2SEEN=<ARGN2>
            """);
        var savedLookup = CombatEngine.WeaponDefLookup;
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(file);
            CombatEngine.OnHitDamage = ctx => stack.Dispatcher.RunHitDamageTriggers(ctx);
            CombatEngine.OnGetHit = ctx => stack.Dispatcher.RunGetHitTriggers(ctx);
            CombatEngine.WeaponDefLookup = _ => (20, 20);
            Character.CombatFlags = (int)CombatFlags.ElementalEngine;

            var world = TestHarness.CreateWorld();
            var attacker = Place(world, 100, player: true);
            attacker.PrivLevel = PrivLevel.GM;
            var target = Place(world, 101);
            target.ResPhysical = 50;
            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            attacker.Equip(sword, Layer.OneHanded);
            attacker.Events.Add(stack.Resources.ResolveDefName("e_n2_attacker"));
            target.Events.Add(stack.Resources.ResolveDefName("e_n2_victim"));

            CombatEngine.ResolveAttack(attacker, target, sword, (CombatFlags)Character.CombatFlags);

            Assert.True(target.TryGetTag("N2SEEN", out var n2) && n2 == "1");
            Assert.Equal(80, target.Hits);
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            File.Delete(file);
        }
    }

    [Fact]
    public void AnArgn2WrittenInGetHitDecidesTheUnparalyze()
    {
        // @GetHit's ARGN2 is read back (CCharFight.cpp:774) before the unparalyze
        // test (:798): adding DAMAGE_NOUNPARALYZE (08000) keeps the victim frozen.
        string file = WriteScript("""
            [EVENTS e_n2_keepfrozen]
            ON=@GetHit
            ARGN2=<EVAL <ARGN2>|08000>
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(file);
            CombatEngine.OnGetHit = ctx => stack.Dispatcher.RunGetHitTriggers(ctx);

            var world = TestHarness.CreateWorld();
            var target = Place(world, 100);
            target.Events.Add(stack.Resources.ResolveDefName("e_n2_keepfrozen"));
            target.SetStatFlag(StatFlag.Freeze);

            Assert.Equal(10, CombatEngine.ApplyScriptDamage(target, 10, DamageType.HitBlunt));
            Assert.True(target.IsStatFlag(StatFlag.Freeze));
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- B5: spell damage goes through @GetHit ----

    [Fact]
    public void SpellDamageRunsTheVictimsGetHitWhichCanRefuseIt()
    {
        // OnSpellEffect hands the damage to OnTakeDamage (CCharSpell.cpp:3870), whose
        // @GetHit RETURN 1 refuses it (CCharFight.cpp:771).
        string file = WriteScript("""
            [EVENTS e_spell_gethit]
            ON=@GetHit
            TAG.SPELLGETHIT=<ARGN1>
            TAG.SPELLNUM=<LOCAL.Spell>
            RETURN 1
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(file);
            CombatEngine.OnGetHit = ctx => stack.Dispatcher.RunGetHitTriggers(ctx);

            var world = TestHarness.CreateWorld();
            var registry = new SpellRegistry();
            registry.Register(new SpellDef
            {
                Id = SpellType.Fireball,
                Flags = SpellFlag.Damage,
                EffectBase = 40,
                EffectScale = 40,
            });
            var engine = new SpellEngine(world, registry) { TriggerDispatcher = stack.Dispatcher };
            var caster = Place(world, 100, hits: 200, player: true);
            var target = Place(world, 101, hits: 200);

            // Without a script the fireball lands in full.
            engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);
            Assert.Equal(160, target.Hits);

            target.Events.Add(stack.Resources.ResolveDefName("e_spell_gethit"));
            engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);

            Assert.True(target.TryGetTag("SPELLGETHIT", out var seen) && seen == "40");
            Assert.True(target.TryGetTag("SPELLNUM", out var num) && num == ((int)SpellType.Fireball).ToString());
            Assert.Equal(160, target.Hits);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- B8: Blood Oath reflects the raised RAW blow, through the damage entry ----

    [Fact]
    public void BloodOathReflectsTheRaisedRawBlowBeforeTheVictimsArmour()
    {
        // 40 raw, 50% physical resist, oath level 50 (CCharFight.cpp:697-701): the blow
        // becomes 44; the attacker takes 44 * (100 - 50) / 100 = 22 as MAGIC|FIXED; the
        // victim takes 44 cut to 22 by its resist.
        Character.CombatFlags = (int)CombatFlags.ElementalEngine;
        var world = TestHarness.CreateWorld();
        var attacker = Place(world, 100, player: true);
        var defender = Place(world, 101, hits: 200);
        defender.ResPhysical = 50;
        attacker.ResPhysical = 50; // a FIXED reflection ignores the attacker's resist
        defender.BloodOathEnemy = attacker.Uid;
        defender.BloodOathLevel = 50;

        Assert.Equal(22, CombatEngine.ApplyScriptDamage(defender, 40, DamageType.HitBlunt, attacker));
        Assert.Equal(178, defender.Hits);
        Assert.Equal(78, attacker.Hits);
    }

    [Fact]
    public void BloodOathAnswersAnyNonFixedBlowButNotAFixedOne()
    {
        // The only type gate is DAMAGE_FIXED (a reflection already), CCharFight.cpp:697.
        var world = TestHarness.CreateWorld();
        var attacker = Place(world, 100, player: true);
        var defender = Place(world, 101, hits: 200);
        defender.BloodOathEnemy = attacker.Uid;
        defender.BloodOathLevel = 50;

        Assert.Equal(40, CombatEngine.ApplyScriptDamage(defender, 40, DamageType.Fixed, attacker));
        Assert.Equal(100, attacker.Hits);

        Assert.Equal(44, CombatEngine.ApplyScriptDamage(defender, 40, DamageType.Magic | DamageType.Fire, attacker));
        Assert.Equal(78, attacker.Hits);
    }

    [Fact]
    public void TheBloodOathReflectionRunsTheAttackersGetHit()
    {
        // The reflection is pSrc->OnTakeDamage (CCharFight.cpp:701): the attacker's own
        // @GetHit sees it and may refuse it.
        var world = TestHarness.CreateWorld();
        var attacker = Place(world, 100, player: true);
        var defender = Place(world, 101, hits: 200);
        defender.BloodOathEnemy = attacker.Uid;
        defender.BloodOathLevel = 50;
        DamageType seenType = DamageType.None;
        CombatEngine.OnGetHit = ctx =>
        {
            if (ctx.Target != attacker)
                return ctx.Damage;
            seenType = ctx.DamageType;
            ctx.Cancelled = true;
            return 0;
        };

        CombatEngine.ApplyScriptDamage(defender, 40, DamageType.HitBlunt, attacker);

        Assert.Equal(DamageType.Magic | DamageType.Fixed, seenType);
        Assert.Equal(100, attacker.Hits);
        Assert.Equal(156, defender.Hits);
    }
}
