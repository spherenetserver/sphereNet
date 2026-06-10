using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Definitions;
using SphereNet.Game.Magic;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using SphereNet.Scripting.Resources;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Stat-gain parity (reference Skill_Experience stat section + the
/// [ADVANCE] curves): stats train toward the skill's STAT_* ceiling, only
/// while the skill lock is Up, polymorph blocks STR/DEX, and without
/// [ADVANCE] curves no stat gain happens at all.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class StatGainParityTests
{
    private static void LoadDefinitions(string contents)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"spherenet_statgain_{Guid.NewGuid():N}.scp");
        File.WriteAllText(tempFile, contents);
        var resources = new ResourceHolder(LoggerFactory.Create(_ => { }).CreateLogger<ResourceHolder>())
        {
            ScpBaseDir = Path.GetDirectoryName(tempFile) ?? ""
        };
        resources.LoadResourceFile(tempFile);
        new DefinitionLoader(resources, new SpellRegistry()).LoadAll();
    }

    private const string TrainingDefs = """
        [ADVANCE]
        STR=1.0,1.0
        INT=1.0,1.0
        DEX=1.0,1.0

        [SKILL 40]
        STAT_STR=100
        BONUS_STATS=100
        BONUS_STR=100
        """;

    private static Character CreatePlayer()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.Str = 50; ch.Dex = 30; ch.Int = 30;
        ch.MaxHits = 50; ch.MaxStam = 30; ch.MaxMana = 30;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void StatGain_TrainsTowardSkillStatTarget()
    {
        LoadDefinitions(TrainingDefs);
        var ch = CreatePlayer();
        ch.SetSkill(SkillType.Swordsmanship, 200);

        // chance = (100000 / 10) * 100 * 100 / 10000 = 10000 per-mille → certain.
        for (int i = 0; i < 10; i++)
            SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

        Assert.True(ch.Str > 50, "STR should train toward the skill's STAT_STR target");
        Assert.Equal(30, ch.Dex);
        Assert.Equal(30, ch.Int);
    }

    [Fact]
    public void StatGain_RequiresSkillLockUp()
    {
        LoadDefinitions(TrainingDefs);
        var ch = CreatePlayer();
        ch.SetSkill(SkillType.Swordsmanship, 200);
        ch.SetSkillLock(SkillType.Swordsmanship, 1); // Down

        for (int i = 0; i < 50; i++)
            SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

        Assert.Equal(50, ch.Str);
    }

    [Fact]
    public void StatGain_PolymorphBlocksStrTraining()
    {
        LoadDefinitions(TrainingDefs);
        var ch = CreatePlayer();
        ch.SetSkill(SkillType.Swordsmanship, 200);
        ch.SetStatFlag(StatFlag.Polymorph);

        for (int i = 0; i < 50; i++)
            SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

        Assert.Equal(50, ch.Str);
    }

    [Fact]
    public void StatGain_WithoutAdvanceCurves_DoesNothing()
    {
        LoadDefinitions("""
            [SKILL 40]
            STAT_STR=100
            BONUS_STATS=100
            BONUS_STR=100
            """);
        var ch = CreatePlayer();
        ch.SetSkill(SkillType.Swordsmanship, 200);

        for (int i = 0; i < 50; i++)
            SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

        Assert.Equal(50, ch.Str);
    }

    [Fact]
    public void StatGain_StopsAtSkillStatTarget()
    {
        LoadDefinitions(TrainingDefs);
        var ch = CreatePlayer();
        ch.SetSkill(SkillType.Swordsmanship, 200);

        for (int i = 0; i < 200; i++)
            SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

        Assert.True(ch.Str <= 100, $"STR must not exceed the STAT_STR target (got {ch.Str})");
    }
}
