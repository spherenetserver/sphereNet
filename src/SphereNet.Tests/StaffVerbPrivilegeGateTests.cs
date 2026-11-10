using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Speech;
using Xunit;

namespace SphereNet.Tests;

/// <summary>CServerConfig::CanUsePrivVerb (CServerConfig.cpp:2644-2669): a verb
/// aimed at a character is refused when the target out-ranks the caller, and the
/// privilege itself cannot be written through a verb line.</summary>
[Collection("DefinitionLoaderSerial")]
public sealed class StaffVerbPrivilegeGateTests
{
    private static (SphereNet.Game.Objects.Characters.Character gm,
        SphereNet.Game.Objects.Characters.Character other) Make(PrivLevel gmLevel, PrivLevel otherLevel)
    {
        var world = TestHarness.CreateWorld();
        var gm = world.CreateCharacter();
        world.PlaceCharacter(gm, new Point3D(100, 100, 0, 0));
        gm.PrivLevel = gmLevel;
        var other = world.CreateCharacter();
        world.PlaceCharacter(other, new Point3D(101, 100, 0, 0));
        other.PrivLevel = otherLevel;
        return (gm, other);
    }

    [Fact]
    public void ALowerStaffMemberCannotAimAVerbAtAHigherOne()
    {
        var (gm, owner) = Make(PrivLevel.Counsel, PrivLevel.Owner);
        Assert.False(new CommandHandler().ExecuteVerbForTarget(gm, "NAME", "Hacked", owner));
        Assert.NotEqual("Hacked", owner.Name);
    }

    [Fact]
    public void PlevelCannotBeWrittenThroughAnXVerb()
    {
        var (gm, _) = Make(PrivLevel.Counsel, PrivLevel.Player);
        var handler = new CommandHandler();
        Assert.False(handler.ExecuteVerbForTarget(gm, "PLEVEL", "7", gm));
        Assert.False(handler.ExecuteVerbForTarget(gm, "PRIVSET", "7", gm));
        Assert.Equal(PrivLevel.Counsel, gm.PrivLevel);
    }

    [Fact]
    public void AStaffMemberMayStillActOnAnEqualOrLowerOne()
    {
        var (gm, player) = Make(PrivLevel.GM, PrivLevel.Player);
        Assert.True(new CommandHandler().ExecuteVerbForTarget(gm, "NAME", "Renamed", player));
        Assert.Equal("Renamed", player.Name);
    }
}
