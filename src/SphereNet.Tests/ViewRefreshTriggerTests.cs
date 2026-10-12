using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;

namespace SphereNet.Tests;

/// <summary>What makes the dirty drain rebuild the views around a character. Each
/// rebuild costs one view delta per nearby client, so a change that is not news, or
/// that already reached every screen, must not ask for one.</summary>
public class ViewRefreshTriggerTests
{
    [Fact]
    public void SettingAStatFlagThatIsAlreadySetIsNotAChange()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.SetStatFlag(StatFlag.InDoors);
        ch.ConsumeDirty();

        ch.SetStatFlag(StatFlag.InDoors);   // movement re-asserts these on every step
        ch.ClearStatFlag(StatFlag.Fly);

        Assert.Equal(DirtyFlag.None, ch.ConsumeDirty());
    }

    [Fact]
    public void ARealStatFlagChangeIsStillDirty()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.ConsumeDirty();

        ch.SetStatFlag(StatFlag.Hidden);
        Assert.Equal(DirtyFlag.StatFlags, ch.ConsumeDirty() & DirtyFlag.StatFlags);

        ch.ClearStatFlag(StatFlag.Hidden);
        Assert.Equal(DirtyFlag.StatFlags, ch.ConsumeDirty() & DirtyFlag.StatFlags);
    }

    [Fact]
    public void AStepAlreadyDeliveredNeedsNoRebuild()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Position = new Point3D(100, 100, 0, 0);
        ch.ConsumeDirty();

        ch.Position = new Point3D(101, 100, 0, 0);
        ch.MoveDelivered = (ch.Position, ch.Direction);
        ch.ConsumeDirty();

        Assert.True(SphereNet.Server.Program.IsDeliveredMoveOnly(ch));
    }

    [Fact]
    public void AStepNobodyWasToldAboutStillRebuilds()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.Position = new Point3D(100, 100, 0, 0);
        ch.MoveDelivered = (ch.Position, ch.Direction);
        ch.ConsumeDirty();

        // Moved by a path that did not go through the move broadcast.
        ch.Position = new Point3D(140, 100, 0, 0);
        ch.ConsumeDirty();

        Assert.False(SphereNet.Server.Program.IsDeliveredMoveOnly(ch));
    }

    [Fact]
    public void AStepTogetherWithAnotherChangeStillRebuilds()
    {
        var world = TestHarness.CreateWorld();
        var ch = world.CreateCharacter();
        ch.ConsumeDirty();

        ch.Position = new Point3D(101, 100, 0, 0);
        ch.SetStatFlag(StatFlag.Hidden);
        ch.MoveDelivered = (ch.Position, ch.Direction);
        ch.ConsumeDirty();

        Assert.False(SphereNet.Server.Program.IsDeliveredMoveOnly(ch));
    }
}
