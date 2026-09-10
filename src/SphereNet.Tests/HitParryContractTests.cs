using System;
using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// @HitParry's arguments, and when it fires (port plan İŞ-21 / PLAN-402, the
/// parry leg).
///
/// The reference documents the contract in its own source (CCharFight.cpp:2095-2119):
/// ARGN1 is the PERCENT of the blow the parry takes off, ARGN2 the damage type, ARGO
/// the parrying item, and four LOCALs carry the chance, the skill that rolls, the
/// item-wear chance and the raw damage. It fires BEFORE the roll, which is the only
/// way LOCAL.ParryChance can mean anything.
///
/// This engine fired it only AFTER a successful roll and read ARGN1 as the damage
/// LET THROUGH - the inverse quantity - so a script that raised ARGN1 to parry harder
/// took less off the blow instead of more, and could neither create nor refuse a parry.
/// </summary>
public sealed class HitParryContractTests : IDisposable
{
    private readonly Func<Character, Character, CombatEngine.HitParryContext, bool>? _savedParry
        = CombatEngine.OnHitParry;
    private readonly Action<Character>? _savedSucceeded = CombatEngine.OnParrySucceeded;
    private readonly bool _savedDurability = CombatEngine.DurabilityEnabled;
    private readonly Func<ushort, (int Min, int Max)?>? _savedWeaponDef = CombatEngine.WeaponDefLookup;

    private readonly int _savedDamageEra = Character.CombatDamageEra;
    private readonly int _savedHitEra = Character.CombatHitChanceEra;
    private readonly int _savedCombatFlags = Character.CombatFlags;
    private readonly int _savedParryEra = Character.CombatParryingEra;
    private readonly int _savedFeatureSE = Character.FeatureSE;
    private readonly int _savedLossMin = CombatEngine.DurabilityLossMin;
    private readonly int _savedLossMax = CombatEngine.DurabilityLossMax;

    public HitParryContractTests()
    {
        // Pin EVERY shared static this class reads, not just the obvious ones.
        // The parry answer depends on the parrying era and the SE feature mask, and
        // the wear check depends on the durability loss band; leaving any of them to
        // whatever the rest of the suite last set makes this class fail once in a
        // while for a reason that has nothing to do with what it is testing.
        CombatEngine.WeaponDefLookup = _ => (20, 20);
        CombatEngine.DurabilityLossMin = 1;
        CombatEngine.DurabilityLossMax = 1;
        Character.CombatDamageEra = 0;
        Character.CombatHitChanceEra = 0;
        Character.CombatFlags = 0;
        Character.CombatParryingEra =
            (int)(ParryEraFlags.PreSeFormula | ParryEraFlags.ShieldBlock);
        Character.FeatureSE = 0;
    }

    public void Dispose()
    {
        CombatEngine.OnHitParry = _savedParry;
        CombatEngine.OnParrySucceeded = _savedSucceeded;
        CombatEngine.DurabilityEnabled = _savedDurability;
        CombatEngine.WeaponDefLookup = _savedWeaponDef;
        CombatEngine.DurabilityLossMin = _savedLossMin;
        CombatEngine.DurabilityLossMax = _savedLossMax;
        Character.CombatDamageEra = _savedDamageEra;
        Character.CombatHitChanceEra = _savedHitEra;
        Character.CombatFlags = _savedCombatFlags;
        Character.CombatParryingEra = _savedParryEra;
        Character.FeatureSE = _savedFeatureSE;
    }

    /// <summary>A weapon whose damage comes from the pinned WeaponDefLookup.</summary>
    private static Item Blade() => new() { ItemType = ItemType.WeaponSword, BaseId = 0x0F5E };

    private static Character Fighter(int parrying = 0)
    {
        var ch = new Character();
        ch.Str = ch.Dex = ch.Int = 100;
        ch.MaxHits = ch.Hits = 100;
        ch.MaxStam = ch.Stam = 100;
        ch.SetSkill(SkillType.Swordsmanship, 1000);
        ch.SetSkill(SkillType.Tactics, 1000);
        ch.SetSkill(SkillType.Parrying, (ushort)parrying);
        return ch;
    }

    /// <summary>Swing until something other than a miss comes out, so the parry
    /// branch is the thing under test rather than the hit roll.</summary>
    private static int SwingUntilResolved(Character attacker, Character target, int tries = 4000)
    {
        for (int i = 0; i < tries; i++)
        {
            target.Hits = target.MaxHits;
            int dmg = CombatEngine.ResolveAttack(attacker, target, Blade());
            if (dmg != CombatEngine.AttackMiss)
                return dmg;
        }
        throw new InvalidOperationException("every swing missed");
    }

    // ---- when it fires ----------------------------------------------------

    [Fact]
    public void ItFiresEvenWhenTheEngineWouldNotHaveRolledAParry()
    {
        // No shield, no Parrying: the engine's own chance is zero. The trigger still
        // fires, and LOCAL.ParryChance is how a script parries anyway.
        var attacker = Fighter();
        var target = Fighter(parrying: 0);

        bool fired = false;
        CombatEngine.OnHitParry = (_, _, ctx) =>
        {
            fired = true;
            Assert.Equal(0, ctx.ParryChance);   // seeded from the engine's own answer
            ctx.ParryChance = 100;              // ...and overridden
            return true;
        };

        Assert.Equal(CombatEngine.AttackParried, SwingUntilResolved(attacker, target));
        Assert.True(fired);
    }

