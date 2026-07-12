using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 257 — Necromancy area-damage tranche: Poison Strike (direct poison to the
/// primary, half-splash within 2 tiles) and Wither (cold damage to everything
/// within 4 tiles of the caster). Implemented as explicit ApplySpecificSpell
/// cases so no reference-script flag edit is required.
/// </summary>
public sealed class SourceXWave257Tests
{
    private static (GameWorld world, SpellEngine engine, Character caster) Arena(SpellType id)
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = id,
            Flags = SpellFlag.TargChar, // routes to ApplySpecificSpell; no EvalInt scaling
            ManaCost = 0,
            CastTimeBase = 1,
            EffectBase = 40,
            EffectScale = 40, // base == scale -> effect is exactly 40
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        caster.MaxHits = 100; caster.Hits = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (world, engine, caster);
    }

    private static Character Victim(GameWorld world, int x, int y)
    {
        var v = world.CreateCharacter();
        v.MaxHits = 100; v.Hits = 100;
        v.ResPoison = 0; v.ResCold = 0; v.ResPhysical = 0;
        world.PlaceCharacter(v, new Point3D((short)x, (short)y, 0, 0));
        return v;
    }

    [Fact]
    public void PoisonStrike_HitsPrimaryFull_SplashHalf_SparesDistant()
    {
        var (world, engine, caster) = Arena(SpellType.PoisonStrike);
        var primary = Victim(world, 101, 100);
        var near = Victim(world, 102, 100);  // within 2 of primary
        var far = Victim(world, 108, 100);   // outside 2 of primary

        Assert.True(engine.CastStart(caster, SpellType.PoisonStrike, primary.Uid, primary.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(60, primary.Hits); // 100 - 40
        Assert.Equal(80, near.Hits);    // 100 - 20 (half splash)
        Assert.Equal(100, far.Hits);    // out of splash
        Assert.Equal(100, caster.Hits); // caster never splashed
    }

    [Fact]
    public void Wither_HitsEveryoneWithinFourTilesOfCaster_ExceptCaster()
    {
        var (world, engine, caster) = Arena(SpellType.Wither);
        var near1 = Victim(world, 102, 100); // 2 tiles
        var near2 = Victim(world, 100, 104); // 4 tiles
        var far = Victim(world, 106, 100);   // 6 tiles

        Assert.True(engine.CastStart(caster, SpellType.Wither, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(60, near1.Hits); // 100 - 40 cold
        Assert.Equal(60, near2.Hits);
        Assert.Equal(100, far.Hits);
        Assert.Equal(100, caster.Hits);
    }

    [Fact]
    public void PoisonStrike_ResistReducesDamage()
    {
        var (world, engine, caster) = Arena(SpellType.PoisonStrike);
        var primary = Victim(world, 101, 100);
        primary.ResPoison = 50; // halve the poison damage

        Assert.True(engine.CastStart(caster, SpellType.PoisonStrike, primary.Uid, primary.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(80, primary.Hits); // 100 - (40 * (100-50)/100 = 20)
    }
}
