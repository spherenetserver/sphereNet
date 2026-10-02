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

    private static Character PayingCaster(GameWorld world)
    {
        var ch = MakeCaster(world);
        ch.PrivLevel = PrivLevel.Player;
        ch.SetSkill(SkillType.Magery, 1000);
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
    public void CorpseSkin_MemoryRemoval_IsSymmetric()
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

        // The effect is its LAYER_SPELL_Corpse_Skin memory: deleting it - by any road -
        // takes back exactly the shift it put on (Spell_Effect_Remove, CCharSpell.cpp:776-787).
        Assert.True(engine.RemoveEffectByMemory(caster.FindLayer(SpellLayers.CorpseSkin)!));
        Assert.Equal(40, caster.ResFire);
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
        // 10 off LOWERMANACOST, the memory's level 10 (CCharSpell.cpp:1351-1354).
        Assert.Equal(-10, caster.Tags.GetInt("LOWERMANACOST"));
        Assert.Equal(10, (ushort)caster.FindLayer(SpellLayers.MindRot)!.MoreP.Y);

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(0, caster.Tags.GetInt("LOWERMANACOST"));
    }

    [Fact]
    public void MindRot_RaisesSpellManaCostThroughLowerManaCost()
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
        // A non-GM creature: a GM pays no mana at all (Spell_CanCast,
        // CCharSpell.cpp:2461), and a creature needs no book or reagents.
        var normal = PayingCaster(world);
        Assert.True(engine.CastStart(normal, SpellType.MagicArrow, target.Uid, target.Position) >= 0);
        Assert.True(engine.CastDone(normal));
        Assert.Equal(200 - 50, normal.Mana);

        // With Mind Rot (LOWERMANACOST -10) → 55 spent.
        var rotted = PayingCaster(world);
        rotted.SetTag("LOWERMANACOST", "-10");
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

        var caster = PayingCaster(world);
        caster.SetTag("LOWERMANACOST", "-10");
        caster.MaxMana = 54; caster.Mana = 54; // enough for 50, short of 55

        Assert.Equal(-1, engine.CastStart(caster, SpellType.MagicArrow, target.Uid, target.Position));
    }
}
