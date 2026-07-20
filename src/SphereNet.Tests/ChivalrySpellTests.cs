using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit finding (wiki/test.txt #8): the modern-school natives the engine's
/// primitives can genuinely express. Chivalry: Cleanse by Fire cures, Divine
/// Fury refills stamina at a timed defense cost, Remove Curse strips ONLY
/// curse-type effects (buffs survive). The combat-coupled uniques
/// (Consecrate/Enemy of One, Bushido/Ninjitsu) stay script territory like
/// the Source-X reference itself.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class ChivalrySpellTests
{
    private static (GameWorld World, SpellEngine Engine, Character Caster, Character Target) Setup(
        params SpellDef[] defs)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        foreach (var def in defs)
            registry.Register(def);
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.IsPlayer = true;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (world, engine, caster, target);
    }

    [Fact]
    public void CleanseByFire_CuresPoison()
    {
        var (_, engine, caster, target) = Setup(new SpellDef
        {
            Id = SpellType.CleanseByFire,
            Name = "Cleanse by Fire",
            Flags = SpellFlag.TargChar | SpellFlag.Good,
        });

        target.ApplyPoison(2);
        Assert.True(target.IsPoisoned);

        engine.ApplyDirectEffect(caster, target, SpellType.CleanseByFire, 500);
        Assert.False(target.IsPoisoned);
    }

    [Fact]
    public void DivineFury_RefillsStamina_AtTimedDefenseCost()
    {
        var (_, engine, caster, _) = Setup(new SpellDef
        {
            Id = SpellType.DivineFury,
            Name = "Divine Fury",
            Flags = SpellFlag.Good,
            DurationBase = 100,
        });

        caster.MaxStam = 100;
        caster.Stam = 10;
        int armorBefore = caster.ProtectionArmor;

        engine.ApplyDirectEffect(caster, caster, SpellType.DivineFury, 500);

        Assert.Equal(100, caster.Stam);
        Assert.Equal(armorBefore - 20, caster.ProtectionArmor);
    }

    [Fact]
    public void RemoveCurse_StripsCursesButKeepsBuffs()
    {
        var (_, engine, caster, target) = Setup(
            new SpellDef
            {
                Id = SpellType.Curse,
                Name = "Curse",
                Flags = SpellFlag.TargChar | SpellFlag.Curse,
                EffectBase = 50, EffectScale = 50, DurationBase = 600,
            },
            new SpellDef
            {
                Id = SpellType.Strength,
                Name = "Strength",
                Flags = SpellFlag.TargChar | SpellFlag.Good | SpellFlag.Bless,
                EffectBase = 50, EffectScale = 50, DurationBase = 600,
            },
            new SpellDef
            {
                Id = SpellType.RemoveCurse,
                Name = "Remove Curse",
                Flags = SpellFlag.TargChar | SpellFlag.Good,
            });

        short strBase = target.Str;
        engine.ApplyDirectEffect(caster, target, SpellType.Strength, 500);
        short strBuffed = target.Str;
        Assert.True(strBuffed > strBase);
        engine.ApplyDirectEffect(caster, target, SpellType.Curse, 500);
        short strCursed = target.Str;
        Assert.True(strCursed < strBuffed);

        engine.ApplyDirectEffect(caster, target, SpellType.RemoveCurse, 500);

        // The curse's stat penalty is reverted, the Strength buff remains.
        Assert.Equal(strBuffed, target.Str);
    }
}
