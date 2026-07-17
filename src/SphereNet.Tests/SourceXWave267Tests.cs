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
/// Wave 267 — AOS suit aggregation for Hit Chance Increase (HCI) and Luck. Source-X
/// accumulates every equipped item's PROPIEQUIP_INCREASEHITCHANCE / PROPIEQUIP_LUCK
/// into the char-level PROPCH_* at equip time; SphereNet mirrors it with a live
/// full-suit scan. The era-2 to-hit formula now reads HCI/DCI from the whole suit
/// (previously only the char tag + weapon/talisman, so armor/jewelry HCI was lost).
/// Luck is aggregated for the status display only — Source-X consumes Luck nowhere
/// (no loot/damage/spawn hook), so it is deliberately NOT wired into drops here.
/// </summary>
public sealed class SourceXWave267Tests
{
    private static (GameWorld world, Character ch) Make(short luck = 0)
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Luck = luck;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return (world, ch);
    }

    private static Item Piece(GameWorld world, string prop, int value)
    {
        var piece = world.CreateItem();
        piece.ItemType = ItemType.Armor;
        piece.SetTag(prop, value.ToString());
        return piece;
    }

    // ---- Luck aggregation (display-only) ----

    [Fact]
    public void EffectiveLuck_NoEquipment_EqualsBase()
    {
        var (_, ch) = Make(luck: 120);
        Assert.Equal(120, CombatEngine.EffectiveLuck(ch));
    }

    [Fact]
    public void EffectiveLuck_SumsEquippedLuckAcrossItems()
    {
        var (world, ch) = Make(luck: 50);
        ch.Equip(Piece(world, "LUCK", 40), Layer.Helm);
        ch.Equip(Piece(world, "LUCK", 30), Layer.Gloves);
        Assert.Equal(120, CombatEngine.EffectiveLuck(ch)); // 50 + 40 + 30
    }

    [Fact]
    public void EffectiveLuck_UnluckyGoesNegative_Unclamped()
    {
        // Source-X UNLUCKY = Luck -100; luck has no floor, unlike the stat slice.
        var (world, ch) = Make(luck: 0);
        ch.Equip(Piece(world, "LUCK", -100), Layer.Neck);
        Assert.Equal(-100, CombatEngine.EffectiveLuck(ch));
    }

    [Fact]
    public void BaseLuckAndScript_StayOnBase_AfterEquip()
    {
        // Live-scan invariant (Wave 262/263): no equip-time mutation of the base.
        var (world, ch) = Make(luck: 50);
        ch.Equip(Piece(world, "LUCK", 40), Layer.Helm);
        Assert.Equal((short)50, ch.Luck);                 // stored base untouched
        Assert.True(ch.TryGetProperty("LUCK", out var scripted));
        Assert.Equal("50", scripted);                     // script getter reads base
    }

    // ---- HCI suit aggregation (the era-2 regression the old weapon:null read missed) ----

    [Fact]
    public void HitChanceEra2_RisesWithHciOnEquippedArmor_NotJustCharTag()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        var target = world.CreateCharacter();
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        attacker.SetSkill(SkillType.Wrestling, 1000);
        target.SetSkill(SkillType.Wrestling, 1000);

        int baseChance = CombatEngine.CalcHitChance(attacker, target, era: 2);

        // HCI on an armor layer (not the char tag, not the weapon/talisman) must now
        // raise the attacker's hit chance — the whole point of full-suit aggregation.
        attacker.Equip(Piece(world, "INCREASEHITCHANCE", 45), Layer.Helm);
        int withSuitHci = CombatEngine.CalcHitChance(attacker, target, era: 2);

        Assert.True(withSuitHci > baseChance,
            $"suit HCI should raise hit chance: {withSuitHci} !> {baseChance}");
    }

    [Fact]
    public void DefenseChanceEra2_RisesWithDciOnEquippedArmor_LowersAttackerChance()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        var target = world.CreateCharacter();
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        attacker.SetSkill(SkillType.Wrestling, 1000);
        target.SetSkill(SkillType.Wrestling, 1000);

        int baseChance = CombatEngine.CalcHitChance(attacker, target, era: 2);

        target.Equip(Piece(world, "INCREASEDEFCHANCE", 45), Layer.Chest);
        int withSuitDci = CombatEngine.CalcHitChance(attacker, target, era: 2);

        Assert.True(withSuitDci < baseChance,
            $"suit DCI should lower attacker hit chance: {withSuitDci} !< {baseChance}");
    }
}
