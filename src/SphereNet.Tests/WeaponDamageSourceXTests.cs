using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Fight_CalcDamage's single damage bonus (CCharFight.cpp:1202-1325), the pre-AOS
/// armour roll (CCharFight.cpp:737-740 with CSRand's half-open draws) and the
/// GetSingle reading of decimal literals in character keys (CExpression.cpp:743-760).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class WeaponDamageSourceXTests
{
    private static (Character Ch, Item Katana) KatanaFighter()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 100;
        ch.MaxHits = ch.Hits = 100;
        ch.SetSkill(SkillType.Tactics, 1000);
        ch.SetSkill(SkillType.Anatomy, 1000);
        var katana = new Item { ItemType = ItemType.WeaponSword, BaseId = 0x13FF };
        ch.Equip(katana, Layer.OneHanded);
        return (ch, katana);
    }

    [Fact]
    public void DamageIncrease_IsPartOfTheOneBonus_PreAos()
    {
        var saved = CombatEngine.WeaponDefLookup;
        try
        {
            CombatEngine.WeaponDefLookup = _ => (11, 13);
            var (ch, katana) = KatanaFighter();
            ch.SetTag("INCREASEDAM", "50");

            // iDmgBonus = 50 (DI) + (1000-500)/10 + 1000/50 + 10 + 100*20/100 = 150
            // min 11 + 11*150/100 = 27, max 13 + 13*150/100 = 32.
            Assert.Equal((27, 32), CombatEngine.CalcWeaponDamage(ch, katana, 1));
        }
        finally
        {
            CombatEngine.WeaponDefLookup = saved;
        }
    }

    [Fact]
    public void DamageIncrease_SumsEquippedItems()
    {
        var saved = CombatEngine.WeaponDefLookup;
        try
        {
            CombatEngine.WeaponDefLookup = _ => (11, 13);
            var (ch, katana) = KatanaFighter();
            ch.SetTag("INCREASEDAM", "20");
            katana.SetTag("INCREASEDAM", "30");

            // LayerAdd folds the item's INCREASEDAM into the char (CCharAct.cpp:3395).
            Assert.Equal(50, CombatEngine.CalculateDamageIncrease(ch));
            Assert.Equal((27, 32), CombatEngine.CalcWeaponDamage(ch, katana, 1));
        }
        finally
        {
            CombatEngine.WeaponDefLookup = saved;
        }
    }

    [Fact]
    public void DamageIncrease_RespectsTheSwingFlagsForNpcs()
    {
        var saved = CombatEngine.WeaponDefLookup;
        try
        {
            CombatEngine.WeaponDefLookup = _ => (10, 10);
            var (ch, katana) = KatanaFighter();
            ch.IsPlayer = false;
            ch.Str = 0;
            ch.SetTag("INCREASEDAM", "50");

            Assert.Equal((10, 10), CombatEngine.CalcWeaponDamage(ch, katana, 0, CombatFlags.None));
            Assert.Equal((15, 15), CombatEngine.CalcWeaponDamage(ch, katana, 0, CombatFlags.NpcBonusDamage));
        }
        finally
        {
            CombatEngine.WeaponDefLookup = saved;
        }
    }

    [Fact]
    public void PreAosArmor_ShareIsSevenToThirtyFourPercent()
    {
        // Get16Val2Fast(7,35) = 7 + Get16ValFast(28): 7..34, never 35.
        var seen = new HashSet<int>();
        for (int i = 0; i < 5000; i++)
        {
            var (arMin, arMax) = CombatEngine.RollPreAosArmorBounds(100);
            Assert.InRange(arMax, 7, 34);
            Assert.Equal(arMax / 2, arMin);
            seen.Add(arMax);
        }
        Assert.Contains(7, seen);
        Assert.Contains(34, seen);
    }

    [Theory]
    // GetVal2Fast(iArMin, iArMax + 1) (CCharFight.cpp:740, #1550): uniform over
    // iArMin..iArMax inclusive.
    [InlineData(17, 34, 17, 34)]   // GetVal2Fast(17, 35) = 17..34
    [InlineData(16, 33, 16, 33)]   // GetVal2Fast(16, 34) = 16..33
    [InlineData(0, 0, 0, 0)]       // GetVal2Fast(0, 1) = 0
    [InlineData(0, 1, 0, 1)]       // GetVal2Fast(0, 2) = 0..1
    [InlineData(5, 5, 5, 5)]       // GetVal2Fast(5, 6) = 5
    public void PreAosArmor_DefenseFollowsGetVal2Fast(int arMin, int arMax, int lo, int hi)
    {
        var seen = new HashSet<int>();
        for (int i = 0; i < 2000; i++)
        {
            int d = CombatEngine.RollPreAosDefense(arMin, arMax);
            Assert.InRange(d, lo, hi);
            seen.Add(d);
        }
        Assert.Contains(lo, seen);
        Assert.Contains(hi, seen);
    }

    [Theory]
    [InlineData("TACTICS", "50.0", 500)]
    [InlineData("TACTICS", "50.5", 505)]
    [InlineData("TACTICS", "50", 50)]
    [InlineData("TACTICS", "{010 010}", 16)]
    [InlineData("TACTICS", "{50.5 50.5}", 505)]
    public void DecimalLiterals_ReadAsGetSingle_ForSkills(string key, string value, int expected)
    {
        var ch = TestHarness.CreateWorld().CreateCharacter();
        Assert.True(ch.TrySetProperty(key, value));
        Assert.Equal(expected, (int)ch.GetSkill(SkillType.Tactics));
    }

    [Theory]
    [InlineData("12.5", 125)]
    [InlineData("0.5", 5)]
    [InlineData("12", 12)]
    [InlineData("{010 010}", 16)]
    public void DecimalLiterals_ReadAsGetSingle_ForStats(string value, short expected)
    {
        var ch = TestHarness.CreateWorld().CreateCharacter();
        Assert.True(ch.TrySetProperty("STR", value));
        Assert.Equal(expected, ch.Str);
    }
}
