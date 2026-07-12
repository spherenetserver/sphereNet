using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 254 — Necromancy debuff tranche: Corpse Skin (elemental resist shift) and
/// Mind Rot (raised spell mana cost). Both are timed states that reuse the existing
/// ActiveSpellEffect expiry/persist/revert model, identified by spell type.
/// </summary>
public sealed class SourceXWave254Tests
{
    private static SpellDef Debuff(SpellType id) => new()
    {
        Id = id,
        Flags = SpellFlag.TargChar | SpellFlag.Good, // route to ApplySpecificSpell
        EffectBase = 15,
        EffectScale = 15,
        DurationBase = 600,
        DurationScale = 600,
    };

    private static Character MakeCaster(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.PrivLevel = PrivLevel.GM;
        ch.MaxMana = 200; ch.Mana = 200;
        world.PlaceCharacter(ch, new Point3D(120, 120, 0, 0));
        return ch;
    }

    // ---------- Corpse Skin ----------

    [Fact]
    public void CorpseSkin_ShiftsResists_ExpiryReverts()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(Debuff(SpellType.CorpseSkin));
        var caster = MakeCaster(world);
        caster.ResFire = 50; caster.ResPoison = 50; caster.ResCold = 50; caster.ResPhysical = 50;

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.CorpseSkin, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(35, caster.ResFire);     // -15
        Assert.Equal(35, caster.ResPoison);   // -15
        Assert.Equal(60, caster.ResCold);     // +10
        Assert.Equal(60, caster.ResPhysical); // +10

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(50, caster.ResFire);
        Assert.Equal(50, caster.ResPoison);
        Assert.Equal(50, caster.ResCold);
        Assert.Equal(50, caster.ResPhysical);
    }

    [Fact]
    public void CorpseSkin_SaveRevertReapply_IsSymmetric()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(Debuff(SpellType.CorpseSkin));
        var caster = MakeCaster(world);
        caster.ResFire = 40;

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.CorpseSkin, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        Assert.Equal(25, caster.ResFire);

        engine.RevertAllForSave();
        Assert.Equal(40, caster.ResFire); // clean base for the save
        engine.ReapplyAllAfterSave();
        Assert.Equal(25, caster.ResFire); // debuff restored
    }

    // ---------- Mind Rot ----------

    [Fact]
    public void MindRot_Cast_SetsState_ExpiryClears()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(Debuff(SpellType.MindRot));
        var caster = MakeCaster(world);

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.MindRot, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        Assert.True(caster.MindRotActive);

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.False(caster.MindRotActive);
    }

    [Fact]
    public void MindRot_RaisesSpellManaCostByTenPercent()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.MagicArrow,
            Flags = SpellFlag.TargChar | SpellFlag.Damage,
            ManaCost = 50,
            CastTimeBase = 1,
        });
        var engine = new SpellEngine(world, registry);

        var target = world.CreateCharacter();
        target.MaxHits = 100; target.Hits = 100;
        world.PlaceCharacter(target, new Point3D(121, 120, 0, 0));

        // Baseline: no Mind Rot → exactly ManaCost is spent (ManaLossPercent 100).
        var normal = MakeCaster(world);
        Assert.True(engine.CastStart(normal, SpellType.MagicArrow, target.Uid, target.Position) >= 0);
        Assert.True(engine.CastDone(normal));
        Assert.Equal(200 - 50, normal.Mana);

        // With Mind Rot → +10% mana cost (55 spent).
        var rotted = MakeCaster(world);
        rotted.MindRotActive = true;
        Assert.True(engine.CastStart(rotted, SpellType.MagicArrow, target.Uid, target.Position) >= 0);
        Assert.True(engine.CastDone(rotted));
        Assert.Equal(200 - 55, rotted.Mana);
    }

    [Fact]
    public void MindRot_InsufficientManaForRaisedCost_BlocksCast()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.MagicArrow,
            Flags = SpellFlag.TargChar | SpellFlag.Damage,
            ManaCost = 50,
            CastTimeBase = 1,
        });
        var engine = new SpellEngine(world, registry);

        var target = world.CreateCharacter();
        world.PlaceCharacter(target, new Point3D(121, 120, 0, 0));

        var caster = MakeCaster(world);
        caster.MindRotActive = true;
        caster.MaxMana = 54; caster.Mana = 54; // enough for 50, short of 55

        Assert.Equal(-1, engine.CastStart(caster, SpellType.MagicArrow, target.Uid, target.Position));
    }
}
