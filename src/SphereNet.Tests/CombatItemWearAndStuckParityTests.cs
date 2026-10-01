using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Combat wear goes through CItem::OnTakeDamage (CItem.cpp:5792): the SELFREPAIR roll
/// first (:5807-5821), then @Damage (:5823-5829), then one hit point of wear. Source-X
/// reaches it from the victim's worn piece (CCharFight.cpp:788-793), the attacker's
/// weapon (:2240-2243, default DAMAGE_HIT_BLUNT) and the parrying item (:2131-2133,
/// one point of the swing's type). The damage entry also deletes the LAYER_FLAG_Stuck
/// item (layer 51) with the paralysis unless the blow is DAMAGE_NOUNPARALYZE (:797-818).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class CombatItemWearAndStuckParityTests
{
    private static void PinCombat()
    {
        CombatEngine.WeaponDefLookup = _ => (20, 20);
        CombatEngine.DurabilityEnabled = true;
        CombatEngine.BreakOnZeroHits = true;
        Character.CombatDamageEra = 0;
        Character.CombatHitChanceEra = 0;
        Character.CombatFlags = 0;
        Character.CombatParryingEra = (int)(ParryEraFlags.PreSeFormula | ParryEraFlags.ShieldBlock);
        Character.FeatureSE = 0;
        // No parry from the engine's own roll; tests that want one ask @HitParry.
        CombatEngine.OnHitParry = (_, _, ctx) => { ctx.ParryChance = 0; return true; };
    }

    private static (GameWorld World, Character Attacker, Character Target) Pair()
    {
        PinCombat();
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        var target = world.CreateCharacter();
        foreach (var ch in new[] { attacker, target })
        {
            ch.Str = ch.Dex = ch.Int = 100;
            ch.MaxHits = ch.Hits = 1000;
            ch.MaxStam = ch.Stam = 100;
        }
        attacker.PrivLevel = PrivLevel.GM;   // a GM always lands
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (world, attacker, target);
    }

    private static Item Gear(GameWorld world, ItemType type, int cur, int max, int selfRepair)
    {
        var item = world.CreateItem();
        item.ItemType = type;
        item.BaseId = 0x0F5E;
        item.HitsMax = (ushort)max;
        item.HitsCur = (ushort)cur;
        if (selfRepair > 0)
            item.SetTag("SELFREPAIR", selfRepair.ToString());
        return item;
    }

    private static void Swing(Character attacker, Character target, Item? weapon)
    {
        for (int i = 0; i < 200; i++)
        {
            target.Hits = target.MaxHits;
            if (CombatEngine.ResolveAttack(attacker, target, weapon) != CombatEngine.AttackMiss)
                return;
        }
        throw new InvalidOperationException("every swing missed");
    }

    /// <summary>The victim's worn piece on the chest layer at a sure chance; no weapon wear.</summary>
    private static void ArmourOnly() => CombatEngine.OnHitDamage = ctx =>
    {
        ctx.ItemDamageLayer = Layer.Chest;
        ctx.ItemDamageChance = 100;
        ctx.WeaponDamageChance = 0;
        return ctx.Damage;
    };

    private static void WeaponOnly() => CombatEngine.OnHitDamage = ctx =>
    {
        ctx.ItemDamageChance = 0;
        ctx.WeaponDamageChance = 100;
        return ctx.Damage;
    };

    // ---- C4: SELFREPAIR on every combat wear site ------------------------------

    [Fact]
    public void SelfRepair_MendsTheVictimsArmourInCombat()
    {
        var (world, attacker, target) = Pair();
        var chest = Gear(world, ItemType.Armor, 50, 100, selfRepair: 10);
        target.Equip(chest, Layer.Chest);
        ArmourOnly();
        ItemDamageEngine.RandVal = _ => 0;   // SELFREPAIR 10 > every 0..9 roll

        Swing(attacker, target, null);

        Assert.Equal(52, chest.HitsCur);
    }

    [Fact]
    public void SelfRepair_MendsTheAttackersWeaponOnAHit()
    {
        var (world, attacker, target) = Pair();
        var blade = Gear(world, ItemType.WeaponSword, 50, 100, selfRepair: 10);
        attacker.Equip(blade, Layer.OneHanded);
        WeaponOnly();
        ItemDamageEngine.RandVal = _ => 0;

        Swing(attacker, target, blade);

        Assert.Equal(52, blade.HitsCur);
    }

    [Fact]
    public void SelfRepair_MendsTheParryingShield()
    {
        var (world, attacker, target) = Pair();
        var shield = Gear(world, ItemType.Shield, 50, 100, selfRepair: 10);
        target.Equip(shield, Layer.TwoHanded);
        CombatEngine.OnHitParry = (_, _, ctx) =>
        {
            ctx.ParryChance = 100;
            ctx.ReductionPercent = 100;
            ctx.ItemParryDamageChance = 100;
            return true;
        };
        ItemDamageEngine.RandVal = _ => 0;

        Swing(attacker, target, null);

        Assert.Equal(52, shield.HitsCur);
    }

    [Fact]
    public void SelfRepair_StopsAtMaxHitsAndSkipsDamageTrigger()
    {
        var (world, attacker, target) = Pair();
        var chest = Gear(world, ItemType.Armor, 99, 100, selfRepair: 10);
        target.Equip(chest, Layer.Chest);
        ArmourOnly();
        ItemDamageEngine.RandVal = _ => 0;
        int fired = 0;
        CombatEngine.OnItemDamaged = (_, _, _, _) => { fired++; return false; };

        Swing(attacker, target, null);

        Assert.Equal(100, chest.HitsCur);
        Assert.Equal(0, fired);   // a successful SELFREPAIR returns before @Damage
    }

    [Fact]
    public void FailedSelfRepairRoll_FallsThroughToDamageAndOnePointOfWear()
    {
        var (world, attacker, target) = Pair();
        var chest = Gear(world, ItemType.Armor, 50, 100, selfRepair: 3);
        target.Equip(chest, Layer.Chest);
        ArmourOnly();
        ItemDamageEngine.RandVal = _ => 5;   // 3 > 5 fails
        int fired = 0;
        CombatEngine.OnItemDamaged = (_, _, _, _) => { fired++; return false; };

        Swing(attacker, target, null);

        Assert.Equal(1, fired);
        Assert.Equal(49, chest.HitsCur);
    }

    [Fact]
    public void NoSelfRepair_DamageVetoSparesTheArmour()
    {
        var (world, attacker, target) = Pair();
        var chest = Gear(world, ItemType.Armor, 50, 100, selfRepair: 0);
        target.Equip(chest, Layer.Chest);
        ArmourOnly();
        CombatEngine.OnItemDamaged = (_, _, _, _) => true;   // RETURN 1

        Swing(attacker, target, null);

        Assert.Equal(50, chest.HitsCur);
    }

    [Fact]
    public void WeaponWear_DamageTriggerSeesHitBluntAndTheBlowFromTheVictim()
    {
        var (world, attacker, target) = Pair();
        var blade = Gear(world, ItemType.WeaponSword, 50, 100, selfRepair: 0);
        attacker.Equip(blade, Layer.OneHanded);
        WeaponOnly();
        DamageType seenType = 0;
        int seenDamage = 0;
        Character? seenSrc = null;
        CombatEngine.OnItemDamaged = (item, dmg, src, type) =>
        {
            if (ReferenceEquals(item, blade)) { seenType = type; seenDamage = dmg; seenSrc = src; }
            return false;
        };

        Swing(attacker, target, blade);

        // pWeapon->OnTakeDamage(iDmg, pCharTarg): the default type is DAMAGE_HIT_BLUNT.
        Assert.Equal(DamageType.HitBlunt, seenType);
        Assert.Equal(20, seenDamage);
        Assert.Same(target, seenSrc);
        Assert.Equal(49, blade.HitsCur);
    }

    [Fact]
    public void CombatWear_TakesOnePointRegardlessOfTheBlow()
    {
        var (world, attacker, target) = Pair();
        var chest = Gear(world, ItemType.Armor, 50, 100, selfRepair: 0);
        target.Equip(chest, Layer.Chest);
        ArmourOnly();

        Swing(attacker, target, null);

        Assert.Equal(49, chest.HitsCur);
    }

    // ---- C6: the stuck layer goes with the paralysis ----------------------------

    private static Item Stuck(GameWorld world, Character ch)
    {
        var flag = world.CreateItem();
        flag.ItemType = ItemType.EqStuck;
        flag.BaseId = 0x0EE3;
        Assert.True(ch.Equip(flag, Layer.FlagStuck));
        ch.SetStatFlag(StatFlag.Freeze);
        return flag;
    }

    [Fact]
    public void NormalDamage_DeletesTheStuckLayerItemAndClearsFreeze()
    {
        var (world, attacker, target) = Pair();
        var flag = Stuck(world, target);

        CombatEngine.ApplyScriptDamage(target, 5, DamageType.HitBlunt, attacker);

        Assert.True(flag.IsDeleted);
        Assert.Null(target.GetEquippedItem(Layer.FlagStuck));
        Assert.False(target.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void NoUnparalyzeDamage_LeavesTheStuckItemAndFreeze()
    {
        var (world, attacker, target) = Pair();
        var flag = Stuck(world, target);

        CombatEngine.ApplyScriptDamage(target, 5, DamageType.HitBlunt | DamageType.NoUnparalyze, attacker);

        Assert.False(flag.IsDeleted);
        Assert.Same(flag, target.GetEquippedItem(Layer.FlagStuck));
        Assert.True(target.IsStatFlag(StatFlag.Freeze));
    }

    [Fact]
    public void StuckLayer_IsSourceXLayer51()
    {
        Assert.Equal(51, (int)Layer.FlagStuck);
    }
}
