using System.Linq;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 261 — Necromancy Summon Familiar: routes through the summon dispatch with
/// a default familiar body (the Source-X creature-selection menu is not yet wired).
/// The familiar is a caster-owned, conjured, timed summon.
/// </summary>
public sealed class SourceXWave261Tests
{
    [Fact]
    public void SummonFamiliar_SummonsTheMenuPick_OwnedByCaster()
    {
        var world = TestHarness.CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.SummonFamiliar,
            Flags = SpellFlag.Summon,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 1200,
            DurationScale = 1200,
        });
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var spawnAt = new Point3D(101, 100, 0, 0);

        // No creature of its own: Spell_Summon_Try has no case for the spell
        // (CCharSpell.cpp:2602-2681), so a cast with no menu pick summons nothing.
        Assert.True(engine.CastStart(caster, SpellType.SummonFamiliar, Serial.Invalid, spawnAt) >= 0);
        Assert.False(engine.CastDone(caster));
        Assert.DoesNotContain(world.GetCharsInRange(caster.Position, 4), c => c != caster);

        // The sm_summon_familiar menu's pick is what gets summoned.
        caster.SetTag("SUMMON_SELECT", "c_familiar_pick");
        Assert.True(engine.CastStart(caster, SpellType.SummonFamiliar, Serial.Invalid, spawnAt) >= 0);
        Assert.True(engine.CastDone(caster));

        var familiar = world.GetCharsInRange(caster.Position, 4)
            .FirstOrDefault(c => c != caster);

        Assert.NotNull(familiar);
        Assert.Equal(caster.Uid, familiar!.OwnerSerial);
        Assert.True(familiar.IsSummoned);
    }
}
