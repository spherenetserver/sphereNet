using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.World.Regions;
using SphereNet.Scripting.Definitions;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Source-X parity for the skill subsystem additions: new SkillDef keys,
/// per-character cap override tags, and the safe-region gain block.
/// </summary>
public class SkillSpellParityTests
{
    [Fact]
    public void SkillDef_ParsesRangeAndPromptCliloc()
    {
        var d = new SkillDef(ResourceId.Invalid);
        d.LoadFromKey("RANGE", "4");
        d.LoadFromKey("PROMPT_CLILOC", "1042000");
        Assert.Equal(4, d.Range);
        Assert.Equal("1042000", d.PromptCliloc);
    }

    [Fact]
    public void SkillDef_ParsesSymbolicPipeFlags()
    {
        var d = new SkillDef(ResourceId.Invalid);
        d.LoadFromKey("FLAGS", "skf_gather|skf_ranged");
        Assert.Equal((int)(SkillFlag.Gather | SkillFlag.Ranged), d.Flags);
        Assert.Equal(0x0600, d.Flags); // 0x400 | 0x200

        // Numeric/hex tokens still parse (leading zero = hex), and mix with names.
        var d2 = new SkillDef(ResourceId.Invalid);
        d2.LoadFromKey("FLAGS", "0800");
        Assert.Equal((int)SkillFlag.Disabled, d2.Flags);
    }

    [Fact]
    public void GetSkillSumMax_HonorsOverrideSkillSumTag()
    {
        var ch = new Character();
        // No tag → the existing class/global default.
        Assert.Equal(SkillEngine.SkillSumMaxOverride, SkillEngine.GetSkillSumMax(ch));
        ch.SetTag("OVERRIDE.SKILLSUM", "5000");
        Assert.Equal(5000, SkillEngine.GetSkillSumMax(ch));
    }

    [Fact]
    public void GetSkillDisplayCap_HonorsOverrideSkillCapTag()
    {
        var ch = new Character();
        var skill = SkillType.Swordsmanship;
        Assert.Equal(1000, SkillEngine.GetSkillDisplayCap(ch, skill)); // default
        ch.SetTag($"OVERRIDE.SKILLCAP_{(int)skill}", "800");
        Assert.Equal(800, SkillEngine.GetSkillDisplayCap(ch, skill));
    }

    [Fact]
    public void GainExperience_BlockedInSafeRegion()
    {
        var world = TestHarness.CreateWorld();
        var region = new Region { Name = "safe_gain_test", Flags = RegionFlag.Safe, MapIndex = 0 };
        region.AddRect(0, 0, 200, 200);
        world.AddRegion(region);

        var ch = world.CreateCharacter();
        ch.SetSkill(SkillType.Swordsmanship, 500);
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        // Many attempts; in a safe region none may advance the skill.
        for (int i = 0; i < 500; i++)
            SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

        Assert.Equal(500, ch.GetSkill(SkillType.Swordsmanship));
    }
}
