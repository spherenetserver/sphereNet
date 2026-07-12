using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Parsing;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXGatherDifficultyWave231Tests
{
    [Theory]
    [InlineData(0, 30)]
    [InlineData(500, 45)]
    [InlineData(999, 59)]
    public void TwoPointSkillCurve_MatchesSourceXRandomLinearSample(int sample, int expected)
    {
        var resource = new RegionResourceDef(default);
        resource.LoadFromKey("SKILL", "30.0,60.0");

        Assert.Equal(expected, resource.GetRandomSkillDifficulty(new FixedRandom(sample)));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(499, 29)]
    [InlineData(500, 30)]
    [InlineData(999, 89)]
    public void MultiPointSkillCurve_InterpolatesAcrossSourceXSegments(int sample, int expected)
    {
        var resource = new RegionResourceDef(default);
        resource.LoadFromKey("SKILL", "10.0,30.0,90.0");

        Assert.Equal(expected, resource.GetRandomSkillDifficulty(new FixedRandom(sample)));
    }

    [Fact]
    public void SinglePointSkillCurve_ProducesConstantDifficulty()
    {
        var resource = new RegionResourceDef(default);
        resource.LoadFromKey("SKILL", "42.0");

        Assert.Equal(42, resource.GetRandomSkillDifficulty(new FixedRandom(731)));
    }

    [Fact]
    public void RegionResourceParser_PreservesEverySkillCurvePoint()
    {
        var resource = new RegionResourceDef(default);
        resource.LoadFromKey("SKILL", "1.0,25.0,70.0,110.0");

        Assert.Equal([10, 250, 700, 1100], resource.SkillCurve);
        Assert.Equal(10, resource.SkillMin);
        Assert.Equal(1100, resource.SkillMax);
    }

    [Fact]
    public void GatherBelowFirstCurvePoint_StillRunsResourceDifficultyCheck()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_gather_curve_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [REGIONRESOURCE r_wave231_ore]
            DEFNAME=r_wave231_ore
            AMOUNT=5,5
            REAP=0x19B9
            REAPAMOUNT=1,1
            SKILL=60.0,110.0

            [REGIONTYPE r_wave231_rock t_rock]
            DEFNAME=r_wave231_rock
            RESOURCES=100.0 r_wave231_ore
            """);

        try
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
            resources.LoadResourceFile(path);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
            var world = TestHarness.CreateWorld();
            var character = world.CreateCharacter();
            character.SetSkill(SkillType.Mining, 0);
            int seenDifficulty = -1;
            Character.OnSkillUseQuickDetailed =
                (Character _, int skill, ref int difficulty, int _) =>
                {
                    Assert.Equal((int)SkillType.Mining, skill);
                    seenDifficulty = difficulty;
                    return 0;
                };

            var result = new GatheringEngine(world).TryGatherForSink(
                character, SkillType.Mining, new Point3D(100, 100, 0, 0));

            Assert.True(result.Handled);
            Assert.False(result.Success);
            Assert.InRange(seenDifficulty, 60, 109);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FixedRandom(int value) : Random
    {
        public override int Next(int maxValue) => Math.Clamp(value, 0, maxValue - 1);
    }
}
