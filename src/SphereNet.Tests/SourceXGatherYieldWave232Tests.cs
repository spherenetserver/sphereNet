using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Scripting.Definitions;
using SphereNet.Scripting.Resources;

namespace SphereNet.Tests;

[Collection("DefinitionLoaderSerial")]
public sealed class SourceXGatherYieldWave232Tests
{
    [Theory]
    [InlineData(0, 10)]
    [InlineData(500, 20)]
    [InlineData(999, 29)]
    public void AmountCurve_UsesSourceXRandomCurveSample(int sample, int expected)
    {
        var resource = new RegionResourceDef(default);
        resource.LoadFromKey("AMOUNT", "10,30");

        Assert.Equal(expected, resource.GetRandomAmount(new FixedRandom(sample)));
    }

    [Theory]
    [InlineData(0, 999, 1)]
    [InlineData(1000, 0, 2)]
    public void ReapAmount_AveragesSkillLinearAndRandomCurve(
        int skill, int randomSample, int expected)
    {
        var resource = new RegionResourceDef(default);
        resource.LoadFromKey("AMOUNT", "10,30");
        resource.LoadFromKey("REAPAMOUNT", "1,3");

        Assert.Equal(expected,
            resource.GetRandomReapAmount(skill, new FixedRandom(randomSample)));
    }

    [Fact]
    public void MissingReapAmount_FallsBackToHalfOfAmountRandomLinear()
    {
        var resource = new RegionResourceDef(default);
        resource.LoadFromKey("AMOUNT", "10,30");

        Assert.Equal(10, resource.GetRandomReapAmount(1000, new FixedRandom(0)));
    }

    [Theory]
    [InlineData(SkillType.Mining, 0, 6)]
    [InlineData(SkillType.Mining, 1, 5)]
    [InlineData(SkillType.Lumberjacking, 0, 5)]
    [InlineData(SkillType.Lumberjacking, 1, 7)]
    public void HumanWorkhorse_AppliesMapAndResourceSpecificPoolBonus(
        SkillType skill, byte map, int expected)
    {
        var human = TestHarness.CreateWorld().CreateCharacter();
        human.BodyId = 0x0190;
        Character.RacialFlags = (int)RacialFlags.HumanWorkhorse;

        Assert.Equal(expected,
            GatheringEngine.ApplyWorkhorsePoolBonus(human, skill, map, 5));
    }

    [Fact]
    public void HumanWorkhorse_DoesNotApplyToOtherRaces()
    {
        var gargoyle = TestHarness.CreateWorld().CreateCharacter();
        gargoyle.BodyId = 0x029A;
        Character.RacialFlags = (int)RacialFlags.HumanWorkhorse;

        Assert.Equal(5,
            GatheringEngine.ApplyWorkhorsePoolBonus(gargoyle, SkillType.Mining, 0, 5));
    }

    [Fact]
    public void GatherSuccess_UsesResourceReapAmountCurve()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sphnet_gather_yield_{Guid.NewGuid():N}.scp");
        File.WriteAllText(path, """
            [REGIONRESOURCE r_wave232_ore]
            DEFNAME=r_wave232_ore
            AMOUNT=10
            REAP=0x19B9
            REAPAMOUNT=4
            SKILL=0.0

            [REGIONTYPE r_wave232_rock t_rock]
            DEFNAME=r_wave232_rock
            RESOURCES=100.0 r_wave232_ore
            """);

        try
        {
            using var loggerFactory = LoggerFactory.Create(_ => { });
            var resources = new ResourceHolder(loggerFactory.CreateLogger<ResourceHolder>());
            resources.LoadResourceFile(path);
            new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
            var world = TestHarness.CreateWorld();
            var character = world.CreateCharacter();
            Character.OnSkillUseQuickDetailed =
                (Character _, int _, ref int _, int _) => 1;

            var result = new GatheringEngine(world).TryGatherForSink(
                character, SkillType.Mining, new Point3D(100, 100, 0, 0));

            Assert.True(result.Success);
            Assert.NotNull(result.Item);
            Assert.Equal(4, result.Item!.Amount);
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
