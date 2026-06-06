using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Skills;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies the @SkillUseQuick trigger and its check/gain split. SkillEngine.UseQuick
// now fires @SkillUseQuick (via Character.OnSkillUseQuick) BEFORE the success check,
// so a script can cancel the quick use (RETURN 1) before any roll or experience
// gain. The hook is nulled between tests by ResetEngineStatics.
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
    public void SkillUseQuick_FiresBeforeCheck_WithSkillAndDifficulty()
    {
        var (_, player) = Setup();
        int firedSkill = -1, firedDiff = -1;
        Character.OnSkillUseQuick = (_, skill, diff) => { firedSkill = skill; firedDiff = diff; return false; };

        SkillEngine.UseQuick(player, SkillType.Hiding, 50);

        Assert.Equal((int)SkillType.Hiding, firedSkill);
        Assert.Equal(50, firedDiff);
    }

    [Fact]
    public void SkillUseQuick_ReturnTrue_CancelsBeforeRollAndGain()
    {
        var (_, player) = Setup();
        Character.OnSkillUseQuick = (_, _, _) => true; // cancel the quick use

        bool success = SkillEngine.UseQuick(player, SkillType.Hiding, 50);

        Assert.False(success);                                    // cancelled → failure
        Assert.Equal(500, player.GetSkill(SkillType.Hiding));     // no roll/gain ran
    }
}
