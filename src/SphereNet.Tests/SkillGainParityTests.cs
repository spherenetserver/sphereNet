using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Skill gain/atrophy parity (reference Skill_Experience/Skill_Decrease):
/// the total cap blocks the gain roll but not the decay roll, so a capped
/// character erodes a DOWN-locked skill and keeps training; without a
/// DOWN-locked skill the cap freezes values.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public class SkillGainParityTests
{
    private static Character CreatePlayer()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    [Fact]
    public void AtTotalCap_DownLockedSkillDecays_AndTrainedSkillGains()
    {
        TestHarness.SeedSkillAdvRates(); // gain follows ADV_RATE strictly (no curve = no gain)
        int savedCap = SkillEngine.SkillSumMaxOverride;
        try
        {
            SkillEngine.SkillSumMaxOverride = 1500;
            var ch = CreatePlayer();
            ch.SetSkill(SkillType.Swordsmanship, 500); // trained skill (lock Up)
            ch.SetSkill(SkillType.Begging, 1000);      // sacrifice skill
            ch.SetSkillLock(SkillType.Begging, 1);     // Down

            for (int i = 0; i < 4000; i++)
                SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

            Assert.True(ch.GetSkill(SkillType.Begging) < 1000,
                "DOWN-locked skill should decay at the total cap");
            Assert.True(ch.GetSkill(SkillType.Swordsmanship) > 500,
                "trained skill should gain once decay opens room");
        }
        finally
        {
            SkillEngine.SkillSumMaxOverride = savedCap;
        }
    }

    [Fact]
    public void AtTotalCap_WithoutDownLock_ValuesStayFrozen()
    {
        int savedCap = SkillEngine.SkillSumMaxOverride;
        try
        {
            SkillEngine.SkillSumMaxOverride = 1500;
            var ch = CreatePlayer();
            ch.SetSkill(SkillType.Swordsmanship, 500);
            ch.SetSkill(SkillType.Begging, 1000); // lock Up (default)

            for (int i = 0; i < 1000; i++)
                SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

            Assert.Equal(500, ch.GetSkill(SkillType.Swordsmanship));
            Assert.Equal(1000, ch.GetSkill(SkillType.Begging));
        }
        finally
        {
            SkillEngine.SkillSumMaxOverride = savedCap;
        }
    }

    [Fact]
    public void HighSkill_LowDifficulty_CanStillGain_WithoutGainRadiusDefault()
    {
        TestHarness.SeedSkillAdvRates(); // gain follows ADV_RATE strictly (no curve = no gain)
        var ch = CreatePlayer();
        ch.SetSkill(SkillType.Swordsmanship, 900);

        bool gained = false;
        for (int i = 0; i < 8000 && !gained; i++)
        {
            SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 1);
            gained = ch.GetSkill(SkillType.Swordsmanship) > 900;
        }

        Assert.True(gained,
            "without a script GAINRADIUS the low-difficulty gain roll must still run");
    }

    [Fact]
    public void SkillGainCheck_PreRollHook_CancelsAndTunesChance()
    {
        var saved = SkillEngine.OnSkillGainCheck;
        try
        {
            var ch = CreatePlayer();
            ch.SetSkill(SkillType.Hiding, 100);
            ch.SetSkillLock(SkillType.Hiding, 0); // Up (gainable)

            // (a) RETURN-1 style cancel: the pre-roll hook returns true → no gain.
            SkillEngine.OnSkillGainCheck = (Character c, SkillType s, ref int chance, ref int max) => true;
            for (int i = 0; i < 5000; i++)
                SkillEngine.GainExperience(ch, SkillType.Hiding, 50);
            Assert.Equal(100, ch.GetSkill(SkillType.Hiding)); // cancelled before the roll

            // (b) chance override: force the per-mille chance to its max → gains promptly.
            SkillEngine.OnSkillGainCheck = (Character c, SkillType s, ref int chance, ref int max) =>
            {
                chance = 1000;
                return false;
            };
            bool gained = false;
            for (int i = 0; i < 200 && !gained; i++)
            {
                SkillEngine.GainExperience(ch, SkillType.Hiding, 50);
                gained = ch.GetSkill(SkillType.Hiding) > 100;
            }
            Assert.True(gained, "a forced chance=1000 must produce a gain");
        }
        finally
        {
            SkillEngine.OnSkillGainCheck = saved;
        }
    }

    [Fact]
    public void LockedSkill_NeverChanges()
    {
        var ch = CreatePlayer();
        ch.SetSkill(SkillType.Swordsmanship, 500);
        ch.SetSkillLock(SkillType.Swordsmanship, 2); // Locked

        for (int i = 0; i < 1000; i++)
            SkillEngine.GainExperience(ch, SkillType.Swordsmanship, 50);

        Assert.Equal(500, ch.GetSkill(SkillType.Swordsmanship));
    }
}
