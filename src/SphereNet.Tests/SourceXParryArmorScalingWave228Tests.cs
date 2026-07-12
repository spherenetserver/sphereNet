using SphereNet.Core.Enums;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXParryArmorScalingWave228Tests
{
    [Fact]
    public void LegacyShieldArmor_UsesSevenPercentHandsCoverage()
    {
        var defender = CreateCharacter();
        EquipArmor(defender, Layer.TwoHanded, ItemType.Shield, 40);

        Assert.Equal(2, CombatEngine.CalcArmorDefense(defender));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(500, 11)]
    [InlineData(1000, 20)]
    public void ArmorScalingShield_UsesParryingAndHalfArmorCap(int parrying, int expected)
    {
        var defender = CreateCharacter();
        EquipArmor(defender, Layer.TwoHanded, ItemType.Shield, 40);
        defender.SetSkill(SkillType.Parrying, (ushort)parrying);
        Character.CombatParryingEra = (int)ParryEraFlags.ArmorScaling;

        Assert.Equal(expected, CombatEngine.CalcArmorDefense(defender));
    }

    [Fact]
    public void ArmorScaling_IgnoresNonShieldOnTwoHandedLayer()
    {
        var defender = CreateCharacter();
        EquipArmor(defender, Layer.TwoHanded, ItemType.WeaponSword, 40);
        defender.SetSkill(SkillType.Parrying, 1000);
        Character.CombatParryingEra = (int)ParryEraFlags.ArmorScaling;

        Assert.Equal(0, CombatEngine.CalcArmorDefense(defender));
    }

    [Theory]
    [InlineData(SpellType.Protection)]
    [InlineData(SpellType.ArchProtection)]
    public void ProtectionSpellMemory_AddsArmorAndRemovesItOnExpiry(SpellType spell)
    {
        var world = TestHarness.CreateWorld();
        var target = world.CreateCharacter();
        target.Str = target.Dex = target.Int = 50;
        var registry = CreateProtectionRegistry(spell, 25);
        var engine = new SpellEngine(world, registry);

        Assert.True(engine.ApplyScriptSpellEffect(target, target, spell, 1000));
        Assert.Equal(25, CombatEngine.CalcArmorDefense(target));
        Assert.Equal(50, target.Str);
        Assert.Equal(50, target.Dex);
        Assert.Equal(50, target.Int);

        engine.ProcessExpirations(long.MaxValue);
        Assert.Equal(0, CombatEngine.CalcArmorDefense(target));
    }

    [Fact]
    public void ProtectionAndArchProtection_ReplaceTheirSharedSpellLayer()
    {
        var world = TestHarness.CreateWorld();
        var target = world.CreateCharacter();
        var registry = new SpellRegistry();
        RegisterProtection(registry, SpellType.Protection, 25);
        RegisterProtection(registry, SpellType.ArchProtection, 15);
        var engine = new SpellEngine(world, registry);

        Assert.True(engine.ApplyScriptSpellEffect(target, target, SpellType.Protection, 1000));
        Assert.Equal(25, CombatEngine.CalcArmorDefense(target));

        Assert.True(engine.ApplyScriptSpellEffect(target, target, SpellType.ArchProtection, 1000));
        Assert.Equal(15, CombatEngine.CalcArmorDefense(target));
    }

    private static Character CreateCharacter() => TestHarness.CreateWorld().CreateCharacter();

    private static void EquipArmor(Character defender, Layer layer, ItemType type, int armor)
    {
        var item = new Item { ItemType = type };
        item.SetTag("ARMOR", armor.ToString());
        defender.Equip(item, layer);
    }

    private static SpellRegistry CreateProtectionRegistry(SpellType spell, int armor)
    {
        var registry = new SpellRegistry();
        RegisterProtection(registry, spell, armor);
        return registry;
    }

    private static void RegisterProtection(SpellRegistry registry, SpellType spell, int armor)
    {
        registry.Register(new SpellDef
        {
            Id = spell,
            Flags = SpellFlag.TargChar | SpellFlag.Bless | SpellFlag.Good,
            EffectBase = armor,
            EffectScale = armor,
            DurationBase = 600,
            DurationScale = 600,
        });
    }
}
