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
///     REF1.MOUNT
/// Both were unimplemented — MAKEMYPET was not a char verb at all, and
/// MOUNT existed only as a property READ (returns the worn mount item),
/// never as the ride VERB.
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
        Character.OnScriptMount = npc =>
        {
            var owner = npc.OwnerSerial.IsValid ? world.FindChar(npc.OwnerSerial) : null;
            return owner != null && engine.TryMount(owner, npc);
        };
        return (world, engine);
    }

    [Fact]
    public void MakeMyPetVerb_AssignsOwnership()
    {
        var (world, _) = Setup();
        var player = world.CreateCharacter();
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
    public void MountVerb_OnOwnedNpc_SeatsTheOwner()
    {
        var (world, _) = Setup();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));

        var mount = world.CreateCharacter();
        mount.BodyId = 0x00C8;
        world.PlaceCharacter(mount, new Point3D(100, 100, 0, 0));

        // The script order: pet first, then ride.
        Assert.True(mount.TryExecuteCommand("MAKEMYPET", player.Uid.Value.ToString(), null!));
        Assert.True(mount.TryExecuteCommand("MOUNT", "", null!));

        // The owner is now mounted, and the mount NPC was hidden/ridden.
        Assert.True(player.IsStatFlag(StatFlag.OnHorse));
        Assert.NotNull(player.GetEquippedItem(Layer.Horse));
        Assert.True(mount.IsStatFlag(StatFlag.Ridden));
    }

    [Fact]
    public void MountProperty_StillReadsTheWornMountItem()
    {
        var (world, _) = Setup();
        var player = world.CreateCharacter();
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
