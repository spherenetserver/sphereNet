using SphereNet.Core.Enums;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// A key that starts with a digit is a skill NUMBER (FindSkillKey,
/// CServerConfig.cpp:2357), on the write side as on the read: a pack's teacher dialog
/// sets the skill its button stands for with SRC.&lt;EVAL &lt;ARGN&gt;-1&gt; 30.0. The read
/// knew it; the write did nothing, and the teacher taught nothing.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class SkillNumberKeyTests
{
    [Fact]
    public void ASkillIsSetByItsNumber()
    {
        var ch = TestHarness.CreateWorld().CreateCharacter();

        Assert.True(ch.TrySetProperty("25", "30.0"));

        Assert.Equal(300, ch.GetSkill(SkillType.Magery));
        Assert.True(ch.TryGetProperty("25", out string read));
        Assert.Equal("30.0", read);
    }

    /// <summary>Skill_SetBase ends by updating the owner's skill window
    /// (addSkillWindow, CCharSkill.cpp:223): a runtime set says so, an unchanged
    /// value does not.</summary>
    [Fact]
    public void ARuntimeSkillSetIsAnnouncedForTheSkillWindow()
    {
        var ch = TestHarness.CreateWorld().CreateCharacter();
        var announced = new List<SkillType>();
        SphereNet.Game.Objects.Characters.Character.OnSkillValueSet = (c, sk) => { if (c == ch) announced.Add(sk); };

        Assert.True(ch.TrySetProperty("MAGERY", "50.0"));
        Assert.True(ch.TrySetProperty("MAGERY", "50.0"));

        Assert.Equal(new[] { SkillType.Magery }, announced);
    }

    [Fact]
    public void ANumberPastTheSkillsIsNoSkill()
    {
        var ch = TestHarness.CreateWorld().CreateCharacter();

        Assert.False(ch.TrySetProperty("999", "30.0"));
    }
}
