using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 255 — generic periodic damage-over-time engine and its first two
/// Necromancy consumers: Pain Spike (fixed direct damage over 10 one-second
/// ticks) and Strangle (poison damage that accelerates and scales with the
/// victim's fatigue). Ticks are advanced by ProcessExpirations.
/// </summary>
public sealed class SourceXWave255Tests
{
    private static (GameWorld world, SpellEngine engine, Character caster, Character target)
        Setup(SpellType id)
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = id,
            Flags = SpellFlag.TargChar,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 600,
            DurationScale = 600,
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        caster.SetSkill(SkillType.SpiritSpeak, 1000);
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.MaxHits = 100; target.Hits = 100;
        target.MaxStam = 100; target.Stam = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (world, engine, caster, target);
    }

    // ---------- Pain Spike ----------

    [Fact]
    public void PainSpike_DealsFixedDirectDamageOverTenTicks_IgnoringResist()
    {
        var (_, engine, caster, target) = Setup(SpellType.PainSpike);
        target.SetSkill(SkillType.MagicResistance, 0); // total = (1000-0)/100 + 18 = 28 -> 2/tick
        target.ResPhysical = 90;                        // direct damage must ignore this

        Assert.True(engine.CastStart(caster, SpellType.PainSpike, target.Uid, target.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        long t = Environment.TickCount64;
        for (int k = 1; k <= 10; k++)
            engine.ProcessExpirations(t + k * 1000 + 100);

        Assert.Equal(80, target.Hits); // 10 ticks * 2 = 20 damage, resist ignored

        // Charges spent: further processing does nothing.
        engine.ProcessExpirations(t + 100_000);
        Assert.Equal(80, target.Hits);
    }

    [Fact]
    public void PainSpike_NoDamageBeforeFirstTickInterval()
    {
        var (_, engine, caster, target) = Setup(SpellType.PainSpike);
        target.SetSkill(SkillType.MagicResistance, 0);

        Assert.True(engine.CastStart(caster, SpellType.PainSpike, target.Uid, target.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        // Same tick as the cast: nothing is due yet.
        engine.ProcessExpirations(Environment.TickCount64);
        Assert.Equal(100, target.Hits);
    }

    // ---------- Strangle ----------

    [Fact]
    public void Strangle_DealsPoisonDamageAndTerminatesAfterCharges()
    {
        var (_, engine, caster, target) = Setup(SpellType.Strangle);
        target.SetSkill(SkillType.MagicResistance, 0);

        Assert.True(engine.CastStart(caster, SpellType.Strangle, target.Uid, target.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        long t = Environment.TickCount64;
        for (int k = 1; k <= 12; k++) // power = 1000/100 = 10 charges
            engine.ProcessExpirations(t + k * 10_000);

        Assert.True(target.Hits < 100); // took poison damage
        int afterCharges = target.Hits;

        engine.ProcessExpirations(t + 500_000);
        Assert.Equal(afterCharges, target.Hits); // no further ticks after charges spent
    }

    [Fact]
    public void Strangle_FatigueIncreasesDamage()
    {
        // Two identical victims; the fatigued one takes strictly more per the
        // 3 - 2*curStam/maxStam multiplier (x1 at full stamina, up to x3 empty).
        int DamageWithStamina(short stam)
        {
            var (world, engine, caster, target) = Setup(SpellType.Strangle);
            target.SetSkill(SkillType.MagicResistance, 0);
            target.MaxStam = 100; target.Stam = stam;
            target.MaxHits = 1000; target.Hits = 1000;
            Assert.True(engine.CastStart(caster, SpellType.Strangle, target.Uid, target.Position) >= 0);
            Assert.True(engine.CastDone(caster));
            long t = Environment.TickCount64;
            for (int k = 1; k <= 12; k++)
                engine.ProcessExpirations(t + k * 10_000);
            return 1000 - target.Hits;
        }

        // Full stamina -> multiplier 1; empty stamina -> multiplier 3. Averaged
        // over 10 ticks the empty-stamina total is comfortably larger despite RNG.
        int full = DamageWithStamina(100);
        int empty = DamageWithStamina(0);
        Assert.True(empty > full, $"empty-stamina Strangle ({empty}) should exceed full ({full})");
    }
}
