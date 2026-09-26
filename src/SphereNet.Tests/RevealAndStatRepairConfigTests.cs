using System;
using System.IO;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Configuration;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Movement;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using SphereNet.MapData;
using SphereNet.Scripting.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// Concealment, starvation and the stat repair pass — PLAN-302's fourth and last
/// packet (port plan İŞ-72).
///
/// REVEALFLAGS is the awkward one, and not because of its size: three of its eleven
/// flags mean the OPPOSITE of the rest. SNOOPING, STEALING and OSILIKEPERSONALSPACE
/// SUPPRESS a reveal when set, while every other flag ENABLES one. A flag set read as
/// if it pointed one way throughout would invert half a shard's stealth rules and look
/// entirely reasonable doing it.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class RevealAndStatRepairConfigTests
{
    private readonly ITestOutputHelper _out;
    public RevealAndStatRepairConfigTests(ITestOutputHelper output) => _out = output;

    private static (GameWorld World, Character Ch) Hidden()
    {
        var map = new MapDataManager("");
        map.AddSyntheticMap(0, 256, 256, landZ: 0, landTile: 3);

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        world.MapData = map;
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.BaseId = 0x0190;
        ch.Str = 60; ch.Dex = 60; ch.Int = 60;
        ch.MaxHits = 100; ch.Hits = 100; ch.MaxStam = 100; ch.Stam = 100;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        ch.SetStatFlag(StatFlag.Hidden);
        return (world, ch);
    }

    // ---- the inverted three ----------------------------------------------

    [Fact]
    public void SettingTheSnoopingFlagSUPPRESSESTheReveal()
    {
        var (_, ch) = Hidden();

        Character.ActiveRevealFlags = RevealFlags.Snooping;
        bool revealedWhenSet = ch.ClearHiddenState(RevealFlags.Snooping);

        ch.SetStatFlag(StatFlag.Hidden);
        Character.ActiveRevealFlags = RevealFlags.None;
        bool revealedWhenClear = ch.ClearHiddenState(RevealFlags.Snooping);

        _out.WriteLine($"snooping flag set -> revealed={revealedWhenSet}; clear -> {revealedWhenClear}");

        // The reference ini's own comment: "Do not reveal hidden players while
        // snooping." Reading it the same way as its neighbours would make a shard that
        // asked for stealthy snooping get the opposite.
        Assert.False(revealedWhenSet);
        Assert.True(revealedWhenClear);
    }

    [Fact]
    public void TheStealingFlagIsInvertedTheSameWay()
    {
        var (_, ch) = Hidden();

        Character.ActiveRevealFlags = RevealFlags.Stealing;
        Assert.False(ch.ClearHiddenState(RevealFlags.Stealing));

        Character.ActiveRevealFlags = RevealFlags.None;
        Assert.True(ch.ClearHiddenState(RevealFlags.Stealing));
    }

    [Fact]
    public void PersonalSpaceIsInvertedToo()
    {
        var (world, ch) = Hidden();
        ch.StepStealth = 5; // sneaking: the step itself does not reveal (CheckRevealOnMove)
        var engine = new MovementEngine(world);

        var blocker = world.CreateCharacter();
        blocker.IsPlayer = true;
        blocker.MaxHits = 100; blocker.Hits = 100;
        world.PlaceCharacter(blocker, new Point3D(100, 99, 0, 0));

        Character.ActiveRevealFlags = RevealFlags.OsiLikePersonalSpace;
        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));

        _out.WriteLine($"after shoving past somebody: hidden={ch.IsStatFlag(StatFlag.Hidden)}");

        // Set, the flag means "do not reveal when a character enters personal space".
        Assert.True(ch.IsStatFlag(StatFlag.Hidden));
    }

    // ---- the ordinary ones -----------------------------------------------

    [Fact]
    public void ADetectThatIsSwitchedOffSucceedsWithNoEffect()
    {
        var (_, ch) = Hidden();

        Character.ActiveRevealFlags = RevealFlags.None;
        bool off = ch.ClearHiddenState(RevealFlags.DetectingHidden);

        Character.ActiveRevealFlags = RevealFlags.DetectingHidden;
        bool on = ch.ClearHiddenState(RevealFlags.DetectingHidden);

        _out.WriteLine($"detect off -> {off}, on -> {on}");

        // "Skill succeeded, but effect is disabled" (CCharSkill.cpp:1716) - the skill
        // still rolls, it simply does not uncover anyone.
        Assert.False(off);
        Assert.True(on);
    }

    [Fact]
    public void AnUngovernedRevealIgnoresTheFlagsEntirely()
    {
        var (_, ch) = Hidden();
        Character.ActiveRevealFlags = RevealFlags.None;

        // Death, a script's REVEAL, combat: upstream calls Reveal() with no flag to
        // consult. A flags check on those paths would make a shard able to configure
        // itself into permanently invisible corpses.
        Assert.True(ch.ClearHiddenState());
        Assert.False(ch.IsStatFlag(StatFlag.Hidden));
    }

    [Fact]
    public void AMountedSneakEndsAtOnceWhenTheShardSaysSo()
    {
        var (world, ch) = Hidden();
        var engine = new MovementEngine(world);
        ch.StepStealth = 10;
        ch.SetStatFlag(StatFlag.OnHorse);

        Character.ActiveRevealFlags = RevealFlags.None;
        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        bool stillHidden = ch.IsStatFlag(StatFlag.Hidden);

        Character.ActiveRevealFlags = RevealFlags.OnHorse;
        Assert.True(engine.TryMove(ch, Direction.North, running: false, sequence: 0));
        bool revealed = !ch.IsStatFlag(StatFlag.Hidden);

        _out.WriteLine($"flag clear -> still hidden={stillHidden}; flag set -> revealed={revealed}");

        // Checked before the step is counted (CCharAct.cpp:4850), so the horse gives
        // the rider away at once rather than after the stealth steps run out.
        Assert.True(stillHidden);
        Assert.True(revealed);
    }

    [Fact]
    public void TheFlagsAreReadFromTheIniInSpheresOwnNotation()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_rf_{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, "[SPHERE]\nRevealFlags=01|02|010|0200\n");
        try
        {
            var ini = new IniParser();
            ini.Load(path);
            var cfg = new SphereConfig();
            cfg.LoadFromIni(ini);

            var flags = (RevealFlags)cfg.RevealFlags;
            _out.WriteLine($"parsed: {flags}");

            // Two things GetInt cannot do: the '|' OR, and Sphere's rule that a LEADING
            // ZERO is hexadecimal - so 010 is sixteen, not ten. Read as decimal the
            // line yields a number that is wrong without anything complaining.
            Assert.Equal(
                RevealFlags.DetectingHidden | RevealFlags.LootingSelf |
                RevealFlags.SpellCast | RevealFlags.StealingFail, flags);
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    // ---- HITSHUNGERLOSS ---------------------------------------------------

    [Fact]
    public void StarvationCostsNothingUntilTheShardAsksForIt()
    {
        var (_, ch) = Hidden();
        ch.Food = 0;
        Character.HitsHungerLoss = 0;

        int before = ch.Hits;
        for (int i = 0; i < 50; i++) ch.OnTick();

        _out.WriteLine($"hits {before} -> {ch.Hits} with the key off");

        // The default, and what the reference ini ships (the line is commented out).
        // This engine once chipped health unconditionally; the invented part was doing
        // it without a setting, not the biting itself.
        Assert.Equal(before, ch.Hits);
    }

    // ---- OVERSKILLMULTIPLY ------------------------------------------------

    private const string ClassScript = """
        [SKILLCLASS 1]
        NAME=Default
        SKILLSUM=7000
        STATSUM=225
        STR=100
        DEX=100
        INT=100
        SKILL_Blacksmithing=1000
        """;

    private static Character ClassedPlayer(out GameWorld world)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_sc_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, ClassScript);
        try
        {
            var resources = new ResourceHolder(
                LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
            { ScpBaseDir = Path.GetDirectoryName(path) ?? "" };
            resources.LoadResourceFile(path);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

            var w = new GameWorld(LoggerFactory.Create(_ => { }));
            w.InitMap(0, 256, 256);
            ObjBase.ResolveWorld = () => w;
            Item.ResolveWorld = () => w;
            var ch = w.CreateCharacter();
            ch.IsPlayer = true;
            ch.BaseId = 0x0190;
            ch.MaxHits = 100; ch.Hits = 100;
            ch.SkillClass = 1;
            w.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
            world = w;
            // Fail loudly if the fixture's class never loaded: without it every cap
            // falls back to the engine default and the assertions below would pass for
            // the wrong reason.
            // Fail loudly if the fixture's class never resolved: without it every cap
            // falls back to the engine default and the assertions below would pass for
            // the wrong reason - which is exactly how the SKILLCLASS indexing defect
            // stayed invisible.
            var loaded = DefinitionLoader.GetSkillClassDef(1);
            Assert.True(loaded != null, "the [SKILLCLASS 1] fixture did not load");
            Assert.Equal(100, loaded!.StrMax);
            return ch;
        }
        finally { try { File.Delete(path); } catch (IOException) { } }
    }

    [Fact]
    public void ARidiculousSkillIsDroppedBackToTheClassLimit()
    {
        var ch = ClassedPlayer(out _);
        ch.SetSkill(SkillType.Blacksmithing, 5000);      // 500.0 against a 100.0 class cap
        Character.OverSkillMultiply = 2;

        ch.NormalizePlayerSkillClass();

        _out.WriteLine($"blacksmithing after the repair pass: {ch.GetSkill(SkillType.Blacksmithing)}");
        Assert.Equal(1000, ch.GetSkill(SkillType.Blacksmithing));
    }

    [Fact]
    public void SomethingMerelyOverTheLimitIsLeftAlone()
    {
        var ch = ClassedPlayer(out _);
        ch.SetSkill(SkillType.Blacksmithing, 1500);      // over the cap, under 2x
        Character.OverSkillMultiply = 2;

        ch.NormalizePlayerSkillClass();

        // The multiplier is SLACK, not a cap: the pass exists to catch the absurd, and
        // clamping everything over the limit here would fight whatever legitimately put
        // the character above it.
        Assert.Equal(1500, ch.GetSkill(SkillType.Blacksmithing));
    }

    [Fact]
    public void ZeroTurnsTheRepairPassOff()
    {
        var ch = ClassedPlayer(out _);
        ch.SetSkill(SkillType.Blacksmithing, 5000);
        Character.OverSkillMultiply = 0;

        ch.NormalizePlayerSkillClass();

        Assert.Equal(5000, ch.GetSkill(SkillType.Blacksmithing));
    }

    [Fact]
    public void StaffAreExempt()
    {
        var ch = ClassedPlayer(out _);
        ch.SetSkill(SkillType.Blacksmithing, 5000);
        ch.PrivLevel = PrivLevel.GM;
        Character.OverSkillMultiply = 2;

        ch.NormalizePlayerSkillClass();

        // Upstream gates on PLEVEL_Player (CChar.cpp:997). A GM with a test character
        // at 500.0 is deliberate, not corruption.
        Assert.Equal(5000, ch.GetSkill(SkillType.Blacksmithing));
    }

    [Fact]
    public void TheStatHalfUsesTheClassCeilingsThatHadNoConsumerBefore()
    {
        var ch = ClassedPlayer(out _);
        ch.Str = 500;                                    // against a 100 class ceiling
        ch.Dex = 150;                                    // over, but under 2x
        Character.OverSkillMultiply = 2;

        ch.NormalizePlayerSkillClass();

        _out.WriteLine($"str {ch.Str}, dex {ch.Dex}");

        // STRMAX/DEXMAX/INTMAX were parsed out of every SKILLCLASS and read by nothing
        // at all until this pass.
        Assert.Equal(100, ch.Str);
        Assert.Equal(150, ch.Dex);
    }
}
