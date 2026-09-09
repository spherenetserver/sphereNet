using System;
using System.Collections.Generic;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// SKILLUSEQUICK is not a question (port plan İŞ-14 / PLAN-304 tail).
///
/// It sits among the read-only queries and behaves like none of them: upstream hands it
/// straight to Skill_UseQuick with gain allowed (CChar.cpp:2802), so reading the key
/// rolls the dice, can raise the skill and fires @SkillUseQuick. That is exactly why it
/// was left out of the CANMAKE wave and given its own pass.
///
/// Two details are the reference's and look like mistakes until you read it: the third
/// argument is INVERTED - a non-zero value turns the bell curve OFF - and fewer than two
/// arguments leaves the key unhandled rather than answering zero.
/// </summary>
public sealed class SkillUseQuickPropertyTests : IDisposable
{
    private readonly List<(int Skill, int Difficulty, bool BellCurve, bool Force)> _calls = [];

    public void Dispose() => Character.OnSkillUseQuickProperty = null;

    /// <summary>The roll is recorded rather than performed, and the hook is installed
    /// HERE rather than in the constructor: the shared-statics reset runs between the
    /// two and would clear it.</summary>
    private Character Someone()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        Character.OnSkillUseQuickProperty = (_, skill, difficulty, bell, force) =>
        {
            _calls.Add((skill, difficulty, bell, force));
            return difficulty <= 500;      // a stand-in for the roll
        };
        return ch;
    }

    [Fact]
    public void TheSkillAndDifficultyReachTheRoll()
    {
        var ch = Someone();

        Assert.True(ch.TryGetProperty("SKILLUSEQUICK.Blacksmithing,300", out string? v));
        Assert.Equal("1", v);

        var (skill, difficulty, bell, force) = Assert.Single(_calls);
        Assert.Equal((int)SkillType.Blacksmithing, skill);
        Assert.Equal(300, difficulty);
        Assert.True(bell);        // on unless the script says otherwise
        Assert.False(force);
    }

    [Fact]
    public void AFailedRollAnswersZero()
    {
        var ch = Someone();

        Assert.True(ch.TryGetProperty("SKILLUSEQUICK.Blacksmithing,900", out string? v));
        Assert.Equal("0", v);
    }

    [Fact]
    public void TheThirdArgumentTurnsTheBellCurveOff()
    {
        var ch = Someone();

        Assert.True(ch.TryGetProperty("SKILLUSEQUICK.Blacksmithing,300,1", out _));

        Assert.False(_calls[0].BellCurve);    // inverted, as upstream writes it
    }

    [Fact]
    public void TheFourthArgumentInsistsOnAScriptedSkill()
    {
        var ch = Someone();

        Assert.True(ch.TryGetProperty("SKILLUSEQUICK.Blacksmithing,300,0,1", out _));

        Assert.True(_calls[0].BellCurve);
        Assert.True(_calls[0].Force);
    }

    [Fact]
    public void TooFewArgumentsLeaveTheKeyUnanswered()
    {
        var ch = Someone();

        // Upstream returns false here rather than answering zero, so the caller sees
        // the key untouched instead of a made-up result.
        Assert.False(ch.TryGetProperty("SKILLUSEQUICK.Blacksmithing", out _));
        Assert.Empty(_calls);
    }

    [Fact]
    public void AnUnknownSkillIsNotRolled()
    {
        var ch = Someone();

        Assert.False(ch.TryGetProperty("SKILLUSEQUICK.NotASkill,300", out _));
        Assert.Empty(_calls);
    }

    [Fact]
    public void ASpaceSeparatorWorksToo()
    {
        var ch = Someone();

        Assert.True(ch.TryGetProperty("SKILLUSEQUICK Blacksmithing,300", out string? v));
        Assert.Equal("1", v);
        Assert.Single(_calls);
    }
}
