using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXBushidoParryWave227Tests
{
    [Fact]
    public void LegacyShieldFormula_RemainsParryOverFortyPlusGmBonus()
    {
        var defender = CreateDefender();
        var shield = Equip(defender, Layer.TwoHanded, ItemType.Shield);
        defender.SetSkill(SkillType.Parrying, 1000);

        Assert.Equal(30, CombatEngine.CalculateParryChance(defender, out Item? parryItem));
        Assert.Same(shield, parryItem);
    }

    [Fact]
    public void SamuraiEmpireShieldFormula_BushidoPenalizesShieldParry()
    {
        var defender = CreateDefender();
        Equip(defender, Layer.TwoHanded, ItemType.Shield);
        defender.SetSkill(SkillType.Parrying, 1000);
        defender.SetSkill(SkillType.Bushido, 1000);
        EnableSe(ParryEraFlags.ShieldBlock);

        Assert.Equal(5, CombatEngine.CalculateParryChance(defender, out _));
    }

    [Theory]
    [InlineData(Layer.OneHanded, ParryEraFlags.OneHandBlock, 25)]
    [InlineData(Layer.TwoHanded, ParryEraFlags.TwoHandBlock, 29)]
    public void SamuraiEmpireWeaponFormula_UsesLayerSpecificDivisor(
        Layer layer, ParryEraFlags blockFlag, int expected)
    {
        var defender = CreateDefender();
        var weapon = Equip(defender, layer, ItemType.WeaponSword);
        defender.SetSkill(SkillType.Parrying, 1000);
        defender.SetSkill(SkillType.Bushido, 1000);
        EnableSe(blockFlag);

        Assert.Equal(expected, CombatEngine.CalculateParryChance(defender, out Item? parryItem));
        Assert.Same(weapon, parryItem);
    }

    [Fact]
    public void SamuraiEmpireFormula_AppliesDexErosionAndEquipmentGate()
    {
        var defender = CreateDefender();
        defender.Dex = 50;
        Equip(defender, Layer.TwoHanded, ItemType.WeaponSword);
        defender.SetSkill(SkillType.Parrying, 1000);
        defender.SetSkill(SkillType.Bushido, 1000);
        EnableSe(ParryEraFlags.TwoHandBlock);

        Assert.Equal(20, CombatEngine.CalculateParryChance(defender, out _));

        EnableSe(ParryEraFlags.OneHandBlock);
        Assert.Equal(0, CombatEngine.CalculateParryChance(defender, out _));
    }

    private static Character CreateDefender()
    {
        var world = TestHarness.CreateWorld();
        var defender = world.CreateCharacter();
        defender.Dex = 100;
        return defender;
    }

    private static Item Equip(Character defender, Layer layer, ItemType type)
    {
        var item = new Item { ItemType = type };
        defender.Equip(item, layer);
        return item;
    }

    private static void EnableSe(ParryEraFlags equipmentFlag)
    {
        Character.FeatureSE = 0x02;
        Character.CombatParryingEra = (int)(ParryEraFlags.SeFormula | equipmentFlag);
    }
}
