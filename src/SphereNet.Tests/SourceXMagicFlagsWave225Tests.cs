using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXMagicFlagsWave225Tests
{
    [Fact]
    public void IgnoreArmor_BypassesElementalResistanceForMagicDamage()
    {
        var (engine, caster, target) = CreateDamageStack();
        target.ResFire = 100;

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);
        Assert.Equal(200, target.Hits);

        CharacterMagicFlags(MagicConfigFlags.IgnoreArmor);
        engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);
        Assert.Equal(160, target.Hits);
    }

    [Fact]
    public void SingleReflection_ConsumesTargetChargeAndDamagesOriginalCaster()
    {
        var (engine, caster, target) = CreateDamageStack();
        target.SetStatFlag(StatFlag.Reflection);

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);

        Assert.Equal(160, caster.Hits);
        Assert.Equal(200, target.Hits);
        Assert.False(target.IsStatFlag(StatFlag.Reflection));
    }

    [Fact]
    public void DoubleReflection_ConsumesBothChargesAndReturnsSpellToOriginalTarget()
    {
        var (engine, caster, target) = CreateDamageStack();
        caster.SetStatFlag(StatFlag.Reflection);
        target.SetStatFlag(StatFlag.Reflection);

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);

        Assert.Equal(200, caster.Hits);
        Assert.Equal(160, target.Hits);
        Assert.False(caster.IsStatFlag(StatFlag.Reflection));
        Assert.False(target.IsStatFlag(StatFlag.Reflection));
    }

    [Fact]
    public void NoReflectOwnWithDeleteReflectOwn_ConsumesBothAndAbsorbsSpell()
    {
        var (engine, caster, target) = CreateDamageStack();
        caster.SetStatFlag(StatFlag.Reflection);
        target.SetStatFlag(StatFlag.Reflection);
        CharacterMagicFlags(MagicConfigFlags.NoReflectOwn | MagicConfigFlags.DeleteReflectOwn);

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Fireball, 1000);

        Assert.Equal(200, caster.Hits);
        Assert.Equal(200, target.Hits);
        Assert.False(caster.IsStatFlag(StatFlag.Reflection));
        Assert.False(target.IsStatFlag(StatFlag.Reflection));
    }

    private static (SpellEngine Engine, SphereNet.Game.Objects.Characters.Character Caster,
        SphereNet.Game.Objects.Characters.Character Target) CreateDamageStack()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Fireball,
            Flags = SpellFlag.Damage,
            EffectBase = 40,
            EffectScale = 40,
        });
        var caster = world.CreateCharacter();
        caster.Hits = caster.MaxHits = 200;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Hits = target.MaxHits = 200;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (new SpellEngine(world, registry), caster, target);
    }

    private static void CharacterMagicFlags(MagicConfigFlags flags) =>
        SphereNet.Game.Objects.Characters.Character.MagicFlags = (int)flags;
}
