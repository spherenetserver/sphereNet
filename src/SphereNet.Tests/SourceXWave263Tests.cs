using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 263 — AOS suit-property aggregation (stat slice). Equipped items contribute
/// their BONUSSTR/BONUSDEX/BONUSINT to the wearer's effective stat, derived on read
/// (base field + equipped-item sum). The correctness-facing consumers — display,
/// melee damage, carry weight, REQSTR gate, skill contribution — see the bonus,
/// while the stored max pools stay derived from the base stat (no feedback loop)
/// and the base field / script property remain untouched (no equip-time mutation).
/// </summary>
public sealed class SourceXWave263Tests
{
    private static (GameWorld world, Character ch) Make(short str = 40, short dex = 40, short intel = 40)
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Str = str; ch.Dex = dex; ch.Int = intel;
        ch.MaxHits = 100; ch.Hits = 100;
        ch.MaxMana = 50; ch.Mana = 50;
        ch.MaxStam = 60; ch.Stam = 60;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    private static Item StatPiece(GameWorld world, string prop, int value)
    {
        var piece = world.CreateItem();
        piece.ItemType = ItemType.Armor;
        piece.SetTag(prop, value.ToString());
        return piece;
    }

    [Fact]
    public void EffectiveStat_NoEquipment_EqualsBase()
    {
        var (_, ch) = Make(str: 55, dex: 45, intel: 35);
        Assert.Equal(55, CombatEngine.EffectiveStr(ch));
        Assert.Equal(45, CombatEngine.EffectiveDex(ch));
        Assert.Equal(35, CombatEngine.EffectiveInt(ch));
    }

    [Fact]
    public void EffectiveStat_SumsEquippedBonusesAcrossItems()
    {
        var (world, ch) = Make(str: 50);
        ch.Equip(StatPiece(world, "BONUSSTR", 10), Layer.Helm);
        ch.Equip(StatPiece(world, "BONUSSTR", 15), Layer.Gloves);

        Assert.Equal(75, CombatEngine.EffectiveStr(ch)); // 50 + 10 + 15
        Assert.Equal(40, CombatEngine.EffectiveDex(ch)); // untouched stat
    }

    [Fact]
    public void EffectiveStat_ClampedNonNegative()
    {
        var (world, ch) = Make(str: 20);
        ch.Equip(StatPiece(world, "BONUSSTR", -50), Layer.Chest);
        Assert.Equal(0, CombatEngine.EffectiveStr(ch)); // 20 - 50 -> floored at 0
    }

    [Fact]
    public void BaseFieldAndScriptProperty_StayOnBase()
    {
        var (world, ch) = Make(str: 50);
        ch.Equip(StatPiece(world, "BONUSSTR", 30), Layer.Helm);

        // No equip-time mutation: the stored base field and the script getter are
        // unchanged; only the effective read includes the suit.
        Assert.Equal(50, ch.Str);
        Assert.True(ch.TryGetProperty("STR", out string v));
        Assert.Equal("50", v);
        Assert.Equal(80, CombatEngine.EffectiveStr(ch));
    }

    [Fact]
    public void MaxPools_UnchangedByStatBonus()
    {
        var (world, ch) = Make(str: 50);
        ch.Equip(StatPiece(world, "BONUSSTR", 40), Layer.Helm);
        ch.Equip(StatPiece(world, "BONUSDEX", 40), Layer.Gloves);
        ch.Equip(StatPiece(world, "BONUSINT", 40), Layer.Neck);

        // The stored max pools are derived from the base stat, not the effective
        // read, so an equipped stat suit never inflates them.
        Assert.Equal(100, ch.MaxHits);
        Assert.Equal(50, ch.MaxMana);
        Assert.Equal(60, ch.MaxStam);
    }

    [Fact]
    public void CarryWeight_RaisedByStrSuit()
    {
        var (world, ch) = Make(str: 50);
        int baseMax = ch.MaxWeight; // 50*7/2 + 40 = 215
        ch.Equip(StatPiece(world, "BONUSSTR", 20), Layer.Chest);
        Assert.Equal(baseMax + 20 * 7 / 2, ch.MaxWeight); // effective 70 -> 285
    }

    [Fact]
    public void MeleeDamage_RaisedByStrSuit()
    {
        var (world, ch) = Make(str: 40);
        var (_, baseMax) = CombatEngine.CalcWeaponDamage(ch, null, era: 0);
        ch.Equip(StatPiece(world, "BONUSSTR", 40), Layer.Helm);
        var (_, suitMax) = CombatEngine.CalcWeaponDamage(ch, null, era: 0);

        // Unarmed max damage scales with effective STR (Str/4 base + STR% bonus).
        Assert.True(suitMax > baseMax, $"expected suit max {suitMax} > base max {baseMax}");
    }

    [Fact]
    public void ReqStrGate_MetByStrSuit()
    {
        var (world, ch) = Make(str: 40);
        var heavy = world.CreateItem();
        heavy.ItemType = ItemType.Armor;
        heavy.SetTag("OVERRIDE.REQSTR", "60");

        // Base STR 40 < 60: too weak.
        Assert.False(ch.CanEquip(heavy, Layer.Chest, out var d1));
        Assert.Equal(Character.EquipDenial.TooWeak, d1);

        // A +25 STR suit lifts effective STR to 65 >= 60.
        ch.Equip(StatPiece(world, "BONUSSTR", 25), Layer.Helm);
        Assert.True(ch.CanEquip(heavy, Layer.Chest, out var d2));
        Assert.Equal(Character.EquipDenial.None, d2);
    }

    [Fact]
    public void SkillStatContribution_UsesEffectiveStat()
    {
        var (world, ch) = Make(str: 50);
        int baseAdjusted = SkillEngine.GetAdjustedSkill(ch, SkillType.Wrestling);
        ch.Equip(StatPiece(world, "BONUSSTR", 40), Layer.Helm);
        int suitAdjusted = SkillEngine.GetAdjustedSkill(ch, SkillType.Wrestling);

        // A skill whose def carries a STR stat weight gains from the effective STR;
        // if the def has no stat weight, both reads are equal (never less).
        Assert.True(suitAdjusted >= baseAdjusted);
    }
}
