using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Wave 251 — economy-risky group (safe subset): owned-vendor CASH dispense.
/// Source-X NPC_VendorGetChkVerb PC_CASH hands the vendor purse to the owner.
/// The safety property is that an OWNED vendor's purse is never topped up by
/// restock (only plain shopkeepers get the infinite buy fund), so dispensing
/// it cannot mint gold.
/// </summary>
public sealed class SourceXWave251Tests
{
    private static (GameWorld world, Character owner) MakeOwnerWithPack(GameWorld world)
    {
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.PrivLevel = PrivLevel.GM; // skip weight/CanCarry — this is about gold flow.
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        owner.Equip(pack, Layer.Pack);
        return (world, owner);
    }

    private static Character MakeVendor(GameWorld world, Character? owner)
    {
        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        if (owner != null)
            vendor.SetTag("OWNER_UID", owner.Uid.Value.ToString());
        world.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));
        return vendor;
    }

    [Fact]
    public void Restock_OwnedVendor_DoesNotTopUpPurse()
    {
        var world = TestHarness.CreateWorld();
        var old = VendorEngine.World;
        VendorEngine.World = world;
        try
        {
            var (_, owner) = MakeOwnerWithPack(world);
            var vendor = MakeVendor(world, owner);

            Assert.Equal(0, VendorEngine.GetVendorGold(vendor));
            VendorEngine.RestockVendor(vendor);
            // Owned vendor keeps only real earnings — restock must NOT mint the buy fund.
            Assert.Equal(0, VendorEngine.GetVendorGold(vendor));
        }
        finally { VendorEngine.World = old; }
    }

    [Fact]
    public void Restock_UnownedVendor_TopsUpPurse()
    {
        var world = TestHarness.CreateWorld();
        var old = VendorEngine.World;
        VendorEngine.World = world;
        try
        {
            var vendor = MakeVendor(world, owner: null);

            Assert.Equal(0, VendorEngine.GetVendorGold(vendor));
            VendorEngine.RestockVendor(vendor);
            // Plain shopkeeper gets the infinite buy fund (Source-X vendor bank).
            Assert.Equal(VendorEngine.RestockGold, VendorEngine.GetVendorGold(vendor));
        }
        finally { VendorEngine.World = old; }
    }

    [Fact]
    public void DispenseVendorGold_GivesRealEarningsToOwnerAndZeroesPurse()
    {
        var world = TestHarness.CreateWorld();
        var old = VendorEngine.World;
        VendorEngine.World = world;
        try
        {
            var (_, owner) = MakeOwnerWithPack(world);
            var vendor = MakeVendor(world, owner);

            // Simulate accumulated earnings (a real player buy would credit this).
            vendor.SetTag("VENDOR_GOLD", "500");

            int dispensed = VendorEngine.DispenseVendorGold(vendor, owner);

            Assert.Equal(500, dispensed);
            Assert.Equal(0, VendorEngine.GetVendorGold(vendor)); // purse emptied
            Assert.Equal(500, VendorEngine.CountGold(owner));    // gold landed in owner's pack
        }
        finally { VendorEngine.World = old; }
    }

    [Fact]
    public void DispenseVendorGold_EmptyPurse_GivesNothing()
    {
        var world = TestHarness.CreateWorld();
        var old = VendorEngine.World;
        VendorEngine.World = world;
        try
        {
            var (_, owner) = MakeOwnerWithPack(world);
            var vendor = MakeVendor(world, owner);

            int dispensed = VendorEngine.DispenseVendorGold(vendor, owner);

            Assert.Equal(0, dispensed);
            Assert.Equal(0, VendorEngine.CountGold(owner));
        }
        finally { VendorEngine.World = old; }
    }
}
