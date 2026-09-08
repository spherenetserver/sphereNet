using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Taking a creature's shape takes its strength with it.
///
/// Upstream moves the caster's STR and DEX to the form's own when MAGICF_POLYMORPHSTATS
/// is on, by no more than MAXPOLYSTATS points in either direction, and remembers the
/// change on the spell memory so it comes off with the form (CCharSpell.cpp:1082). A
/// definition that declares no value for a stat leaves that stat alone.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class PolymorphStatsParityTests : IDisposable
{
    private readonly string _scriptPath;
    private readonly ResourceHolder _resources;

    private const string Defs = """
        [CHARDEF 00d9]
        DEFNAME=c_dog_poly
        NAME=a dog
        ID=0x00D9
        STR=200
        DEX=120

        [CHARDEF 00cd]
        DEFNAME=c_rabbit_poly
        NAME=a rabbit
        ID=0x00CD
        DEX=90
        """;

    public PolymorphStatsParityTests()
    {
        _scriptPath = Path.Combine(Path.GetTempPath(), $"sphnet_poly_{Guid.NewGuid():N}.scp");
        File.WriteAllText(_scriptPath, Defs);
        _resources = new ResourceHolder(
            LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>());
    }

    public void Dispose()
    {
        SpellEngine.MaxPolyStats = 150;
        Character.MagicFlags = 0;
        File.Delete(_scriptPath);
    }

    private (SpellEngine Engine, Character Caster, GameWorld World) Bench(string form)
    {
        _resources.LoadResourceFile(_scriptPath);
        new DefinitionLoader(_resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        registry.Register(new SpellDef
        {
            Id = SpellType.Polymorph,
            ManaCost = 0,
            CastTimeBase = 1,
            DurationBase = 60,
        });

        var caster = world.CreateCharacter();
        caster.MaxMana = caster.Mana = 100;
        caster.SetSkill(SkillType.Magery, 2000);
        caster.BodyId = 0x0190;
        caster.Str = 100;
        caster.Dex = 100;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        caster.SetTag("POLY_SELECT", form);

        // The stats only move when the shard asks for it: upstream gates this on
        // MAGICF_POLYMORPHSTATS.
        Character.MagicFlags = (int)MagicConfigFlags.PolymorphStats;
        var engine = new SpellEngine(world, registry);
        return (engine, caster, world);
    }

    private static void Cast(SpellEngine engine, Character caster)
    {
        Assert.True(engine.CastStart(caster, SpellType.Polymorph, caster.Uid, caster.Position) > 0);
        Assert.True(engine.CastDone(caster));
    }

    [Fact]
    public void TheFormsOwnStatsAreTakenOn()
    {
        var (engine, caster, _) = Bench("c_dog_poly");

        Cast(engine, caster);

        // 100 -> 200 is a change of 100, inside the 150 the setting allows.
        Assert.Equal(200, caster.Str);
        Assert.Equal(120, caster.Dex);
    }

    [Fact]
    public void TheChangeIsBoundedByTheSetting()
    {
        var (engine, caster, _) = Bench("c_dog_poly");
        SpellEngine.MaxPolyStats = 40;

        Cast(engine, caster);

        // The form wants +100; only 40 of it is allowed.
        Assert.Equal(140, caster.Str);
        Assert.Equal(120, caster.Dex);   // +20 was under the cap anyway
    }

    [Fact]
    public void AStatTheFormDoesNotDeclareIsLeftAlone()
    {
        var (engine, caster, _) = Bench("c_rabbit_poly");

        Cast(engine, caster);

        Assert.Equal(100, caster.Str);   // the rabbit declares no STR
        Assert.Equal(90, caster.Dex);
    }

    [Fact]
    public void WithoutTheMagicFlagNothingMoves()
    {
        var (engine, caster, _) = Bench("c_dog_poly");
        Character.MagicFlags = 0;

        Cast(engine, caster);

        Assert.Equal(100, caster.Str);
        Assert.Equal(100, caster.Dex);
    }
}
