using SphereNet.Core.Types;
using SphereNet.Game.Messages;
using SphereNet.Game.Mounts;
using SphereNet.Game.Objects.Characters;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Horse_Mount says why it refuses (CCharAct.cpp:3950-3980): "You dont own that horse."
/// for a creature that is not the rider's (MSG_MOUNT_DONTOWN). A stranger's attempt was
/// answered by SphereNet's own "That target cannot be mounted.", and an out-of-reach one
/// by "That is too far away." instead of MSG_MOUNT_DIST.
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class MountRefusalMessageTests
{
    [Fact]
    public void AStrangerIsToldTheHorseIsNotTheirs()
    {
        var world = TestHarness.CreateWorld();
        var engine = new MountEngine(world);
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.BodyId = 0x0190;
        world.PlaceCharacter(owner, new Point3D(99, 100, 0, 0));
        var stranger = world.CreateCharacter();
        stranger.IsPlayer = true;
        stranger.BodyId = 0x0190;
        world.PlaceCharacter(stranger, new Point3D(100, 100, 0, 0));
        var horse = world.CreateCharacter();
        horse.BodyId = 0x00C8;
        world.PlaceCharacter(horse, new Point3D(101, 100, 0, 0));
        horse.TryAssignOwnership(owner, owner);
        var told = new List<string>();
        Character.SendOwnerMessage = (ch, text) => { if (ch == stranger) told.Add(text); };

        Assert.False(engine.TryMount(stranger, horse));

        Assert.Contains(ServerMessages.Get(Msg.MsgMountDontown), told);
    }
}
