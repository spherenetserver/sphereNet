using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;
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
/// İş 03 / C3 — spell-effect removal reentrancy. ProcessExpirations and the
/// removal helpers used to walk the live effect list by index while firing
/// removal callbacks (@EffectRemove / OnSpellEffectRemove). A callback that
/// removed other effects on the same target (the classic case: it kills the
/// target, which clears every remaining effect) shrank the list under the
/// index and the next [i] access threw ArgumentOutOfRangeException — on the
/// main tick, which rethrew and took the server down.
///
/// These drive real effects - each one its worn IT_SPELL memory - with a timer that
/// has already run out, and a reentrant OnSpellEffectRemove callback, asserting the
/// pass completes, every effect is retired exactly once, and every modifier is taken
/// back exactly once (no double subtraction).
/// </summary>
[Collection("GlobalConfigSerial")]
public sealed class SpellEffectReentrancyTests
{
    private static readonly FieldInfo s_effects =
        typeof(SpellEngine).GetField("_effects", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("_effects not found");

    private static int EffectCount(SpellEngine engine) =>
        ((IEnumerable)s_effects.GetValue(engine)!).Cast<object>().Count();

    private static (GameWorld world, SpellEngine engine, Character caster, Character target) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        Character.ResolveCharByUid = world.FindChar;
        Character.MagicFlags = 0;

        var caster = world.CreateCharacter();
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        var target = world.CreateCharacter();
        target.Str = 50;
        target.Dex = 50;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        var registry = new SpellRegistry();
        foreach (var spell in new[] { SpellType.Clumsy, SpellType.NightSight, SpellType.ReactiveArmor,
                     SpellType.Invisibility, SpellType.Strength })
        {
            registry.Register(new SpellDef
            {
                Id = spell, Name = spell.ToString(), Flags = SpellFlag.TargChar | SpellFlag.Good,
                EffectBase = 5, EffectScale = 5, DurationBase = 600, DurationScale = 600,
            });
        }
        var engine = new SpellEngine(world, registry);
        return (world, engine, caster, target);
    }

    // Put a real effect on the target and make its memory's timer already due, so
    // ProcessExpirations retires it deterministically.
    private static Item AddEffect(SpellEngine engine, Character caster, Character target, SpellType spell)
    {
        engine.ApplyDirectEffect(caster, target, spell, 1000);
        var mem = target.Memories.Single(m => !m.IsDeleted && m.ItemType == ItemType.Spell &&
            m.MoreP.X == (int)spell);
        mem.SetTimeout(1);
        return mem;
    }

    [Fact]
    public void ProcessExpirations_RemovalCallbackKillsTargetAndClearsRest_CompletesSafely()
    {
        var (_, engine, caster, target) = Setup();
        // Three different spell layers: stat spells share LAYER_SPELL_STATS and
        // would replace each other (Spell_Effect_Create).
        AddEffect(engine, caster, target, SpellType.Clumsy);
        AddEffect(engine, caster, target, SpellType.NightSight);
        AddEffect(engine, caster, target, SpellType.ReactiveArmor);
        Assert.Equal(3, EffectCount(engine));
        Assert.Equal(45, CombatEngine.EffectiveDex(target)); // 50 base - 5 on the modifier

        int removed = 0;
        bool cascaded = false;
        Character.OnSpellEffectRemove = (ch, _, _, _) =>
        {
            removed++;
            // First removal "kills" the target: clear all its remaining effects
            // mid-pass — exactly the interleaving that overran the old index loop.
            if (!cascaded)
            {
                cascaded = true;
                engine.ClearAllEffectsOnDeath(ch);
            }
            return TriggerResult.Default;
        };

        try
        {
            var ex = Record.Exception(() => engine.ProcessExpirations(Environment.TickCount64));
            Assert.Null(ex);                       // no ArgumentOutOfRangeException
            Assert.Equal(0, EffectCount(engine));  // every effect retired
            Assert.Equal(3, removed);              // each observed exactly once
            Assert.Equal(50, CombatEngine.EffectiveDex(target)); // taken back exactly once
            Assert.Equal(0, target.ModDex);
        }
        finally
        {
            Character.OnSpellEffectRemove = null;
        }
    }

    [Fact]
    public void ClearAllEffectsOnDeath_ReentrantCallbackStripsRest_CompletesSafely()
    {
        var (_, engine, caster, target) = Setup();
        // Three different spell layers: stat spells share LAYER_SPELL_STATS and
        // would replace each other (Spell_Effect_Create).
        AddEffect(engine, caster, target, SpellType.Clumsy);
        AddEffect(engine, caster, target, SpellType.NightSight);
        AddEffect(engine, caster, target, SpellType.ReactiveArmor);

        int removed = 0;
        bool cascaded = false;
        Character.OnSpellEffectRemove = (ch, _, _, _) =>
        {
            removed++;
            if (!cascaded)
            {
                cascaded = true;
                engine.StripDispellableEffects(ch); // reentrant removal of the rest
            }
            return TriggerResult.Default;
        };

        try
        {
            var ex = Record.Exception(() => engine.ClearAllEffectsOnDeath(target));
            Assert.Null(ex);
            Assert.Equal(0, EffectCount(engine));
            Assert.Equal(3, removed);
            Assert.Equal(50, CombatEngine.EffectiveDex(target));
        }
        finally
        {
            Character.OnSpellEffectRemove = null;
        }
    }

    [Fact]
    public void BreakInvisibility_CallbackClearsOtherEffects_CompletesSafely()
    {
        var (_, engine, caster, target) = Setup();
        AddEffect(engine, caster, target, SpellType.Invisibility);
        AddEffect(engine, caster, target, SpellType.Strength);
        AddEffect(engine, caster, target, SpellType.NightSight);
        Assert.Equal(3, EffectCount(engine));

        bool cascaded = false;
        Character.OnSpellEffectRemove = (ch, _, _, _) =>
        {
            if (!cascaded)
            {
                cascaded = true;
                engine.ClearAllEffectsOnDeath(ch); // remove the other effects mid-pass
            }
            return TriggerResult.Default;
        };

        try
        {
            var ex = Record.Exception(() => engine.BreakInvisibility(target));
            Assert.Null(ex);
            Assert.Equal(0, EffectCount(engine)); // invisibility + cascaded rest all gone
            Assert.Equal(50, CombatEngine.EffectiveStr(target)); // taken back exactly once
            Assert.Equal(0, target.ModStr);
        }
        finally
        {
            Character.OnSpellEffectRemove = null;
        }
    }

    [Fact]
    public void ProcessExpirations_NoReentrancy_StillRetiresAllExpiredEffects()
    {
        // Baseline: the snapshot rewrite must not regress the ordinary path.
        var (_, engine, caster, target) = Setup();
        AddEffect(engine, caster, target, SpellType.Clumsy);
        AddEffect(engine, caster, target, SpellType.NightSight);

        int removed = 0;
        Character.OnSpellEffectRemove = (_, _, _, _) => { removed++; return TriggerResult.Default; };
        try
        {
            engine.ProcessExpirations(Environment.TickCount64);
            Assert.Equal(0, EffectCount(engine));
            Assert.Equal(2, removed);
            Assert.Equal(50, CombatEngine.EffectiveDex(target));
        }
        finally
        {
            Character.OnSpellEffectRemove = null;
        }
    }
}
