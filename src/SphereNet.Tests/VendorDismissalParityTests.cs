using System;
using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a dismissed player vendor gives back (port plan İŞ-29 / PLAN-405).
///
/// Source-X empties a vendor the moment it loses its owner: NPC_PetClearOwners moves
/// the purse and every vendor-layer container's contents into the OWNER'S BANK and
/// drops the vendor's invulnerability (CCharNPCPet.cpp:562-584). Releasing one here
/// only cleared the ownership flags, so a shopkeeper's takings and everything it had
/// bought from players went ownerless with it.
///
/// Only what really exists comes back: this engine's SELL stock is a template rebuilt
/// on demand and never persisted, so returning that would mint items rather than
/// return them.
/// </summary>
public sealed class VendorDismissalParityTests : IDisposable
{
    private readonly GameWorld? _savedWorld = VendorEngine.World;

    public void Dispose() => VendorEngine.World = _savedWorld;

    private (GameWorld World, Character Owner, Character Vendor, Item Bank, Item Extra) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        VendorEngine.World = world;

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var bank = world.CreateItem();
        bank.ItemType = ItemType.EqBankBox;
        bank.BaseId = 0x09AB;
        owner.Equip(bank, Layer.BankBox);

        var vendor = world.CreateCharacter();
        vendor.Name = "the shopkeeper";
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));
        Assert.True(vendor.TryAssignOwnership(owner, owner));

        var extra = world.CreateItem();
        extra.ItemType = ItemType.EqVendorBox;
        extra.BaseId = 0x09AB;
        vendor.Equip(extra, Layer.VendorExtra);

        return (world, owner, vendor, bank, extra);
    }

    [Fact]
    public void TheStockItBoughtGoesToTheOwnersBank()
    {
        var (world, owner, vendor, bank, extra) = Setup();
        var goods = world.CreateItem();
        goods.BaseId = 0x0F51;
        goods.Name = "a dagger";
        Assert.True(extra.TryAddItem(goods));

        vendor.ClearOwnership(clearFriends: true);

        Assert.Contains(goods, bank.Contents);
        Assert.DoesNotContain(goods, extra.Contents);
    }

    [Fact]
    public void TheTakingsGoToTheOwnersBankToo()
    {
        var (_, owner, vendor, bank, _) = Setup();
        vendor.SetTag("VENDOR_GOLD", "1234");

        vendor.ClearOwnership(clearFriends: true);

        int banked = bank.Contents
            .Where(i => i.ItemType == ItemType.Gold || i.BaseId == 0x0EED)
            .Sum(i => (int)i.Amount);
        Assert.Equal(1234, banked);
        Assert.Equal(0, VendorEngine.GetVendorGold(vendor));
    }

    [Fact]
    public void ADismissedVendorIsNoLongerInvulnerable()
    {
        var (_, _, vendor, _, _) = Setup();
        vendor.SetStatFlag(StatFlag.Invul);

        vendor.ClearOwnership(clearFriends: true);

        Assert.False(vendor.IsStatFlag(StatFlag.Invul));
    }

    [Fact]
    public void WithNoBankTheGoodsLandAtTheOwnersFeetRatherThanNowhere()
    {
        var (world, owner, vendor, bank, extra) = Setup();
        owner.Unequip(Layer.BankBox);
        world.RemoveItem(bank);

        var goods = world.CreateItem();
        goods.BaseId = 0x0F51;
        Assert.True(extra.TryAddItem(goods));

        vendor.ClearOwnership(clearFriends: true);

        Assert.False(goods.IsDeleted);
        Assert.False(goods.ContainedIn.IsValid);
        Assert.Equal(owner.Position.X, goods.X);
        Assert.Equal(owner.Position.Y, goods.Y);
    }

    [Fact]
    public void AnOrdinaryPetGivesNothingBack()
    {
        // The return is a VENDOR rule; releasing a plain pet must not reach into it.
        var (world, owner, _, bank, _) = Setup();

        var pet = world.CreateCharacter();
        pet.NpcBrain = NpcBrainType.Animal;
        world.PlaceCharacter(pet, new Point3D(102, 100, 0, 0));
        Assert.True(pet.TryAssignOwnership(owner, owner));
        pet.SetTag("VENDOR_GOLD", "500");   // not a vendor: never consulted

        pet.ClearOwnership(clearFriends: true);

        Assert.Empty(bank.Contents);
        Assert.Equal(500, VendorEngine.GetVendorGold(pet));
    }
}
