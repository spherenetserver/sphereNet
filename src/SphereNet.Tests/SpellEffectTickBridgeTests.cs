using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using SphereNet.Scripting.Execution;
using SphereNet.Scripting.Expressions;
using SphereNet.Scripting.Resources;
using SphereNet.Scripting.Variables;
using Xunit;
using GameTriggerArgs = SphereNet.Game.Scripting.TriggerArgs;

namespace SphereNet.Tests;

// @SpellEffectTick bridge (Source-X SPELLFLAG_TICK): the native poison tick
// fires Character.OnSpellEffectTick before applying damage; the script LOCAL
// contract travels through TriggerArgs.Locals — one VarMap shared by every
// ON=@X block in the chain (CScriptTriggerArgs.m_VarsLocal parity), seeded
// by the engine and read back after the fire. The hook is nulled between
// tests by ResetEngineStatics.
public class SpellEffectTickBridgeTests
{
    private static Character SetupVictim()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var victim = world.CreateCharacter();
        victim.IsPlayer = true;
        victim.MaxHits = 100;
        victim.Hits = 100;
        world.PlaceCharacter(victim, new Point3D(100, 100, 0, 0));
        return victim;
    }

    [Fact]
    public void PoisonTick_SeedsContract_AndAppliesScriptDamageAsIs()
    {
        var victim = SetupVictim();
        SpellEffectTickContext? seen = null;
        Character.OnSpellEffectTick = (_, ctx) => { seen = ctx; ctx.Damage = 3; return true; };

        victim.ApplyPoison(2, Serial.Invalid);
        int damage = victim.ProcessPoisonTick(Environment.TickCount64 + 60_000);

        Assert.Equal(3, damage);
        Assert.Equal(97, victim.Hits);
        Assert.NotNull(seen);
        Assert.Equal((int)SpellType.Poison, seen!.SpellId);
        Assert.Equal(2, seen.Level);
        Assert.Equal(300, seen.Strength);          // level 2 → normal band midpoint
        Assert.Equal(8, seen.Charges);              // seeded before the auto-decrement
    }

    [Fact]
    public void PoisonTick_HookReturnsFalse_DestroysEffectWithoutDamage()
    {
        var victim = SetupVictim();
        Character.OnSpellEffectTick = (_, _) => false; // script RETURN 1

        victim.ApplyPoison(5, Serial.Invalid);
        int damage = victim.ProcessPoisonTick(Environment.TickCount64 + 60_000);

        Assert.Equal(0, damage);
        Assert.Equal(100, victim.Hits);
        Assert.False(victim.IsPoisoned); // memory destroyed = cured
    }

    [Fact]
    public void PoisonTick_ScriptZeroDamage_SkipsApplicationButKeepsSchedule()
    {
        var victim = SetupVictim();
        Character.OnSpellEffectTick = (_, ctx) => { ctx.Damage = 0; return true; };

        victim.ApplyPoison(3, Serial.Invalid);
        int damage = victim.ProcessPoisonTick(Environment.TickCount64 + 60_000);

        Assert.Equal(0, damage);
        Assert.Equal(100, victim.Hits);
        Assert.True(victim.IsPoisoned); // charges remain, effect continues
    }

    [Fact]
    public void SharedLocals_FlowBothWays_AndArgoShimAnswersMemoryReads()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
        string tmp = Path.Combine(Path.GetTempPath(), $"sphnet_tickbridge_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tmp, """
            [EVENTS e_tick_probe]
            ON=@SpellEffectTick
            TAG.SEEN_EFFECT=<LOCAL.EFFECT>
            TAG.SEEN_MOREY=<ARGO.MOREY>
            LOCAL.EFFECT 3
            RETURN 0
            """);
        try
        {
            resources.LoadResourceFile(tmp);
            var interpreter = new ScriptInterpreter(
                new ExpressionParser(), loggerFactory.CreateLogger<ScriptInterpreter>());
            var runner = new TriggerRunner(
                interpreter, resources, loggerFactory.CreateLogger<TriggerRunner>());
            var dispatcher = new TriggerDispatcher { Resources = resources, Runner = runner };

            var victim = new Character();
            victim.Events.Add(ResourceId.FromEventName("e_tick_probe"));

            var locals = new VarMap();
            locals.Set("EFFECT", "7"); // engine-seeded tick damage
            var args = new GameTriggerArgs
            {
                CharSrc = victim,
                N1 = (int)SpellType.Poison,
                N2 = 300,
                O1 = new SpellMemoryShim { SpellId = (int)SpellType.Poison, MoreY = 300 },
                Locals = locals,
            };
            var result = dispatcher.FireCharTrigger(victim, CharTrigger.SpellEffectTick, args);

            Assert.NotEqual(TriggerResult.True, result);
            Assert.True(victim.TryGetProperty("TAG.SEEN_EFFECT", out var seenEffect));
            Assert.Equal("7", seenEffect);                  // seeded value visible to the script
            Assert.True(victim.TryGetProperty("TAG.SEEN_MOREY", out var seenMorey));
            Assert.Equal("300", seenMorey);                 // ARGO shim memory read
            Assert.Equal(3, locals.GetInt("EFFECT"));       // script write visible to the engine
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
