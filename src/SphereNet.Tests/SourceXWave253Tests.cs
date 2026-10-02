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
/// Wave 253 — Necromancy foundation: Curse Weapon (life-leech add) and Wraith
/// Form (mana drain) states + their two combat hooks (reference
/// CCharFight.cpp:2272-2275 and 2299-2304). The combat hooks read a transient
/// state that the spell — or a script/GM — sets, decoupled from the cast path.
/// </summary>
public sealed class SourceXWave253Tests
{
    private static (GameWorld world, Character attacker, Character target) MakeCombatants()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.MaxMana = 50; attacker.Mana = 0;
        attacker.MaxHits = 100; attacker.Hits = 50;
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.MaxMana = 50; target.Mana = 50;
        target.MaxHits = 100; target.Hits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (world, attacker, target);
    }

    // ---------- Wraith Form mana drain (deterministic) ----------

    [Fact]
    public void WraithForm_DrainsTargetMana_ScaledBySpiritSpeak()
    {
        var (_, attacker, target) = MakeCombatants();
        attacker.WraithFormActive = true;
        attacker.SetSkill(SkillType.SpiritSpeak, 1000); // 5 + 15*1000/1000 = 20

        CombatEngine.ApplyAosOnHitEffects(attacker, target, damage: 100, weapon: null, flags: default);

        Assert.Equal(30, target.Mana);   // 50 - 20
        Assert.Equal(20, attacker.Mana); // 0 + 20
    }

    [Fact]
    public void WraithForm_Inactive_NoManaDrain()
    {
        var (_, attacker, target) = MakeCombatants();
        attacker.WraithFormActive = false;

        CombatEngine.ApplyAosOnHitEffects(attacker, target, damage: 100, weapon: null, flags: default);

        Assert.Equal(50, target.Mana);
        Assert.Equal(0, attacker.Mana);
    }

    [Fact]
    public void WraithForm_DrainScalesDownWithLowerSpiritSpeak()
    {
        var (_, attacker, target) = MakeCombatants();
        attacker.WraithFormActive = true;
        attacker.SetSkill(SkillType.SpiritSpeak, 500); // 5 + 15*500/1000 = 12

        CombatEngine.ApplyAosOnHitEffects(attacker, target, damage: 100, weapon: null, flags: default);

        Assert.Equal(38, target.Mana);   // 50 - 12
        Assert.Equal(12, attacker.Mana);
    }

    [Fact]
    public void WraithForm_DrainCappedByTargetMana()
    {
        var (_, attacker, target) = MakeCombatants();
        attacker.WraithFormActive = true;
        attacker.SetSkill(SkillType.SpiritSpeak, 1000); // wants 20
        target.Mana = 8;

        CombatEngine.ApplyAosOnHitEffects(attacker, target, damage: 100, weapon: null, flags: default);

        Assert.Equal(0, target.Mana);    // only 8 available
        Assert.Equal(8, attacker.Mana);
    }

    // ---------- Curse Weapon life-leech add (weapon-gated) ----------

    [Fact]
    public void CursedWeapon_LeechesThroughItsOwnProperty()
    {
        var (world, attacker, target) = MakeCombatants();

        bool leeched = false;
        var prev = CombatEngine.OnLeechEffect;
        CombatEngine.OnLeechEffect = _ => leeched = true;
        try
        {
            // The curse is the weapon's HITLEECHLIFE (CCharSpell.cpp:1365): with it
            // the life-leech percent is there, counted once.
            var weapon = world.CreateItem();
            weapon.ItemType = ItemType.WeaponSword;
            weapon.SetTag("HITLEECHLIFE", "50");
            Assert.Equal(50, CombatEngine.GetOnHitPropertyValue(attacker, weapon, "HITLEECHLIFE"));
            CombatEngine.ApplyAosOnHitEffects(attacker, target, damage: 100, weapon: weapon, flags: default);
            Assert.True(leeched);
            Assert.True(attacker.Hits >= 50); // heal never reduces hits
        }
        finally { CombatEngine.OnLeechEffect = prev; }
    }

    [Fact]
    public void UncursedWeapon_NoLeech()
    {
        var (world, attacker, target) = MakeCombatants();

        bool leeched = false;
        var prev = CombatEngine.OnLeechEffect;
        CombatEngine.OnLeechEffect = _ => leeched = true;
        try
        {
            var weapon = world.CreateItem();
            weapon.ItemType = ItemType.WeaponSword;
            CombatEngine.ApplyAosOnHitEffects(attacker, target, damage: 100, weapon: weapon, flags: default);
            Assert.False(leeched);
        }
        finally { CombatEngine.OnLeechEffect = prev; }
    }

    // ---------- Spell cast sets/clears the states ----------

    private static SpellEngine MakeEngineWith(SpellType id, out GameWorld world, out Character caster)
    {
        world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = id,
            Flags = SpellFlag.TargChar | SpellFlag.Good,
            EffectBase = 15,
            EffectScale = 15,
            DurationBase = 600,
            DurationScale = 600,
        });
        caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(120, 120, 0, 0));
        return new SpellEngine(world, registry);
    }

    [Fact]
    public void CurseWeapon_Cast_CursesTheWeapon_ExpiryClears()
    {
        var engine = MakeEngineWith(SpellType.CurseWeapon, out var world, out var caster);
        var weapon = world.CreateItem();
        weapon.ItemType = ItemType.WeaponSword;
        weapon.SetTag("HITLEECHLIFE", "7");
        Assert.True(caster.Equip(weapon, Layer.OneHanded));

        Assert.True(engine.CastStart(caster, SpellType.CurseWeapon, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        var mem = caster.FindLayer(SpellLayers.CurseWeapon);
        Assert.NotNull(mem);
        Assert.Equal(50, (ushort)mem!.MoreP.Y);           // the fixed level, not the EFFECT curve
        Assert.Equal(57, CombatEngine.GetItemNumProperty(weapon, "HITLEECHLIFE"));

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(7, CombatEngine.GetItemNumProperty(weapon, "HITLEECHLIFE"));
    }

    [Fact]
    public void WraithForm_Cast_SetsActive_ExpiryClears()
    {
        var engine = MakeEngineWith(SpellType.WraithForm, out _, out var caster);
        Assert.True(engine.CastStart(caster, SpellType.WraithForm, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        Assert.True(caster.WraithFormActive);

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.False(caster.WraithFormActive);
    }
}
