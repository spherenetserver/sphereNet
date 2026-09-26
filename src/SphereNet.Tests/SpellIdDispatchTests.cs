using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X OnSpellEffect switches on the spell id (CCharSpell.cpp:3874), not on
/// SPELLFLAG_BLESS / SPELLFLAG_CURSE. The pack flags Clumsy without "curse" and
/// Magic Reflection with "bless", so flag routing sent both into a switch that had
/// no case for them and they did nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellIdDispatchTests
{
    private static (SpellEngine, Character caster, Character target) Setup(SpellType spell, SpellFlag flags)
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef { Id = spell, Flags = flags, DurationBase = 100 });
        var engine = new SpellEngine(world, registry);
        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.Str = 50; target.Dex = 50; target.Int = 50;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (engine, caster, target);
    }

    [Fact]
    public void ClumsyLowersDexWithoutTheCurseFlag()
    {
        // Scripts-X [SPELL 1]: FLAGS=spellflag_dir_anim|spellflag_targ_char|spellflag_harm|spellflag_resist
        var (engine, caster, target) = Setup(SpellType.Clumsy,
            SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Resist);

        engine.ApplyDirectEffect(caster, target, SpellType.Clumsy, 500);

        Assert.True(target.Dex < 50);
    }

    [Fact]
    public void MagicReflectionRaisesReflectionDespiteItsBlessFlag()
    {
        // Scripts-X [SPELL 36] carries spellflag_bless.
        var (engine, caster, target) = Setup(SpellType.MagicReflect,
            SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Bless);

        engine.ApplyDirectEffect(caster, target, SpellType.MagicReflect, 500);

        Assert.True(target.IsStatFlag(StatFlag.Reflection));
    }
}
