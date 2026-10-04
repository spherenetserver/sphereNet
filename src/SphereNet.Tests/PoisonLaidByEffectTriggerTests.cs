using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Sphere 56T custom-version compatibility: a pack whose @SpellEffect lays the poison
/// itself (POISON=&lt;strength&gt; from the caster's Poisoning) keeps that poison - the
/// spell's own, from an EFFECT of 15-20 that leaves no charges, used to replace it at
/// once and the poison ended on its first tick. With no such script the spell poisons
/// exactly as Source-X does.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PoisonLaidByEffectTriggerTests
{
    private static (SpellEngine Engine, Character Caster, Character Target) Setup(TriggerDispatcher? triggers)
    {
        var world = TestHarness.CreateWorld();
        Character.ResolveCharByUid = world.FindChar;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Poison,
            Name = "Poison",
            Flags = SpellFlag.TargChar | SpellFlag.Harm,
            EffectBase = 15, EffectScale = 5,
        });
        var engine = new SpellEngine(world, registry) { TriggerDispatcher = triggers };

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.BodyId = 0x0190;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.BodyId = 0x0190;
        target.Hits = target.MaxHits = 100;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));
        return (engine, caster, target);
    }

    [Fact]
    public void APoisonTheEffectTriggerLaidIsKept()
    {
        var triggers = new TriggerDispatcher();
        triggers.RegisterCharEvent("EVENTSPLAYER", "SpellEffect", (target, args) =>
        {
            if (args.N1 == (int)SpellType.Poison)
                ((Character)target).SetPoison(750, 15, args.CharSrc as Character);
            return TriggerResult.Default;
        });
        var (engine, caster, target) = Setup(triggers);

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Poison, 1000);

        var mem = target.Poison.Memory;
        Assert.NotNull(mem);
        Assert.Equal(15u, mem!.More2);           // the script's charges, not 0
        Assert.True(target.IsStatFlag(StatFlag.Poisoned));
    }

    [Fact]
    public void WithoutSuchAScriptTheSpellPoisonsAsSourceXDoes()
    {
        var (engine, caster, target) = Setup(new TriggerDispatcher());

        engine.ApplyScriptSpellEffect(caster, target, SpellType.Poison, 1000);

        var mem = target.Poison.Memory;
        Assert.NotNull(mem);
        Assert.True(mem!.More2 < 15);            // SetPoison(effect, effect / 50)
    }
}
