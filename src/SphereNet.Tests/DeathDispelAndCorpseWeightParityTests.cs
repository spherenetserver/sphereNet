using System;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Death;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What death ends, and what a corpse will hold (port plan İŞ-31 / PLAN-406).
///
/// Source-X CChar::Death runs Spell_Dispel(100) before the corpse is made
/// (CCharAct.cpp:4397) - "get rid of all spell effects". Nothing did that here, so a
/// player who died buffed rose still buffed and kept it until the timers ran out, and
/// a curse outlived the death that ended it.
///
/// MakeCorpse also bounds the corpse: "set corpse maxweight to prevent weird exploits
/// like when someone place many items on an player corpse just to make this player get
/// stuck on resurrect" (CItemCorpse.cpp:194). The drop path here already enforces a
/// container's MODMAXWEIGHT - the corpse simply had none.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class DeathDispelAndCorpseWeightParityTests
{
    private static SpellEngine Spells(GameWorld world)
    {
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Strength,
            Name = "Strength",
            Flags = SpellFlag.TargChar | SpellFlag.Bless | SpellFlag.Good,
            ManaCost = 0,
            CastTimeBase = 1,
            EffectBase = 8,
            EffectScale = 8,
            DurationBase = 1200,
            DurationScale = 1200,
        });
        return new SpellEngine(world, registry);
    }

    private static Character Victim(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM;      // no cast failures in the fixture
        ch.Str = 50;
        ch.MaxHits = 50;
        ch.Hits = 50;
        ch.MaxMana = 100;
        ch.Mana = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Equip(pack, Layer.Pack);
        return ch;
    }

    // ---- the dispel ----------------------------------------------------

    [Fact]
    public void DeathEndsEveryActiveSpellEffect()
    {
        var world = TestHarness.CreateWorld();
        var spells = Spells(world);
        var deaths = new DeathEngine(world) { DispelEffectsHook = spells.StripDispellableEffects };
        var victim = Victim(world);
        victim.Str = 40;

        victim.BeginCast(SpellType.Strength, victim.Uid, victim.Position);
        Assert.True(spells.CastDone(victim));
        Assert.True(victim.Str > 40);   // the buff is on

        deaths.ProcessDeath(victim);

        Assert.True(victim.IsDead);
        // The delta is reverted and nothing is left to persist: the buff does not
        // survive into the ghost, nor past the resurrection.
        Assert.Equal(40, victim.Str);
        Assert.Empty(spells.GetPersistedEffectRecords(victim, Environment.TickCount64));
    }

    [Fact]
    public void ACurseDoesNotOutliveTheDeathThatEndedIt()
    {
        // Spell_Dispel(100) is indiscriminate: it ends what helped you and what
        // hurt you alike, so a ghost does not rise still cursed.
        var world = TestHarness.CreateWorld();
        var spells = Spells(world);
        var deaths = new DeathEngine(world) { DispelEffectsHook = spells.StripDispellableEffects };
        var caster = Victim(world);
        var victim = Victim(world);
        victim.Str = 40;

        caster.BeginCast(SpellType.Strength, victim.Uid, victim.Position);
        Assert.True(spells.CastDone(caster));
        Assert.True(victim.Str > 40);

        deaths.ProcessDeath(victim, caster);

        Assert.Equal(40, victim.Str);
        Assert.Empty(spells.GetPersistedEffectRecords(victim, Environment.TickCount64));
    }

    [Fact]
    public void WithoutAHostTheDeathStillCompletes()
    {
        // Bare test setups leave the hook null; death must not depend on it.
        var world = TestHarness.CreateWorld();
        var deaths = new DeathEngine(world);
        var victim = Victim(world);

        deaths.ProcessDeath(victim);

        Assert.True(victim.IsDead);
    }

    // ---- the corpse weight bound ---------------------------------------

    [Fact]
    public void ACorpseHoldsNoMoreThanItsOwnerCouldCarry()
    {
        var world = TestHarness.CreateWorld();
        var deaths = new DeathEngine(world);
        var victim = Victim(world);
        int expected = victim.MaxWeight;

        var corpse = deaths.ProcessDeath(victim);

        Assert.NotNull(corpse);
        Assert.Equal(ItemType.Corpse, corpse!.ItemType);
        Assert.Equal(expected, corpse.ModMaxWeight);
        Assert.True(corpse.ModMaxWeight > 0);
    }

    [Fact]
    public void AStrongerVictimLeavesARoomierCorpse()
    {
        // The bound is the victim's own carry limit, not a constant.
        var world = TestHarness.CreateWorld();
        var deaths = new DeathEngine(world);

        var weak = Victim(world);
        var strong = Victim(world);
        strong.Str = 100;

        var weakCorpse = deaths.ProcessDeath(weak);
        var strongCorpse = deaths.ProcessDeath(strong);

        Assert.NotNull(weakCorpse);
        Assert.NotNull(strongCorpse);
        Assert.True(strongCorpse!.ModMaxWeight > weakCorpse!.ModMaxWeight);
    }
}
