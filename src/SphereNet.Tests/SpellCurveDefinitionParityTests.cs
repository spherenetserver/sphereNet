using SphereNet.Core.Enums;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Scripting.Definitions;
using Xunit.Abstractions;

namespace SphereNet.Tests;

/// <summary>
/// [SPELL] CAST_TIME / EFFECT / DURATION / INTERRUPT are value curves read the way the
/// reference reads them.
///
/// All four are CValueCurveDef (CSpellDef.h:69-72) loaded by CValueCurveDef::Load
/// (CValueDefs.cpp:72): Str_ParseCmds splits the line and every argument goes through
/// Exp_GetVal (CExpression.cpp:305), so a point is a whole expression in the legacy
/// fixed-point notation ("6.0" = 60, "0.5" = 5, "3*60.0" = 1800, "6" = 6). The curve
/// keeps every point - an explicit 0 endpoint is a point - and GetLinear
/// (CValueDefs.cpp:90) interpolates segment by segment. Nothing rescales the result:
/// CAST_TIME is already tenths (CCharSpell.cpp:3499) and so is DURATION
/// (CCharSpell.cpp:4308).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SpellCurveDefinitionParityTests(ITestOutputHelper output)
{
    private static SpellDef LoadOne(string body, int spellId = 1)
    {
        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        string path = Path.Combine(Path.GetTempPath(), $"spell_curve_{Guid.NewGuid():N}.scp");
        try
        {
            File.WriteAllText(path, $"[SPELL {spellId}]\nNAME=Probe\n{body}\n");
            runtime.Resources.LoadResourceFile(path);
            var registry = new SpellRegistry();
            new DefinitionLoader(runtime.Resources, registry).LoadAll();
            return registry.Get((SpellType)spellId)
                ?? throw new InvalidOperationException("spell did not load");
        }
        finally { File.Delete(path); }
    }

    // ---- B22: the unit of a point ------------------------------------------------

    [Theory]
    [InlineData("6", 6)]          // an integer is already tenths
    [InlineData("6.0", 60)]       // fixed point: the dot is skipped
    [InlineData("0.5", 5)]        // "0." stays decimal, not hex
    [InlineData("1.5", 15)]
    [InlineData("60", 60)]
    [InlineData("3*60.0", 1800)]  // one expression, one point
    public void CastTimeIsTenthsInTheReferenceNotation(string text, int tenths)
    {
        var def = LoadOne($"CAST_TIME={text}");
        Assert.Equal(tenths, def.GetCastTime(0));
        Assert.Equal(tenths, def.GetCastTime(1000));
    }

    [Theory]
    [InlineData("6", 6)]
    [InlineData("6.0", 60)]
    [InlineData("60.0", 600)]
    [InlineData("3*60.0", 1800)]
    [InlineData("0", 0)]
    [InlineData("0.0", 0)]
    public void DurationIsTenthsInTheReferenceNotation(string text, int tenths)
    {
        var def = LoadOne($"DURATION={text}");
        Assert.Equal(tenths, def.GetDuration(0));
        Assert.Equal(tenths, def.GetDuration(1000));
    }

    [Fact]
    public void MultipliedEndpointsInterpolate()
    {
        var def = LoadOne("DURATION=2*60.0,4*60.0");
        Assert.Equal(1200, def.GetDuration(0));
        Assert.Equal(1800, def.GetDuration(500));
        Assert.Equal(2400, def.GetDuration(1000));
    }

    [Fact]
    public void AnEffectPointIsNotTruncatedToWholeUnits()
    {
        // "10.0" is 100 in the reference, not 10.
        var def = LoadOne("EFFECT=10.0,60.0");
        Assert.Equal(100, def.GetEffect(0));
        Assert.Equal(600, def.GetEffect(1000));
    }

    [Fact]
    public void ALeadingDotIsSkipped()
    {
        // GetSingle skips a leading '.' (CExpression.cpp:661): ".45.0" is 450.
        var def = LoadOne("DURATION=30.0,.45.0");
        Assert.Equal(300, def.GetDuration(0));
        Assert.Equal(450, def.GetDuration(1000));
    }

    // ---- B23: the points of a curve ----------------------------------------------

    [Fact]
    public void AnEmptyCurveIsZero()
    {
        var def = LoadOne("CAST_TIME=\nEFFECT=\nDURATION=");
        Assert.Equal(0, def.GetEffect(1000));
        Assert.Equal(0, def.GetDuration(1000));
        Assert.Equal(1, def.GetCastTime(1000)); // engine one-tenth floor on an empty curve
    }

    [Fact]
    public void AnExplicitZeroEndpointIsKept()
    {
        var def = LoadOne("EFFECT=10,0\nDURATION=10,0\nCAST_TIME=20,0\nINTERRUPT=100.0,0.0");
        Assert.Equal(10, def.GetEffect(0));
        Assert.Equal(5, def.GetEffect(500));
        Assert.Equal(0, def.GetEffect(1000));
        Assert.Equal(0, def.GetDuration(1000));
        Assert.Equal(1000, def.GetInterruptChance(0));
        Assert.Equal(0, def.GetInterruptChance(1000));
        Assert.Equal(1, def.GetCastTime(1000)); // 0 from the curve, engine floor 1
    }

    [Fact]
    public void AThreePointCurveInterpolatesPerSegment()
    {
        var def = LoadOne("EFFECT=10,30,90\nCAST_TIME=10,30,90\nDURATION=10,30,90");
        Assert.Equal(10, def.GetEffect(0));
        Assert.Equal(20, def.GetEffect(250));
        Assert.Equal(30, def.GetEffect(500));
        Assert.Equal(60, def.GetEffect(750));
        Assert.Equal(90, def.GetEffect(1000));
        Assert.Equal(30, def.GetCastTime(500));
        Assert.Equal(90, def.GetCastTime(1000));
        Assert.Equal(30, def.GetDuration(500));
    }

    [Fact]
    public void ScriptReadBackWritesEveryPoint()
    {
        // CSpellDef::r_WriteVal -> CValueCurveDef::Write (CValueDefs.cpp:55).
        var def = LoadOne("EFFECT=10,30,90\nDURATION=10,0\nCAST_TIME=6");
        Assert.True(def.TryGetProperty("EFFECT", out string effect));
        Assert.Equal("10,30,90", effect);
        Assert.True(def.TryGetProperty("DURATION", out string duration));
        Assert.Equal("10,0", duration);
        Assert.True(def.TryGetProperty("CAST_TIME", out string cast));
        Assert.Equal("6", cast);
    }

    [Fact]
    public void TheLegacyEndpointPropertiesStillDescribeAConstantOrALine()
    {
        // Code-built definitions keep their meaning: Base alone is a constant, a
        // non-zero Scale is the top endpoint.
        var constant = new SpellDef { Id = SpellType.Bless, DurationBase = 600, EffectBase = 7 };
        Assert.Equal(600, constant.GetDuration(1000));
        Assert.Equal(7, constant.GetEffect(1000));

        var line = new SpellDef { Id = SpellType.Bless, EffectScale = 20, EffectBase = 5 };
        Assert.Equal(5, line.GetEffect(0));
        Assert.Equal(20, line.GetEffect(1000));
        Assert.Equal(5, line.EffectBase);
        Assert.Equal(20, line.EffectScale);
    }

    // ---- ValueCurve itself --------------------------------------------------------

    [Theory]
    [InlineData("", 0)]
    [InlineData("7", 1)]
    [InlineData("1,2", 2)]
    [InlineData("1, 2", 2)]
    [InlineData("1 , 2", 2)]
    [InlineData("1 2", 2)]      // space is a separator too (CExpression.cpp:144)
    [InlineData("1,2,3,4,5", 5)]
    [InlineData("(1, 2)", 1)]  // brackets hold an argument together
    public void ParseKeepsThePointCount(string text, int count)
    {
        Assert.Equal(count, ValueCurve.Parse(text).Count);
    }

    [Fact]
    public void GetLinearRoundsLikeTheReferenceMulDiv()
    {
        // IMulDivLL rounds half up and subtracts one for a negative product
        // (common.h:207): 30 + IMulDivLL(-15, 500, 1000) = 30 - 8 = 22.
        var curve = ValueCurve.Parse("30,15");
        Assert.Equal(22, curve.GetLinear(500));
        Assert.Equal(25 + 238, ValueCurve.Parse("2.5,50.0,200.0").GetLinear(250));
    }

    [Fact]
    public void GetLinearExtrapolatesAboveFullSkill()
    {
        // No clamp on the skill in the reference: 120.0 continues the last segment.
        Assert.Equal(120, ValueCurve.Parse("0,100").GetLinear(1200));
        // ...and a result at or below zero reads 0.
        Assert.Equal(0, ValueCurve.Parse("100,0").GetLinear(1200));
    }

    [Fact]
    public void AFourPointCurveFollowsTheReferenceSegmentChoice()
    {
        // 4 points: segments of 1000/3 = 333; the low index is
        // IMulDiv(skill, 4, 1000) capped at 2 (CValueDefs.cpp:124-131).
        var curve = ValueCurve.Parse("0,30,60,90");
        Assert.Equal(0, curve.GetLinear(0));
        Assert.Equal(30, curve.GetLinear(333));
        Assert.Equal(90, curve.GetLinear(1000));
    }

    // ---- the shipped pack --------------------------------------------------------

    [Fact]
    public void TheReferencePackSpellCurvesReadAsTheReferenceReadsThem()
    {
        string? dir = TestRepo.Optional("oldSphere/Scripts-X-main/spells");
        if (Gate.Missing(output, "Source-X reference tree", dir == null)) return;

        var runtime = ScriptTestBootstrap.CreateRuntimeStack();
        foreach (string file in Directory.GetFiles(dir!, "*.scp"))
            runtime.Resources.LoadResourceFile(file);
        var registry = new SpellRegistry();
        new DefinitionLoader(runtime.Resources, registry).LoadAll();

        // s_clumsy: CAST_TIME=0.5 EFFECT=3,15 DURATION=2*60.0,3*60.0 INTERRUPT=100.0,100.0
        var clumsy = registry.Get(SpellType.Clumsy)!;
        Assert.Equal(5, clumsy.GetCastTime(0));
        Assert.Equal(3, clumsy.GetEffect(0));
        Assert.Equal(9, clumsy.GetEffect(500));
        Assert.Equal(15, clumsy.GetEffect(1000));
        Assert.Equal(1200, clumsy.GetDuration(0));
        Assert.Equal(1500, clumsy.GetDuration(500));
        Assert.Equal(1800, clumsy.GetDuration(1000));
        Assert.Equal(1000, clumsy.GetInterruptChance(500));

        // s_bless: CAST_TIME=1.0 DURATION=2*60.0,3*60.0
        var bless = registry.Get(SpellType.Bless)!;
        Assert.Equal(10, bless.GetCastTime(1000));
        Assert.Equal(1800, bless.GetDuration(1000));

        // s_reactive_armor: EFFECT=10.0,60.0 -> 100..600 (the % reflected is /10).
        var reactive = registry.Get(SpellType.ReactiveArmor)!;
        Assert.Equal(100, reactive.GetEffect(0));
        Assert.Equal(600, reactive.GetEffect(1000));

        // s_curse_weapon: INTERRUPT=100.0,0.0 -> 1000 at no skill, 0 at full skill.
        var curseWeapon = registry.Get((SpellType)104)!;
        Assert.Equal(1000, curseWeapon.GetInterruptChance(0));
        Assert.Equal(0, curseWeapon.GetInterruptChance(1000));

        // s_nether_cyclone: DURATION=30.0,.45.0 -> 300..450.
        var cyclone = registry.Get((SpellType)692)!;
        Assert.Equal(300, cyclone.GetDuration(0));
        Assert.Equal(450, cyclone.GetDuration(1000));

        // s_animate_dead: CAST_TIME=60 -> 60 tenths (6 s), not 60 s.
        var animate = registry.Get((SpellType)1001)!;
        Assert.Equal(60, animate.GetCastTime(0));
    }
}
