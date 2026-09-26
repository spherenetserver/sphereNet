using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Poison Strike and Wither against Source-X. Neither has a native case in
/// CChar::OnSpellEffect (the commented-out list at CCharSpell.cpp:4142-4147): both
/// are AREA spells, swept by Spell_Area at the radius @Success leaves in
/// LOCAL.AreaRadius (or the per-spell default, :3064-3080), and the pack's [SPELL]
/// @Effect stage deals the damage. The old engine-side damage handlers were
/// invented and are gone.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SourceXWave257Tests
{
    private static (GameWorld world, SpellEngine engine, Character caster) Arena(SpellType id,
        SpellFlag flags, TriggerDispatcherHolder? scripts = null)
    {
        var world = TestHarness.CreateWorld();
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = id,
            Flags = flags,
            ManaCost = 0,
            CastTimeBase = 1,
            EffectBase = 40,
            EffectScale = 40,
        });
        var engine = new SpellEngine(world, registry) { TriggerDispatcher = scripts?.Stack.Dispatcher };

        var caster = world.CreateCharacter();
        caster.PrivLevel = PrivLevel.GM;
        caster.MaxMana = 100; caster.Mana = 100;
        caster.MaxHits = 100; caster.Hits = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (world, engine, caster);
    }

    private sealed class TriggerDispatcherHolder(ScriptRuntimeStack stack, string path)
    {
        public ScriptRuntimeStack Stack { get; } = stack;
        public string Path { get; } = path;
    }

    private static TriggerDispatcherHolder Scripts(string text)
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"area-{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, text);
        var stack = ScriptTestBootstrap.CreateRuntimeStack();
        stack.Resources.LoadResourceFile(path);
        return new TriggerDispatcherHolder(stack, path);
    }

    private static Character Victim(GameWorld world, int x, int y)
    {
        var v = world.CreateCharacter();
        v.MaxHits = 100; v.Hits = 100;
        v.ResPoison = 0; v.ResCold = 0; v.ResPhysical = 0;
        world.PlaceCharacter(v, new Point3D((short)x, (short)y, 0, 0));
        return v;
    }

    [Fact]
    public void PoisonStrike_HasNoNativeDamage()
    {
        var (world, engine, caster) = Arena(SpellType.PoisonStrike,
            SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Area);
        var primary = Victim(world, 101, 100);
        var near = Victim(world, 102, 100);

        Assert.True(engine.CastStart(caster, SpellType.PoisonStrike, primary.Uid, primary.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(100, primary.Hits);
        Assert.Equal(100, near.Hits);
    }

    [Fact]
    public void Wither_HasNoNativeDamage()
    {
        var (world, engine, caster) = Arena(SpellType.Wither,
            SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Area);
        var near = Victim(world, 102, 100);

        Assert.True(engine.CastStart(caster, SpellType.Wither, near.Uid, near.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.Equal(100, near.Hits);
    }

    [Theory]
    [InlineData(SpellType.ArchCure, 0, 2)]
    [InlineData(SpellType.ArchProtection, 0, 3)]
    [InlineData(SpellType.MassCurse, 0, 2)]
    [InlineData(SpellType.Reveal, 1000, 6)]      // 1 + 1000/200
    [InlineData(SpellType.ChainLightning, 0, 2)]
    [InlineData(SpellType.MassDispel, 0, 8)]
    [InlineData(SpellType.MeteorSwarm, 0, 2)]
    [InlineData(SpellType.Earthquake, 900, 7)]   // 1 + 900/150
    [InlineData(SpellType.PoisonStrike, 0, 2)]
    [InlineData(SpellType.Wither, 0, 4)]
    [InlineData(SpellType.Explosion, 0, 4)]      // anything else
    public void TheDefaultAreaRadiusIsSourceXsTable(SpellType spell, int level, int radius)
    {
        Assert.Equal(radius, SpellEngine.DefaultAreaRadius(spell, level));
    }

    [Fact]
    public void AreaSweepUsesTheSuccessRadiusAndTheEffectStageDealsTheDamage()
    {
        // The pack's own shape: @Success narrows the radius, @Effect does the work.
        var scripts = Scripts("[SPELL 110]\nNAME=Poison Strike\n" +
            "ON=@Success\nlocal.AreaRadius=1\n" +
            "ON=@Effect\nTAG.STRUCK=1\n");
        try
        {
            var (world, engine, caster) = Arena(SpellType.PoisonStrike,
                SpellFlag.TargChar | SpellFlag.Harm | SpellFlag.Area, scripts);
            var primary = Victim(world, 105, 100);
            var inside = Victim(world, 106, 100);    // 1 tile from the target
            var outside = Victim(world, 107, 100);   // 2 tiles - past LOCAL.AreaRadius=1

            Assert.True(engine.CastStart(caster, SpellType.PoisonStrike, primary.Uid, primary.Position) >= 0);
            Assert.True(engine.CastDone(caster));

            Assert.True(primary.TryGetTag("STRUCK", out _));
            Assert.True(inside.TryGetTag("STRUCK", out _));
            Assert.False(outside.TryGetTag("STRUCK", out _));
            Assert.False(caster.TryGetTag("STRUCK", out _));   // harmful: never the caster
        }
        finally { File.Delete(scripts.Path); }
    }

    [Fact]
    public void ATargCharAreaSpellTakesTheAreaBranch()
    {
        // TARG_CHAR + AREA sweeps around the target (m_Act_p, :3082-3085) at the
        // default radius 4 instead of touching the target alone.
        var (world, engine, caster) = Arena(SpellType.Heal,
            SpellFlag.TargChar | SpellFlag.Heal | SpellFlag.Area);
        var target = Victim(world, 110, 100);
        var within = Victim(world, 114, 100);   // 4 from the target
        var beyond = Victim(world, 115, 100);   // 5 from the target
        target.Hits = within.Hits = beyond.Hits = 10;

        Assert.True(engine.CastStart(caster, SpellType.Heal, target.Uid, target.Position) >= 0);
        Assert.True(engine.CastDone(caster));

        Assert.True(target.Hits > 10);
        Assert.True(within.Hits > 10);
        Assert.Equal(10, beyond.Hits);
    }
}
