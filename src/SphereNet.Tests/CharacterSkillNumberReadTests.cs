using SphereNet.Core.Enums;
using Xunit;

namespace SphereNet.Tests;

/// <summary>A character key that starts with a digit is a skill number (Source-X
/// FindSkillKey, CServerConfig.cpp:2353); dialogs walk the skills by index with
/// &lt;I.&lt;LOCAL._FOR&gt;&gt;.</summary>
public sealed class CharacterSkillNumberReadTests
{
    [Fact]
    public void NumericKey_ReadsThatSkill()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Anatomy, 505);

        Assert.True(ch.TryGetProperty(((int)SkillType.Anatomy).ToString(), out string byNumber));
        Assert.True(ch.TryGetProperty("ANATOMY", out string byName));
        Assert.Equal(byName, byNumber);
        Assert.Equal("50.5", byNumber); // "%u.%u" (CChar.cpp:2341)
    }

    /// <summary>A skill reads "100.0", not the raw tenths: FLOATVAL takes it as the real
    /// number, so a pack that works weapon damage out as &lt;FLOATVAL &lt;TACTICS&gt;/100&gt;
    /// gets 1, not 10 - the raw "1000" made those swings hit ten times too hard. An
    /// integer expression still skips the dot, so comparisons are unchanged.</summary>
    [Fact]
    public void ASkillReadsWithItsDecimalForFloatMath()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Tactics, 1000);
        Assert.True(ch.TryGetProperty("TACTICS", out string tactics));
        Assert.Equal("100.0", tactics);

        var parser = new SphereNet.Scripting.Expressions.ExpressionParser
        {
            VariableResolver = name => name.Equals("TACTICS", StringComparison.OrdinalIgnoreCase) ? tactics : null
        };
        Assert.Equal(1.0, double.Parse(parser.EvaluateStr("<FLOATVAL <TACTICS>/100>"),
            System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Equal("1", parser.EvaluateStr("<EVAL <TACTICS> == 1000>"));
        Assert.Equal("0", parser.EvaluateStr("<EVAL <TACTICS> < 100.0>"));
        Assert.Equal("100", parser.EvaluateStr("<EVAL <TACTICS>/10>"));
    }

    [Fact]
    public void OutOfRangeNumber_IsNotASkill()
    {
        var ch = TestHarness.CreateWorld().CreateCharacter();
        Assert.False(ch.TryGetProperty("999", out _));
    }
}
