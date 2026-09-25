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
        Assert.Equal("505", byNumber);
    }

    [Fact]
    public void OutOfRangeNumber_IsNotASkill()
    {
        var ch = TestHarness.CreateWorld().CreateCharacter();
        Assert.False(ch.TryGetProperty("999", out _));
    }
}
