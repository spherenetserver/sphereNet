using System.Linq;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// What a house owes the world when it stops existing (port plan İŞ-34 / PLAN-502).
///
/// A locked-down item is NOT MOVABLE here - ObjAttributes.LockedDown fails
/// Item.IsMovableType - so one left behind by a deleted house is stuck in the world for
/// good, still linked to a multi that is gone. Source-X lets go of them
/// (UnlockAllItems, CItemMulti.cpp:1804) and hands the moving crate over
/// (TransferMovingCrateToBank, :1522), which also DELETES the crate when it is empty
/// rather than posting an empty box to someone's bank (:1539).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class HouseTeardownHoldingsParityTests
{
    private static (GameWorld World, HousingEngine Houses, Item Multi, House House, Character Owner) Setup()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 256, 256);
        ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;

        var houses = new HousingEngine(world, new MultiRegistry());

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.PrivLevel = PrivLevel.Player;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var bank = world.CreateItem();
        bank.ItemType = ItemType.EqBankBox;
        bank.BaseId = 0x09AB;
        owner.Equip(bank, Layer.BankBox);

        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.BaseId = 0x4064;
        multi.SetTag("HOUSE.OWNER", $"0{owner.Uid.Value:X}");
        world.PlaceItem(multi, new Point3D(100, 100, 10, 0));

        var house = houses.RegisterExistingMulti(multi);
        Assert.NotNull(house);
        house!.Owner = owner.Uid;
        Item.ResolveHouse = uid => uid == multi.Uid ? house : null;

        return (world, houses, multi, house, owner);
    }

    private static Item Rug(GameWorld world)
    {
        var rug = world.CreateItem();
        rug.BaseId = 0x176F;
        world.PlaceItem(rug, new Point3D(100, 100, 10, 0));
        return rug;
    }

    private static Item Bank(Character owner) => owner.GetEquippedItem(Layer.BankBox)!;

    // ---- deleting the house ---------------------------------------------

    [Fact]
    public void DeletingAHouseLetsGoOfWhatItHadLockedDown()
    {
        var (world, _, multi, house, owner) = Setup();
        var rug = Rug(world);
        Assert.True(house.Lockdown(rug.Uid, owner.Uid));
        Assert.False(rug.IsMovableType);          // that is the whole problem

        world.RemoveItem(multi);
        multi.Delete();

        Assert.False(rug.IsDeleted);
        Assert.True(rug.IsMovableType);           // the owner can pick it up again
        Assert.False(rug.Link.IsValid);       // no link to a multi that is gone
        Assert.True(rug.TryGetProperty($"ISEVENT.{House.LockdownEvent}", out string ev));
        Assert.Equal("0", ev);
    }

    [Fact]
    public void DeletingAHouseReleasesItsSecuredContainers()
    {
        var (world, _, multi, house, owner) = Setup();
        var chest = world.CreateItem();
        chest.ItemType = ItemType.Container;
        chest.BaseId = 0x0E3C;
        world.PlaceItem(chest, new Point3D(100, 100, 10, 0));
        Assert.True(house.SecureContainer(chest.Uid, owner.Uid));

        world.RemoveItem(multi);
        multi.Delete();

        Assert.False(chest.IsAttr(ObjAttributes.Secure));
        Assert.False(chest.Link.IsValid);
        Assert.True(chest.TryGetProperty($"ISEVENT.{House.SecureEvent}", out string ev));
        Assert.Equal("0", ev);
    }

    [Fact]
    public void DeletingAHouseHandsTheLoadedCrateToTheOwnersBank()
    {
        var (world, _, multi, house, owner) = Setup();
        var crate = house.GetMovingCrate(create: true)!;
        var goods = world.CreateItem();
        goods.BaseId = 0x0F51;
        Assert.True(crate.TryAddItem(goods));

        world.RemoveItem(multi);
        multi.Delete();

        Assert.False(crate.IsDeleted);
        Assert.Contains(crate, Bank(owner).Contents);
        Assert.Contains(goods, crate.Contents);
    }

    [Fact]
    public void AnEmptyCrateIsNotPostedToTheBank()
    {
        // Source-X deletes it instead (CItemMulti.cpp:1539).
        var (world, _, multi, house, owner) = Setup();
        var crate = house.GetMovingCrate(create: true)!;

        world.RemoveItem(multi);
        multi.Delete();

        Assert.True(crate.IsDeleted);
        Assert.Empty(Bank(owner).Contents);
    }

    [Fact]
    public void WithNoBankTheLoadedCrateLandsOnTheGroundRatherThanNowhere()
    {
        var (world, _, multi, house, owner) = Setup();
        var bank = Bank(owner);
        owner.Unequip(Layer.BankBox);
        world.RemoveItem(bank);

        var crate = house.GetMovingCrate(create: true)!;
        var goods = world.CreateItem();
        goods.BaseId = 0x0F51;
        Assert.True(crate.TryAddItem(goods));

        world.RemoveItem(multi);
        multi.Delete();

        Assert.False(crate.IsDeleted);
        Assert.False(crate.ContainedIn.IsValid);
        Assert.Contains(goods, crate.Contents);
    }

    // ---- redeeding ------------------------------------------------------

    [Fact]
    public void RedeedingDoesNotPostAnEmptyCrateEither()
    {
        var (world, houses, multi, house, owner) = Setup();
        var crate = house.GetMovingCrate(create: true)!;

        Assert.NotNull(houses.RedeedFromScript(multi.Uid));

        Assert.True(crate.IsDeleted);
        Assert.DoesNotContain(Bank(owner).Contents, i => i.BaseId == House.MovingCrateId);
    }

    [Fact]
    public void RedeedingPacksTheLockdownsIntoTheCrateItAlreadyHad()
    {
        var (world, houses, multi, house, owner) = Setup();
        var crate = house.GetMovingCrate(create: true)!;
        var rug = Rug(world);
        Assert.True(house.Lockdown(rug.Uid, owner.Uid));

        Assert.NotNull(houses.RedeedFromScript(multi.Uid));

        Assert.False(crate.IsDeleted);
        Assert.Contains(rug, crate.Contents);
        Assert.Contains(crate, Bank(owner).Contents);
    }

    // ---- one parent -----------------------------------------------------

    [Fact]
    public void ADeliveredCrateHasExactlyOneParent()
    {
        // The crate lived on the ground at house Z - 20; once it is in the bank it
        // must not still be a world item at that spot.
        var (world, _, multi, house, owner) = Setup();
        var crate = house.GetMovingCrate(create: true)!;
        var goods = world.CreateItem();
        goods.BaseId = 0x0F51;
        Assert.True(crate.TryAddItem(goods));
        var crateSpot = crate.Position;

        world.RemoveItem(multi);
        multi.Delete();

        Assert.Equal(Bank(owner).Uid, crate.ContainedIn);
        Assert.DoesNotContain(world.GetItemsInRange(crateSpot, 0), i => i == crate);
    }
}
