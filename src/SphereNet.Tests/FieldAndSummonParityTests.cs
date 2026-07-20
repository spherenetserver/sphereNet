using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Guild;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit findings (wiki/1.txt): field spells use the classic field art with
/// typed step behavior and the spell's DURATION (finding 1), fixed summon
/// spells carry their Source-X creature ids (finding 2), and town/guild
/// membership pools are independent (finding 5).
/// </summary>
[Collection("VendorStateSerial")]
public sealed class FieldAndSummonParityTests
{
    private static (GameWorld World, SpellEngine Engine, Character Caster) Setup(params SpellDef[] defs)
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var registry = new SpellRegistry();
        foreach (var def in defs)
            registry.Register(def);
        var engine = new SpellEngine(world, registry);

        var caster = world.CreateCharacter();
        caster.IsPlayer = true;
        caster.PrivLevel = PrivLevel.GM;
        world.PlaceCharacter(caster, new Point3D(100, 100, 0, 0));
        return (world, engine, caster);
    }

    private static SpellDef FireFieldDef() => new()
    {
        Id = SpellType.FireField,
        Name = "Fire Field",
        Flags = SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Damage | SpellFlag.Field,
        EffectBase = 2, EffectScale = 10,
        DurationBase = 1200, // 2 minutes in tenths
    };

    [Fact]
    public void FireField_UsesClassicArt_DurationCurve_AndBurnsOnTouch()
    {
        var (world, engine, caster) = Setup(FireFieldDef());

        Assert.True(engine.CastStart(caster, SpellType.FireField, caster.Uid,
            new Point3D(105, 100, 0, 0)) > 0);
        Assert.True(engine.CastDone(caster));

        // Caster faces east → the wall runs north-south → N/S art 0x3996,
        // never the invisible EFFECT_ID=0 of the pack def.
        var segments = world.GetAllObjects().OfType<Item>()
            .Where(i => !i.IsDeleted && i.BaseId == 0x3996).ToList();
        Assert.True(segments.Count >= 3, $"expected field segments, got {segments.Count}");

        // Duration follows the spell's DURATION curve (2 min), not a fixed 30s.
        Assert.All(segments, s =>
            Assert.True(s.DecayTime > Environment.TickCount64 + 60_000,
                "field segment still uses the fixed 30s decay"));

        // Touch burns (typed fire damage), instead of nothing/flat routing.
        var victim = world.CreateCharacter();
        victim.MaxHits = 100;
        victim.Hits = 100;
        world.PlaceCharacter(victim, segments[0].Position);
        Assert.True(engine.ApplyFieldTouch(victim, segments[0]));
        Assert.True(victim.Hits < 100, "fire field touch dealt no damage");
    }

    [Fact]
    public void PoisonField_PoisonsOnTouch_InsteadOfFlatDamage()
    {
        var (world, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.PoisonField,
            Name = "Poison Field",
            Flags = SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Field,
            DurationBase = 1200,
        });

        Assert.True(engine.CastStart(caster, SpellType.PoisonField, caster.Uid,
            new Point3D(105, 100, 0, 0)) > 0);
        Assert.True(engine.CastDone(caster));

        var segment = world.GetAllObjects().OfType<Item>()
            .First(i => !i.IsDeleted && i.BaseId == 0x3920);
        Assert.False(segment.TryGetTag("FIELD_DAMAGE", out _)); // no flat damage

        var victim = world.CreateCharacter();
        victim.MaxHits = 100;
        victim.Hits = 100;
        world.PlaceCharacter(victim, segment.Position);
        Assert.True(engine.ApplyFieldTouch(victim, segment));
        Assert.True(victim.IsPoisoned, "poison field did not poison");
        Assert.Equal(100, victim.Hits); // poisons, does not flat-damage
    }

    [Fact]
    public void WallOfStone_IsABarrier_AndNeverMaterialisesOverACharacter()
    {
        var (world, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.WallOfStone,
            Name = "Wall of Stone",
            Flags = SpellFlag.TargXYZ | SpellFlag.Harm | SpellFlag.Field,
            DurationBase = 1200,
        });

        // A bystander stands on one of the five wall tiles.
        var bystander = world.CreateCharacter();
        world.PlaceCharacter(bystander, new Point3D(105, 101, 0, 0));

        Assert.True(engine.CastStart(caster, SpellType.WallOfStone, caster.Uid,
            new Point3D(105, 100, 0, 0)) > 0);
        Assert.True(engine.CastDone(caster));

        var walls = world.GetAllObjects().OfType<Item>()
            .Where(i => !i.IsDeleted && i.BaseId == 0x0080).ToList();
        Assert.NotEmpty(walls);
        Assert.All(walls, w => Assert.False(w.TryGetTag("FIELD_DAMAGE", out _)));
        // The occupied tile was skipped (Source-X: no stone wall over a char).
        Assert.DoesNotContain(walls, w => w.X == 105 && w.Y == 101);
    }

    [Fact]
    public void BladeSpirit_SummonsItsClassicCreature()
    {
        var (world, engine, caster) = Setup(new SpellDef
        {
            Id = SpellType.BladeSpirit,
            Name = "Blade Spirit",
            Flags = SpellFlag.TargXYZ | SpellFlag.Summon,
            DurationBase = 1200,
        });

        Assert.True(engine.CastStart(caster, SpellType.BladeSpirit, caster.Uid,
            new Point3D(102, 100, 0, 0)) > 0);
        Assert.True(engine.CastDone(caster));

        var spirit = world.GetCharsInRange(caster.Position, 5)
            .FirstOrDefault(c => c != caster && c.IsSummoned);
        Assert.NotNull(spirit);
        Assert.Equal((ushort)0x023E, spirit!.BodyId); // CREID_BLADE_SPIRIT
    }

    [Fact]
    public void GuildMember_CanStillBecomeTownCitizen()
    {
        var guilds = new GuildManager();
        var member = new Serial(0x1234);
        guilds.CreateGuild(new Serial(0x40001111), "The Guild", member);
        guilds.CreateGuild(new Serial(0x40002222), "Britain", new Serial(0x5678),
            isTownStone: true);

        // The guild membership must NOT block town citizenship: the town-pool
        // lookup sees no record for the guild member.
        Assert.NotNull(guilds.FindGuildRecordFor(member, townStones: false));
        Assert.Null(guilds.FindGuildRecordFor(member, townStones: true));
    }
}
