using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Wave W-C (wiki/hedef.txt) — magic trigger/effect parity:
//   * @SpellEffect fires on the AFFECTED char (not the caster) with SRC = the
//     caster, ARGN1 = spell, ARGN2 = skill level, LOCAL.Effect/Resist/Duration
//     seeded; RETURN 1 cancels the effect on that target, LOCAL.Duration is
//     read back (Source-X CChar::OnSpellEffect)
//   * paralyze breaks when the victim takes damage (Source-X OnTakeDamage
//     deletes the LAYER_SPELL_Paralyze memory)
//   * Dispel strips active buff/curse effects, not just conjured creatures
public class ParityWaveCTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    private static (SpellEngine engine, Character caster, Character target) Setup(
        GameWorld world, SpellDef def)
    {
        var registry = new SpellRegistry();
        registry.Register(def);
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM; // deterministic: no fizzle/LOS surprises
        caster.MaxMana = caster.Mana = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));

        var target = world.CreateCharacter();
        target.IsPlayer = true;
        target.MaxHits = 100;
        target.Hits = 50;
        world.PlaceCharacter(target, new Point3D(101, 100, 0, 0));

        return (engine, caster, target);
    }

    private static SpellDef HealDef() => new()
    {
        Id = SpellType.Heal,
        Name = "Heal",
        Flags = SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Good,
        ManaCost = 0,
        CastTimeBase = 1,
        EffectBase = 10,
        EffectScale = 10,
    };

    [Fact]
    public void SpellEffect_FiresOnTarget_WithCasterAsSrc_AndLocalEffect()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_spellfx_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_spellfx_probe]
            ON=@SpellEffect
            TAG.GOTSRC=<SRC.UID>
            TAG.GOTN1=<ARGN1>
            TAG.GOTEFFECT=<LOCAL.Effect>
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = CreateWorld();
            var (engine, caster, target) = Setup(world, HealDef());
            engine.TriggerDispatcher = stack.Dispatcher;
            target.Events.Add(stack.Resources.ResolveDefName("e_spellfx_probe"));

            caster.BeginCast(SpellType.Heal, target.Uid, target.Position);
            Assert.True(engine.CastDone(caster));

            // The trigger ran on the TARGET (pre-W-C it fired on the caster only).
            Assert.True(target.TryGetTag("GOTSRC", out var src));
            Assert.Equal(caster.Uid.Value, ParseUid(src!));
            Assert.True(target.TryGetTag("GOTN1", out var n1) && n1 == ((int)SpellType.Heal).ToString());
            // LOCAL.Effect was seeded with the computed potency (a positive heal).
            Assert.True(target.TryGetTag("GOTEFFECT", out var eff) && long.Parse(eff!) > 0);
            // ...and the heal itself applied.
            Assert.True(target.Hits > 50);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SpellEffect_Return1_CancelsEffectOnTarget()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_spellfxret_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_spellfx_veto]
            ON=@SpellEffect
            RETURN 1
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = CreateWorld();
            var (engine, caster, target) = Setup(world, HealDef());
            engine.TriggerDispatcher = stack.Dispatcher;
            target.Events.Add(stack.Resources.ResolveDefName("e_spellfx_veto"));

            caster.BeginCast(SpellType.Heal, target.Uid, target.Position);
            engine.CastDone(caster);

            Assert.Equal(50, target.Hits); // effect vetoed on this target
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void SpellEffect_LocalDurationRewrite_ShortensBuff()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_spellfxdur_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [EVENTS e_spellfx_dur]
            ON=@SpellEffect
            LOCAL.Duration=50
            """);
        try
        {
            var stack = ScriptTestBootstrap.CreateRuntimeStack();
            stack.Resources.LoadResourceFile(tempFile);

            var world = CreateWorld();
            var def = new SpellDef
            {
                Id = SpellType.Strength,
                Name = "Strength",
                Flags = SpellFlag.TargChar | SpellFlag.Bless | SpellFlag.Good,
                ManaCost = 0,
                CastTimeBase = 1,
                EffectBase = 5,
                EffectScale = 5,
                DurationBase = 1200, // 120s by the def curve
                DurationScale = 1200,
            };
            var (engine, caster, target) = Setup(world, def);
            engine.TriggerDispatcher = stack.Dispatcher;
            target.Events.Add(stack.Resources.ResolveDefName("e_spellfx_dur"));

            caster.BeginCast(SpellType.Strength, target.Uid, target.Position);
            Assert.True(engine.CastDone(caster));

            // Serialized record: version|spell|remainingMs|... — the script's
            // LOCAL.Duration=50 (5.0s) must beat the 120s def curve.
            var record = Assert.Single(engine.GetPersistedEffectRecords(target, Environment.TickCount64));
            long remainingMs = long.Parse(record.Split('|')[2]);
            Assert.InRange(remainingMs, 1, 5_000);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void Paralyze_BreaksWhenVictimTakesDamage()
    {
        var world = CreateWorld();
        var def = new SpellDef
        {
            Id = SpellType.Paralyze,
            Name = "Paralyze",
            Flags = SpellFlag.TargChar,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 600,
            DurationScale = 600,
        };
        var (engine, caster, target) = Setup(world, def);

        caster.BeginCast(SpellType.Paralyze, target.Uid, target.Position);
        Assert.True(engine.CastDone(caster));
        Assert.True(target.IsStatFlag(StatFlag.Freeze));

        // Damage routes through TryInterruptFromDamage on every path — the
        // classic "hit breaks paralyze" interaction.
        engine.TryInterruptFromDamage(target, 5);

        Assert.False(target.IsStatFlag(StatFlag.Freeze));
        Assert.Empty(engine.GetPersistedEffectRecords(target, Environment.TickCount64));
    }

    [Fact]
    public void Dispel_StripsActiveBuff()
    {
        var world = CreateWorld();
        var def = new SpellDef
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
        };
        var (engine, caster, target) = Setup(world, def);
        target.Str = 40;

        caster.BeginCast(SpellType.Strength, target.Uid, target.Position);
        Assert.True(engine.CastDone(caster));
        Assert.True(target.Str > 40); // buff applied

        engine.StripDispellableEffects(target);

        Assert.Equal(40, target.Str); // buff reverted, not permanent
        Assert.Empty(engine.GetPersistedEffectRecords(target, Environment.TickCount64));
    }

    // <X.UID> script reads render as bare hex (no 0x prefix).
    private static uint ParseUid(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            s = s[2..];
        return Convert.ToUInt32(s, 16);
    }
}
