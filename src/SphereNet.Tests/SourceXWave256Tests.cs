using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 256 — Necromancy damage-reaction tranche: Blood Oath (a bonded victim
/// reflects its linked enemy's blows) and Evil Omen (the next harmful effect
/// lands harder, then is consumed). Both act on the victim-side of a hit, wired
/// into the melee damage path and the poison-spell application.
/// </summary>
public sealed class SourceXWave256Tests
{
    private static (GameWorld world, Character attacker, Character target, Item sword) MeleeSetup()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.IsPlayer = true;
        attacker.PrivLevel = PrivLevel.GM; // era-0 roll: always hits
        attacker.Str = 50; attacker.Dex = 50; attacker.Int = 50;
        attacker.MaxHits = 100; attacker.Hits = 100;
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.MaxHits = 100; target.Hits = 100;
        target.SetSkill(SkillType.Parrying, 0);
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var sword = world.CreateItem();
        sword.ItemType = ItemType.WeaponSword;
        sword.BaseId = 0x0F5E;
        attacker.Equip(sword, Layer.OneHanded);
        return (world, attacker, target, sword);
    }

    // ---------- Blood Oath ----------

    [Fact]
    public void BloodOath_ReflectsLinkedEnemysBlow()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null;
            CombatEngine.WeaponDefLookup = _ => (10, 10); // deterministic 10 damage

            var (_, attacker, target, sword) = MeleeSetup();
            target.BloodOathEnemy = attacker.Uid;
            target.BloodOathLevel = 50;

            int dmg = CombatEngine.ResolveAttack(attacker, target, sword);

            Assert.Equal(10, dmg);
            Assert.Equal(89, target.Hits);   // 100 - 10 hit - 1 (extra 10/10)
            Assert.Equal(95, attacker.Hits); // 100 - 5 reflect (10 * (100-50)/100)
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    [Fact]
    public void BloodOath_DoesNotReflectAnUnlinkedAttacker()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null;
            CombatEngine.WeaponDefLookup = _ => (10, 10);

            var (_, attacker, target, sword) = MeleeSetup();
            target.BloodOathEnemy = new Serial(0x00ABCDEF); // someone else
            target.BloodOathLevel = 50;

            int dmg = CombatEngine.ResolveAttack(attacker, target, sword);

            Assert.Equal(10, dmg);
            Assert.Equal(90, target.Hits);    // plain 10 damage, no extra
            Assert.Equal(100, attacker.Hits); // no reflect
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    [Fact]
    public void BloodOath_Cast_BondsCasterToEnemy_ExpiryClears()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.BloodOath,
            Flags = SpellFlag.TargChar | SpellFlag.Good,
            DurationBase = 600, DurationScale = 600,
        });
        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var enemy = world.CreateCharacter();
        enemy.SetSkill(SkillType.MagicResistance, 0); // level = 0/20 + 10 = 10
        world.PlaceCharacter(enemy, new Point3D(101, 100, 0, 0));

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.BloodOath, enemy.Uid, enemy.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(enemy.Uid, caster.BloodOathEnemy);
        Assert.Equal(10, caster.BloodOathLevel);

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.False(caster.BloodOathEnemy.IsValid);
        Assert.Equal(0, caster.BloodOathLevel);
    }

    // ---------- Evil Omen ----------

    [Fact]
    public void EvilOmen_AmplifiesNextMeleeHitByaQuarter_ThenConsumed()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null;
            CombatEngine.WeaponDefLookup = _ => (10, 10);

            var (_, attacker, target, sword) = MeleeSetup();
            target.EvilOmenActive = true;
            target.EvilOmenExpireTick = Environment.TickCount64 + 60_000;

            int dmg = CombatEngine.ResolveAttack(attacker, target, sword);

            Assert.Equal(88, target.Hits);        // 100 - (10 + 10/4)
            Assert.False(target.EvilOmenActive);  // consumed

            // A second hit is no longer amplified.
            CombatEngine.ResolveAttack(attacker, target, sword);
            Assert.Equal(78, target.Hits);        // 88 - 10
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    [Fact]
    public void ConsumeEvilOmen_ExpiredMarkerReturnsFalseAndClears()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.EvilOmenActive = true;
        ch.EvilOmenExpireTick = Environment.TickCount64 - 1000; // already expired

        Assert.False(ch.ConsumeEvilOmen());
        Assert.False(ch.EvilOmenActive);
    }

    [Fact]
    public void EvilOmen_PoisonSpellLandsOneLevelHigher()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Poison,
            Flags = SpellFlag.TargChar,
            ManaCost = 0, CastTimeBase = 1,
        });
        var engine = new SpellEngine(world, registry);

        Character CastPoisonOn(bool withOmen)
        {
            var caster = world.CreateCharacter();
            caster.PrivLevel = PrivLevel.GM;
            caster.MaxMana = 100; caster.Mana = 100;
            caster.SetSkill(SkillType.Magery, 800); // base deadly (level 4)
            world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
            var victim = world.CreateCharacter();
            victim.MaxHits = 100; victim.Hits = 100;
            world.PlaceCharacter(victim, new Point3D(101, 100, 0, 0));
            if (withOmen)
            {
                victim.EvilOmenActive = true;
                victim.EvilOmenExpireTick = Environment.TickCount64 + 60_000;
            }
            Assert.True(engine.CastStart(caster, SpellType.Poison, victim.Uid, victim.Position) >= 0);
            Assert.True(engine.CastDone(caster));
            return victim;
        }

        Assert.Equal(4, CastPoisonOn(withOmen: false).PoisonLevel);
        var omened = CastPoisonOn(withOmen: true);
        Assert.Equal(5, omened.PoisonLevel);       // 4 + 1
        Assert.False(omened.EvilOmenActive);        // consumed
    }
}
