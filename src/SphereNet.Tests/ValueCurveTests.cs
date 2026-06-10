using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Legacy [SKILL] value-curve semantics (reference CValueCurveDef +
/// ahextoi): decimal points concatenate digits, leading zero marks hex,
/// values interpolate linearly across skill 0-100.0 and ADV_RATE chance is
/// the inverse of "uses per 0.1 gain".
/// </summary>
public class ValueCurveTests
{
    [Theory]
    [InlineData("2.5", 25)]
    [InlineData("50.0", 500)]
    [InlineData("200.0", 2000)]
    [InlineData("110", 110)]
    [InlineData("1.0", 10)]
    [InlineData("0.5", 5)]      // leading "0." stays decimal
    [InlineData("0480", 0x480)] // leading zero = hex
    [InlineData("04", 4)]
    [InlineData("-3.0", -30)]
    [InlineData("", 0)]
    public void ParseSphereNumber_UsesLegacyFixedPointAndHexRules(string token, int expected)
    {
        Assert.Equal(expected, ValueCurve.ParseSphereNumber(token));
    }

    [Fact]
    public void Parse_RealScriptAdvRate_ProducesFixedPointCurve()
    {
        // sphere_skills.scp: ADV_RATE=2.5,50.0,200.0
        var curve = ValueCurve.Parse("2.5,50.0,200.0");
        Assert.Equal(3, curve.Count);
        Assert.Equal(25, curve.GetLinear(0));
        Assert.Equal(500, curve.GetLinear(500));
        Assert.Equal(2000, curve.GetLinear(1000));
        // Midpoint of the first segment interpolates linearly.
        Assert.Equal(25 + (500 - 25) * 250 / 500, curve.GetLinear(250));
    }

    [Fact]
    public void GetChancePercent_IsInverseOfUsesPerGain()
    {
        var curve = ValueCurve.Parse("2.5,50.0,200.0");
        Assert.Equal(100000 / 25, curve.GetChancePercent(0));     // trivially easy
        Assert.Equal(100000 / 500, curve.GetChancePercent(500));  // 20 per-mille
        Assert.Equal(100000 / 2000, curve.GetChancePercent(1000)); // 5 per-mille
        Assert.Equal(0, ValueCurve.Empty.GetChancePercent(500));
    }

    [Fact]
    public void GetLinear_TwoAndManyValueCurves_Interpolate()
    {
        var two = ValueCurve.Parse("3.0,1.0");
        Assert.Equal(30, two.GetLinear(0));
        Assert.Equal(20, two.GetLinear(500));
        Assert.Equal(10, two.GetLinear(1000));

        var single = ValueCurve.Parse("2.0");
        Assert.Equal(20, single.GetLinear(0));
        Assert.Equal(20, single.GetLinear(1000));

        var four = ValueCurve.Parse("9.0,6.0,3.0,0");
        Assert.Equal(90, four.GetLinear(0));
        Assert.Equal(0, four.GetLinear(1000));
        Assert.True(four.GetLinear(400) < four.GetLinear(100));
    }

    [Fact]
    public void SkillDef_ParsesRealScriptValues()
    {
        var def = new SkillDef(SphereNet.Core.Types.ResourceId.Invalid);
        def.LoadFromKey("ADV_RATE", "2.5,50.0,200.0");
        def.LoadFromKey("DELAY", "2.0");
        def.LoadFromKey("STAT_STR", "80.0");
        def.LoadFromKey("BONUS_STATS", "15");

        Assert.False(def.AdvRate.IsEmpty);
        Assert.Equal(25, def.AdvRate.GetLinear(0));
        Assert.Equal(20, def.Delay.GetLinear(500)); // 2.0s = 20 tenths at any level
        Assert.Equal(800, def.StatStr);
        Assert.Equal(15, def.BonusStats);
    }
}
