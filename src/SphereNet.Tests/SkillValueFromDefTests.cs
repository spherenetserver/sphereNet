using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Items;

namespace SphereNet.Tests;

/// <summary>
/// A skill set from a script takes the number the language writes.
///
/// The setter read a plain decimal and, when that failed, threw the value away while
/// still answering true - so the assignment looked accepted and the skill did not move.
/// &lt;DEF.name&gt; hands values back in hex, so a pack setting a skill from a DEF set
/// nothing at all:
///
///     SRC.ALLSKILLS 0
///     SRC.Alchemy=&lt;DEF.sn_player_skill_value&gt;     // 1000 arrives as 03E8
///
/// ended with every skill at zero, and the script's own guard reported that some other
/// trigger must have changed the result. Nothing had; the value was discarded in silence.
/// </summary>
public sealed class SkillValueFromDefTests
{
    private static SphereNet.Game.Objects.Characters.Character Fresh()
    {
        var world = TestHarness.CreateWorld();
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));
        return ch;
    }

    /// <summary>The form a DEF hands back.</summary>
    [Fact]
    public void AHexValueSetsTheSkill()
    {
        var ch = Fresh();
        Assert.True(ch.TrySetProperty("ALCHEMY", "03E8"));   // 1000
        Assert.Equal(1000, ch.GetSkill(SkillType.Alchemy));
    }

    /// <summary>Decimal still means decimal.</summary>
    [Fact]
    public void ADecimalValueStillSetsIt()
    {
        var ch = Fresh();
        ch.TrySetProperty("ALCHEMY", "1000");
        Assert.Equal(1000, ch.GetSkill(SkillType.Alchemy));
    }

    /// <summary>The whole sequence the pack writes: clear, then set seven from a hex
    /// value, and the total is what the script checks against.</summary>
    [Fact]
    public void SevenSkillsFromAHexValueTotalWhatTheyShould()
    {
        var ch = Fresh();
        ch.TryExecuteCommand("ALLSKILLS", "0", null!);

        foreach (string name in new[]
                 { "ALCHEMY", "ANATOMY", "ANIMALLORE", "ARMSLORE", "PARRYING",
                   "BEGGING", "BLACKSMITHING" })
            ch.TrySetProperty(name, "03E8");

        Assert.True(ch.TryGetProperty("SKILLTOTAL", out string total));
        Assert.Equal((7 * 1000).ToString(), total);
    }

    /// <summary>A value that is not a number at all leaves the skill alone rather than
    /// zeroing it.</summary>
    [Fact]
    public void RubbishLeavesTheSkillAlone()
    {
        var ch = Fresh();
        ch.TrySetProperty("ALCHEMY", "500");
        ch.TrySetProperty("ALCHEMY", "not a number");
        Assert.Equal(500, ch.GetSkill(SkillType.Alchemy));
    }
}
