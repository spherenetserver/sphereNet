using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 258 — Necromancy form tranche: Lich Form (elemental resist shift) and
/// Vampiric Embrace (fire-resist shift + life-leech on hit). Both are
/// LAYER_SPELL_Polymorph forms following the WraithForm template, so casting one
/// replaces another.
/// </summary>
public sealed class SourceXWave258Tests
{
    private static SpellDef Form(SpellType id) => new()
    {
        Id = id,
        Flags = SpellFlag.TargChar | SpellFlag.Good,
        DurationBase = 600,
        DurationScale = 600,
    };

    private static (GameWorld world, SpellEngine engine, Character caster) Setup(params SpellType[] ids)
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        foreach (var id in ids) registry.Register(Form(id));
        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        caster.ResFire = 50; caster.ResPoison = 50; caster.ResCold = 50;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (world, new SpellEngine(world, registry), caster);
    }

    [Fact]
    public void LichForm_ShiftsResists_SetsForm_ExpiryReverts()
    {
        var (_, engine, caster) = Setup(SpellType.LichForm);

        Assert.True(engine.CastStart(caster, SpellType.LichForm, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(40, caster.ResFire);   // -10
        Assert.Equal(60, caster.ResPoison); // +10
        Assert.Equal(60, caster.ResCold);   // +10
        Assert.True(caster.LichFormActive);
        Assert.True(caster.IsStatFlag(StatFlag.Polymorph));

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(50, caster.ResFire);
        Assert.Equal(50, caster.ResPoison);
        Assert.Equal(50, caster.ResCold);
        Assert.False(caster.LichFormActive);
        Assert.False(caster.IsStatFlag(StatFlag.Polymorph));
    }

    [Fact]
    public void VampiricEmbrace_ShiftsFireResist_SetsForm_ExpiryReverts()
    {
        var (_, engine, caster) = Setup(SpellType.VampiricEmbrace);

        Assert.True(engine.CastStart(caster, SpellType.VampiricEmbrace, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(40, caster.ResFire); // -10
        Assert.True(caster.VampiricEmbraceActive);

        engine.ProcessExpirations(Environment.TickCount64 + 120_000);
        Assert.Equal(50, caster.ResFire);
        Assert.False(caster.VampiricEmbraceActive);
    }

    [Fact]
    public void VampiricEmbrace_LeechesLifeOnAnyHit_Unarmed()
    {
        var world = TestHarness.CreateWorld();
        var attacker = world.CreateCharacter();
        attacker.MaxHits = 100; attacker.Hits = 40;
        attacker.VampiricEmbraceActive = true;
        world.PlaceCharacter(attacker, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.MaxHits = 100; target.Hits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        bool leeched = false;
        var prev = CombatEngine.OnLeechEffect;
        CombatEngine.OnLeechEffect = _ => leeched = true;
        try
        {
            // No weapon: the form still leeches (unlike Curse Weapon).
            CombatEngine.ApplyAosOnHitEffects(attacker, target, damage: 100, weapon: null, flags: default);
            Assert.True(leeched);
            Assert.True(attacker.Hits >= 40); // healed, never reduced
        }
        finally { CombatEngine.OnLeechEffect = prev; }
    }

    [Fact]
    public void CastingLichForm_ReplacesWraithForm()
    {
        var (_, engine, caster) = Setup(SpellType.WraithForm, SpellType.LichForm);

        Assert.True(engine.CastStart(caster, SpellType.WraithForm, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));
        Assert.True(caster.WraithFormActive);

        Assert.True(engine.CastStart(caster, SpellType.LichForm, caster.Uid, caster.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        // One LAYER_SPELL_Polymorph form at a time: Lich replaces Wraith.
        Assert.True(caster.LichFormActive);
        Assert.False(caster.WraithFormActive);
    }
}
