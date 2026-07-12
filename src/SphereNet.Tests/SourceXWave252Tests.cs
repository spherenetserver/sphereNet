using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 252 — effective-skill layer (reference Skill_GetAdjusted uiBonSkill term).
/// The per-character <c>SkillMod&lt;n&gt;</c> bonus rides on top of the raw base
/// skill, feeding the adjusted-skill reads (skill-list display, skill-use success)
/// WITHOUT touching the raw base that combat/crafting/skill-gain use.
/// </summary>
public sealed class SourceXWave252Tests
{
    private static Character MakeChar(GameWorld world)
    {
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void SkillMod_AbsentIsZero_BaseUnchanged()
    {
        var world = TestHarness.CreateWorld();
        var ch = MakeChar(world);
        ch.SetSkill(SkillType.Alchemy, 500);

        Assert.Equal(0, SkillEngine.GetSkillModBonus(ch, SkillType.Alchemy));
        Assert.Equal(500, SkillEngine.GetAdjustedSkill(ch, SkillType.Alchemy));
        Assert.Equal(500, ch.GetSkill(SkillType.Alchemy)); // raw base untouched
    }

    [Fact]
    public void SkillMod_AddsToAdjustedButNotBase()
    {
        var world = TestHarness.CreateWorld();
        var ch = MakeChar(world);
        ch.SetSkill(SkillType.Alchemy, 500);
        ch.SetTag($"SkillMod{(int)SkillType.Alchemy}", "150");

        Assert.Equal(150, SkillEngine.GetSkillModBonus(ch, SkillType.Alchemy));
        Assert.Equal(650, SkillEngine.GetAdjustedSkill(ch, SkillType.Alchemy)); // 500 + 150
        Assert.Equal(500, ch.GetSkill(SkillType.Alchemy));                       // base unchanged
    }

    [Fact]
    public void SkillMod_Negative_ClampsAdjustedToZero()
    {
        var world = TestHarness.CreateWorld();
        var ch = MakeChar(world);
        ch.SetSkill(SkillType.Alchemy, 100);
        ch.SetTag($"SkillMod{(int)SkillType.Alchemy}", "-300"); // curse debuff

        Assert.Equal(-300, SkillEngine.GetSkillModBonus(ch, SkillType.Alchemy));
        Assert.Equal(0, SkillEngine.GetAdjustedSkill(ch, SkillType.Alchemy)); // 100 - 300, clamped
    }

    [Fact]
    public void SkillMod_BareKeyPropertySet_PersistsAndReadsBack()
    {
        var world = TestHarness.CreateWorld();
        var ch = MakeChar(world);

        // A legacy @Equip script writes the bare key (no TAG. prefix), matching Sphere.
        Assert.True(ch.TrySetProperty($"SKILLMOD{(int)SkillType.Swordsmanship}", "200"));
        Assert.Equal(200, SkillEngine.GetSkillModBonus(ch, SkillType.Swordsmanship));

        Assert.True(ch.TryGetProperty($"SKILLMOD{(int)SkillType.Swordsmanship}", out string read));
        Assert.Equal("200", read);

        // Setting 0 clears it (@UnEquip).
        Assert.True(ch.TrySetProperty($"SKILLMOD{(int)SkillType.Swordsmanship}", "0"));
        Assert.Equal(0, SkillEngine.GetSkillModBonus(ch, SkillType.Swordsmanship));
        Assert.True(ch.TryGetProperty($"SKILLMOD{(int)SkillType.Swordsmanship}", out string cleared));
        Assert.Equal("0", cleared);
    }

    [Fact]
    public void SkillMod_UnsetPropertyReadsZero()
    {
        var world = TestHarness.CreateWorld();
        var ch = MakeChar(world);
        Assert.True(ch.TryGetProperty($"SKILLMOD{(int)SkillType.Magery}", out string read));
        Assert.Equal("0", read);
    }
}
