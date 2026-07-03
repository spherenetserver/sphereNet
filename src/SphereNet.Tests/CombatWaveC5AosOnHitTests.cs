using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Combat wave C5 — the AOS on-hit property subset (Source-X Fight_Hit tail,
// CCharFight.cpp:2270-2361): HitLeechLife/Mana/Stam, HitManaDrain, the
// HitArea* splashes and the HitDispel/Fireball/Harm/Lightning/MagicArrow
// spell procs. Values are tag-backed on chars and items (def-tag fallback).
public class CombatWaveC5AosOnHitTests
{
    private static (Character Attacker, Character Target) MakePair(GameWorld world)
    {
        var attacker = world.CreateCharacter();
        attacker.Str = 50; attacker.Dex = 50; attacker.Int = 50;
        attacker.MaxHits = 100; attacker.Hits = 100;
        attacker.MaxMana = 100; attacker.Mana = 0;
        attacker.MaxStam = 100; attacker.Stam = 0;
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.MaxHits = 100; target.Hits = 100;
        target.MaxMana = 100; target.Mana = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (attacker, target);
    }

    [Fact]
    public void LeechStam_RestoresTheFullDamage()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, target) = MakePair(world);
        attacker.SetTag("HITLEECHSTAM", "100"); // 100 > rand(100): always procs

        CombatEngine.ApplyAosOnHitEffects(attacker, target, 25, null, CombatFlags.None);

        Assert.Equal(25, attacker.Stam);
    }

    [Fact]
    public void ManaDrain_StealsTwentyPercent_ClampedToVictimMana()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, target) = MakePair(world);
        attacker.SetTag("HITMANADRAIN", "100");

        // 20% of 100 damage = 20 mana from the victim to the attacker.
        CombatEngine.ApplyAosOnHitEffects(attacker, target, 100, null, CombatFlags.None);
        Assert.Equal(80, target.Mana);
        Assert.Equal(20, attacker.Mana);

        // The drain clamps to what the victim still has.
        target.Mana = 5;
        CombatEngine.ApplyAosOnHitEffects(attacker, target, 100, null, CombatFlags.None);
        Assert.Equal(0, target.Mana);
        Assert.Equal(25, attacker.Mana);
    }

    [Fact]
    public void LeechLife_HealsWithinTheSourceXWindow()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, target) = MakePair(world);
        attacker.Hits = 10;
        attacker.SetTag("HITLEECHLIFE", "100");

        // rand(0 .. dmg*100*30/10000): with dmg=100 the window is 0..30 per
        // hit — repeat until a non-zero roll lands (P(all zero) ~ (1/31)^50).
        for (int i = 0; i < 50 && attacker.Hits == 10; i++)
            CombatEngine.ApplyAosOnHitEffects(attacker, target, 100, null, CombatFlags.None);

        Assert.InRange((int)attacker.Hits, 11, 40);
    }

    [Fact]
    public void HitSpellProcs_FireThroughTheHook_FromWeaponAndCharTags()
    {
        var savedSpell = CombatEngine.OnHitSpell;
        try
        {
            var procs = new List<int>();
            CombatEngine.OnHitSpell = (_, _, spellId) => procs.Add(spellId);

            var world = TestHarness.CreateWorld();
            var (attacker, target) = MakePair(world);
            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.SetTag("HITFIREBALL", "100");     // item-borne proc
            attacker.SetTag("HITLIGHTNING", "100"); // char-borne proc

            CombatEngine.ApplyAosOnHitEffects(attacker, target, 10, sword, CombatFlags.None);

            Assert.Contains((int)SpellType.Fireball, procs);
            Assert.Contains((int)SpellType.Lightning, procs);
            Assert.DoesNotContain((int)SpellType.Harm, procs);

            // Without a weapon the whole proc block is skipped (Source-X
            // gates it on pWeapon).
            procs.Clear();
            CombatEngine.ApplyAosOnHitEffects(attacker, target, 10, null, CombatFlags.None);
            Assert.Empty(procs);
        }
        finally
        {
            CombatEngine.OnHitSpell = savedSpell;
        }
    }

    [Fact]
    public void HitArea_PhysicalAlways_ElementalOnlyUnderTheEngineFlag()
    {
        var savedArea = CombatEngine.OnHitAreaDamage;
        try
        {
            var splashes = new List<(int Dmg, DamageType Type)>();
            CombatEngine.OnHitAreaDamage = (_, _, dmg, type) => splashes.Add((dmg, type));

            var world = TestHarness.CreateWorld();
            var (attacker, target) = MakePair(world);
            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.SetTag("HITAREAPHYSICAL", "100");
            sword.SetTag("HITAREAFIRE", "100");

            // Without COMBAT_ELEMENTAL_ENGINE only the physical splash fires,
            // at half the damage.
            CombatEngine.ApplyAosOnHitEffects(attacker, target, 40, sword, CombatFlags.None);
            Assert.Single(splashes);
            Assert.Equal((20, DamageType.Physical), splashes[0]);

            splashes.Clear();
            CombatEngine.ApplyAosOnHitEffects(attacker, target, 40, sword, CombatFlags.ElementalEngine);
            Assert.Contains((20, DamageType.Physical), splashes);
            Assert.Contains((20, DamageType.Fire), splashes);
        }
        finally
        {
            CombatEngine.OnHitAreaDamage = savedArea;
        }
    }

    [Fact]
    public void OnHitPropertyValue_SumsCharWeaponAndTalisman()
    {
        var world = TestHarness.CreateWorld();
        var (attacker, _) = MakePair(world);
        attacker.SetTag("HITLEECHMANA", "10");

        var sword = world.CreateItem();
        sword.SetTag("HITLEECHMANA", "20");
        var talisman = world.CreateItem();
        talisman.SetTag("HITLEECHMANA", "30");
        attacker.Equip(talisman, Layer.Talisman);

        Assert.Equal(60, CombatEngine.GetOnHitPropertyValue(attacker, sword, "HITLEECHMANA"));
    }

    [Fact]
    public void ResolveAttack_RunsTheOnHitEffects()
    {
        var savedLookup = CombatEngine.WeaponDefLookup;
        var savedHook = CombatEngine.OnHitDamage;
        try
        {
            CombatEngine.OnHitDamage = null;
            CombatEngine.WeaponDefLookup = _ => (10, 10);

            var world = TestHarness.CreateWorld();
            var (attacker, target) = MakePair(world);
            attacker.IsPlayer = true;
            attacker.PrivLevel = PrivLevel.GM; // era-0 roll: always hits
            attacker.SetTag("HITLEECHSTAM", "100");
            target.SetSkill(SkillType.Parrying, 0);

            var sword = world.CreateItem();
            sword.ItemType = ItemType.WeaponSword;
            sword.BaseId = 0x0F5E;
            attacker.Equip(sword, Layer.OneHanded);

            int damage = CombatEngine.ResolveAttack(attacker, target, sword);

            Assert.Equal(10, damage);
            Assert.Equal(10, attacker.Stam); // leeched the full damage
        }
        finally
        {
            CombatEngine.WeaponDefLookup = savedLookup;
            CombatEngine.OnHitDamage = savedHook;
        }
    }

    [Fact]
    public void PropertySurface_TagBackedOnCharsItemsAndDefs()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        Assert.True(ch.TrySetProperty("HITLEECHLIFE", "50"));
        Assert.True(ch.TryGetProperty("HITLEECHLIFE", out var v) && v == "50");

        var item = world.CreateItem();
        Assert.True(item.TrySetProperty("HITFIREBALL", "25"));
        Assert.True(item.TryGetProperty("HITFIREBALL", out var f) && f == "25");

        var itemDef = new SphereNet.Scripting.Definitions.ItemDef(default);
        itemDef.LoadFromKey("HITLIGHTNING", "30");
        Assert.Equal("30", itemDef.TagDefs.Get("HITLIGHTNING"));

        var charDef = new SphereNet.Scripting.Definitions.CharDef(default);
        charDef.LoadFromKey("HITMANADRAIN", "40");
        Assert.Equal("40", charDef.TagDefs.Get("HITMANADRAIN"));
    }
}
