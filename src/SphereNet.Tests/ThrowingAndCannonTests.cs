using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Combat;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;

namespace SphereNet.Tests;

/// <summary>
/// Audit findings (wiki/test.txt #4/#5): throwing weapons are ranged and fire
/// themselves (no bolt default, no pack ammo), and the cannon muzzle has a
/// real load-powder / load-shot / fire state machine instead of a message.
/// </summary>
[Collection("VendorStateSerial")]
public sealed class ThrowingAndCannonTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void ThrowingWeapon_IsRanged_AndConsumesNoBoltDefault()
    {
        var world = CreateWorld();
        var glaive = world.CreateItem();
        glaive.ItemType = ItemType.WeaponThrowing;

        Assert.True(CombatHelper.IsRangedWeapon(glaive));
        Assert.True(CombatHelper.IsThrowingWeapon(glaive));
        Assert.False(CombatHelper.IsMeleeWeapon(glaive));

        // No crossbow-bolt fallback: gfx 0 = fly the weapon's own graphic,
        // and no ammo item id/type is demanded.
        var spec = CombatHelper.ResolveAmmoSpec(null, ItemType.WeaponThrowing, null);
        Assert.Equal(0, spec.Gfx);
        Assert.Equal(0, spec.BaseId);
    }

    [Fact]
    public void CannonMuzzle_LoadsPowderThenShot_ThenFiresForDamage()
    {
        var world = CreateWorld();
        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        player.Backpack = pack;
        player.Equip(pack, Layer.Pack);

        var muzzle = world.CreateItem();
        muzzle.BaseId = 0x0E8D;
        muzzle.ItemType = ItemType.CannonMuzzle;
        world.PlaceItem(muzzle, new Point3D(100, 101, 0, 0));

        var powder = world.CreateItem();
        powder.BaseId = 0x0F8C; // i_reag_sulfur_ash
        powder.Amount = 5;
        pack.AddItem(powder);

        var ball = world.CreateItem();
        ball.BaseId = 0x0E73;
        ball.ItemType = ItemType.CannonBall;
        pack.AddItem(ball);

        var victim = world.CreateCharacter();
        victim.MaxHits = 250;
        victim.Hits = 250;
        world.PlaceCharacter(victim, new Point3D(105, 101, 0, 0));

        var loggerFactory = LoggerFactory.Create(_ => { });
        var client = TestHarness.CreateClient(loggerFactory, world,
            new SphereNet.Game.Accounts.AccountManager(loggerFactory), 2613);
        TestHarness.AttachCharacter(client, player);

        // 1. Needs powder.
        client.HandleDoubleClick(muzzle.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId,
            powder.Uid.Value, powder.X, powder.Y, powder.Z, 0);
        Assert.Equal(1u, muzzle.More1 & 3);
        Assert.Equal(4, powder.Amount);

        // 2. Needs shot.
        client.HandleDoubleClick(muzzle.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId,
            ball.Uid.Value, ball.X, ball.Y, ball.Z, 0);
        Assert.Equal(3u, muzzle.More1 & 3);
        Assert.True(ball.IsDeleted);

        // 3. Armed — fire at the victim: load resets, heavy damage lands.
        client.HandleDoubleClick(muzzle.Uid.Value);
        client.HandleTargetResponse(0, client.ActiveTargetCursorId,
            victim.Uid.Value, victim.X, victim.Y, victim.Z, 0);
        Assert.Equal(0u, muzzle.More1 & 3);
        Assert.True(victim.Hits <= 170,
            $"cannon fire dealt no meaningful damage (hits={victim.Hits})");
    }
}
