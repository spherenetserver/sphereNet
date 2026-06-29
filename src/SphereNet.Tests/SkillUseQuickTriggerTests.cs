using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the @SkillUseQuick trigger contract. SkillEngine.UseQuick fires
// @SkillUseQuick (via Character.OnSkillUseQuick) AFTER the success roll with
// ARGN3 = result; the script can flip the result or cancel the use (RETURN 1 →
// negative return). The hook is nulled between tests by ResetEngineStatics.
public class SkillUseQuickTriggerTests
{
    private static (GameWorld world, Character player) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        player.SetSkill(SkillType.Hiding, 500);
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        return (world, player);
    }

    [Fact]
    public void SkillUseQuick_FiresAfterCheck_WithSkillDifficultyAndResult()
    {
        var (_, player) = Setup();
        int firedSkill = -1, firedDiff = -1, firedResult = -1;
        Character.OnSkillUseQuick = (_, skill, diff, result) =>
        { firedSkill = skill; firedDiff = diff; firedResult = result; return result; };

        SkillEngine.UseQuick(player, SkillType.Hiding, 50);

        Assert.Equal((int)SkillType.Hiding, firedSkill);
        Assert.Equal(50, firedDiff);
        Assert.True(firedResult is 0 or 1); // the rolled result was passed in
    }

    [Fact]
    public void SkillUseQuick_NegativeReturn_CancelsUseAndGain()
    {
        var (_, player) = Setup();
        Character.OnSkillUseQuick = (_, _, _, _) => -1; // cancel the quick use

        bool success = SkillEngine.UseQuick(player, SkillType.Hiding, 50);

        Assert.False(success);                                // cancelled → failure
        Assert.Equal(500, player.GetSkill(SkillType.Hiding)); // no gain ran
    }

    [Fact]
    public void SkillUseQuick_CanFlipResultToSuccess()
    {
        var (_, player) = Setup();
        Character.OnSkillUseQuick = (_, _, _, _) => 1; // force success

        // High difficulty would normally fail for a 50.0-skill hider.
        bool success = SkillEngine.UseQuick(player, SkillType.Hiding, 1000);

        Assert.True(success);
    }
}