    [Fact]
    public void AScriptCanRefuseAParryTheEngineWouldHaveRolled()
    {
        var attacker = Fighter();
        var target = Fighter(parrying: 1000);
        target.Equip(new Item { ItemType = ItemType.Shield }, Layer.TwoHanded);

        CombatEngine.OnHitParry = (_, _, ctx) => { ctx.ParryChance = 0; return true; };
        bool parried = false;
        CombatEngine.OnParrySucceeded = _ => parried = true;

        for (int i = 0; i < 500; i++)
        {
            target.Hits = target.MaxHits;
            Assert.NotEqual(CombatEngine.AttackParried,
                CombatEngine.ResolveAttack(attacker, target, Blade()));
        }
        Assert.False(parried);
    }

    [Fact]
    public void ReturningOneDropsTheBlowWhole()
    {
        var attacker = Fighter();
        var target = Fighter(parrying: 0);

        CombatEngine.OnHitParry = (_, _, _) => false;   // RETURN 1

        Assert.Equal(CombatEngine.AttackParried, SwingUntilResolved(attacker, target));
    }

    // ---- ARGN1 is a percent -----------------------------------------------

    [Fact]
    public void AHundredPercentIsAFullBlockAndZeroTakesNothingOff()
    {
        var attacker = Fighter();
        var target = Fighter(parrying: 0);

        CombatEngine.OnHitParry = (_, _, ctx) =>
        {
            ctx.ParryChance = 100;
            ctx.ReductionPercent = 100;
            return true;
        };
        Assert.Equal(CombatEngine.AttackParried, SwingUntilResolved(attacker, target));

        // 0% off: the parry lands but the blow is untouched.
        bool parried = false;
        CombatEngine.OnParrySucceeded = _ => parried = true;
        CombatEngine.OnHitParry = (_, _, ctx) =>
        {
            ctx.ParryChance = 100;
            ctx.ReductionPercent = 0;
            return true;
        };
        int dmg = SwingUntilResolved(attacker, target);
        Assert.True(parried);
        Assert.NotEqual(CombatEngine.AttackParried, dmg);
        Assert.True(dmg > 0, "a 0% reduction must not swallow the blow");
    }

    [Fact]
    public void APartialReductionTakesThatPercentOff()
    {
        // Measured on ONE swing rather than across totals: the script is handed the
        // raw damage, so the reduction can be checked against that exact number
        // instead of against an average the hit roll also moves.
        var attacker = Fighter();
        var target = Fighter(parrying: 0);

        int raw = 0;
        CombatEngine.OnHitParry = (_, _, ctx) =>
        {
            raw = ctx.Damage;
            ctx.ParryChance = 100;
            ctx.ReductionPercent = 50;
            return true;
        };

        int dmg = SwingUntilResolved(attacker, target);

        Assert.True(raw > 1, $"expected a real blow to reduce, got {raw}");
        Assert.Equal(raw - raw * 50 / 100, dmg);
    }

    // ---- what else the script is handed ------------------------------------

    [Fact]
    public void TheParryingItemIsHandedOverAndWornOnASuccess()
    {
        CombatEngine.DurabilityEnabled = true;
        var attacker = Fighter();
        var target = Fighter(parrying: 1000);
        var shield = new Item { ItemType = ItemType.Shield, HitsMax = 50, HitsCur = 50 };
        target.Equip(shield, Layer.TwoHanded);

        Item? seen = null;
        CombatEngine.OnHitParry = (_, _, ctx) =>
        {
            seen = ctx.ParryItem;
            ctx.ParryChance = 100;
            ctx.ItemParryDamageChance = 100;
            return true;
        };

        SwingUntilResolved(attacker, target);

        Assert.Same(shield, seen);
        Assert.True(shield.HitsCur < 50, "the reference wears the parrying item on a success");
    }

    [Fact]
    public void AZeroItemDamageChanceLeavesTheShieldAlone()
    {
        CombatEngine.DurabilityEnabled = true;
        var attacker = Fighter();
        var target = Fighter(parrying: 1000);
        var shield = new Item { ItemType = ItemType.Shield, HitsMax = 50, HitsCur = 50 };
        target.Equip(shield, Layer.TwoHanded);

        CombatEngine.OnHitParry = (_, _, ctx) =>
        {
            ctx.ParryChance = 100;
            ctx.ItemParryDamageChance = 0;
            return true;
        };

        for (int i = 0; i < 50; i++)
        {
            target.Hits = target.MaxHits;
            CombatEngine.ResolveAttack(attacker, target, Blade());
        }

        Assert.Equal(50, shield.HitsCur);
    }

    [Fact]
    public void TheRawDamageIsHandedOverAndCanBeRewritten()
    {
        var attacker = Fighter();
        var target = Fighter(parrying: 0);

        int seenDamage = -1;
        CombatEngine.OnHitParry = (_, _, ctx) =>
        {
            seenDamage = ctx.Damage;
            ctx.ParryChance = 0;    // no parry: the rewritten damage is what lands
            ctx.Damage = 7;
            return true;
        };

        int dmg = SwingUntilResolved(attacker, target);

        Assert.True(seenDamage > 0, "the raw damage should reach the script");
        Assert.Equal(7, dmg);
    }
}
