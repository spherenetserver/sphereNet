using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Mounts;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Field report: the staff .bineq mount script does nothing. Its @DClick
/// body makes a new mount NPC a pet of the caster and then rides it:
///     NEW.MAKEMYPET &lt;SRC&gt;
///     SRC.MOUNT &lt;REF1&gt;
/// MOUNT is Source-X CHV_MOUNT: the verb's owner is the RIDER and the
/// argument is the horse uid (CChar.cpp → Horse_Mount).
/// </summary>
[Collection("VendorStateSerial")]
public sealed class StaffHorseScriptTests
{
    private static (GameWorld world, MountEngine engine) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        var engine = new MountEngine(world);
        Character.OnScriptMount = (rider, horse) => engine.TryMount(rider, horse);
        return (world, engine);
    }

    [Fact]
    public void MakeMyPetVerb_AssignsOwnership()
    {
        var (world, _) = Setup();
        var player = world.CreateCharacter();
        player.BodyId = 0x0190;
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var mount = world.CreateCharacter();
        mount.BodyId = 0x00C8; // a horse body
        world.PlaceCharacter(mount, new Point3D(100, 100, 0, 0));

        Assert.True(mount.TryExecuteCommand("MAKEMYPET", player.Uid.Value.ToString(), null!));

        Assert.True(mount.IsStatFlag(StatFlag.Pet));
        Assert.Equal(player.Uid, mount.OwnerSerial);
        Assert.True(mount.HasOwner(player.Uid));
    }

    [Fact]
    public void MountVerb_OnRiderWithHorseUid_SeatsTheRider()
    {
        var (world, _) = Setup();
        var player = world.CreateCharacter();
        player.BodyId = 0x0190;
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var mount = world.CreateCharacter();
        mount.BodyId = 0x00C8;
        world.PlaceCharacter(mount, new Point3D(100, 100, 0, 0));

        // The script order: pet first, then ride.
        Assert.True(mount.TryExecuteCommand("MAKEMYPET", player.Uid.Value.ToString(), null!));
        Assert.True(player.TryExecuteCommand("MOUNT", $"0{mount.Uid.Value:X}", null!));

        // The owner is now mounted, and the mount NPC was hidden/ridden.
        Assert.True(player.IsStatFlag(StatFlag.OnHorse));
        Assert.NotNull(player.GetEquippedItem(Layer.Horse));
        Assert.True(mount.IsStatFlag(StatFlag.Ridden));
    }

    [Fact]
    public void MountVerb_WithoutArgument_IsNoOp()
    {
        var (world, _) = Setup();
        var player = world.CreateCharacter();
        player.BodyId = 0x0190;
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var mount = world.CreateCharacter();
        mount.BodyId = 0x00C8;
        world.PlaceCharacter(mount, new Point3D(100, 100, 0, 0));
        Assert.True(mount.TryExecuteCommand("MAKEMYPET", player.Uid.Value.ToString(), null!));

        // Source-X resolves the argument uid; an NPC-side bare MOUNT names
        // no horse and must not seat the owner.
        Assert.True(mount.TryExecuteCommand("MOUNT", "", null!));
        Assert.False(player.IsStatFlag(StatFlag.OnHorse));
        Assert.False(mount.IsStatFlag(StatFlag.Ridden));
    }

    [Fact]
    public void MountProperty_StillReadsTheWornMountItem()
    {
        var (world, _) = Setup();
        var player = world.CreateCharacter();
        player.BodyId = 0x0190;
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var horseItem = world.CreateItem();
        horseItem.BaseId = 0x3E9F;
        player.Equip(horseItem, Layer.Horse);

        // The verb addition must not shadow the property read used by
        // <SRC.MOUNT> / IF (<FINDLAYER.25>) style scripts.
        Assert.True(player.TryGetProperty("MOUNT", out string val));
        Assert.Equal($"0{horseItem.Uid.Value:X}", val);
    }
}
