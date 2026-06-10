using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Success S-curve (reference Calc_GetSCurve, SKILL_VARIANCE=150) and the
/// stat-adjusted skill value (reference Skill_GetAdjusted) used by checks.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class SkillSuccessCurveTests
{
    [Theory]
    [InlineData(0, 500)]     // skill == difficulty → 50%
    [InlineData(150, 750)]   // one variance above → 75%
    [InlineData(-150, 250)]  // one variance below → 25%
    [InlineData(300, 875)]
    [InlineData(-300, 125)]
    [InlineData(450, 937)]   // 1000 - (125 - 62)
    public void CalcSCurve_MatchesReferenceHalvingCurve(int delta, int expected)
    {
        Assert.Equal(expected, SkillEngine.CalcSCurve(delta, 150));
    }

    [Fact]
    public void GetAdjustedSkill_AddsBonusStatsPercentOfWeightedStats()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_adj_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, """
            [SKILL 0]
            BONUS_STATS=10
            BONUS_STR=100
            """);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();

        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 50;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        ch.SetSkill(SkillType.Alchemy, 500);

        // base 500 + 10 * (100 * 50) / 10000 = 505
        Assert.Equal(505, SkillEngine.GetAdjustedSkill(ch, SkillType.Alchemy));
        // No def loaded for Anatomy → raw base value.
        ch.SetSkill(SkillType.Anatomy, 400);
        Assert.Equal(400, SkillEngine.GetAdjustedSkill(ch, SkillType.Anatomy));
    }
}
