using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the wired @EffectAdd trigger (fires when a temporary spell effect is
// registered, SpellEngine) through the static Character.OnEffectAdd hook, nulled
// between tests by ResetEngineStatics.
//
// @PersonalSpace is wired at MovementEngine's shove point (Character.OnPersonalSpace)
// and locked by the trigger-coverage guardrail, but has no behaviour test here: the
// shove path only runs on the full terrain algorithm (_world.MapData != null), and
// the in-memory test world has no MapData, so movement takes the bypass branch.
public class PersonalSpaceEffectTriggerTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void EffectAdd_SpellEffectScheduled_FiresTrigger()
    {
        var world = CreateWorld();
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Polymorph,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 5,
            DurationScale = 5,
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 1000);
        caster.BodyId = 0x0190;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        int effectSpell = -1;
        Character? effectTarget = null;
        Character.OnEffectAdd = (t, spellId) => { effectTarget = t; effectSpell = spellId; };

        var engine = new SpellEngine(world, registry);
        Assert.True(engine.CastStart(caster, SpellType.Polymorph, caster.Uid, caster.Position) > 0);
        Assert.True(engine.CastDone(caster)); // applies the polymorph effect

        Assert.Same(caster, effectTarget);
        Assert.Equal((int)SpellType.Polymorph, effectSpell);
    }
}
