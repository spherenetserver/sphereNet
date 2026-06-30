using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.NPCs;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Source-X vendor/stable parity (wiki/10.txt audit): claiming a stabled pet does
// not lose it when the follower cap is full, and the buy price comes from the
// selected stock entry (not the first same-BaseId entry).
public class VendorStableParityTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static Item AddPack(GameWorld world, Character ch)
    {
        var pack = world.CreateItem();
        pack.ItemType = ItemType.Container;
        pack.BaseId = 0x0E75;
        ch.Backpack = pack;
        ch.Equip(pack, Layer.Pack);
        return pack;
    }

    // ---- #8: ClaimPet must not drop the pet when the follower cap is full ----

    [Fact]
    public void ClaimPet_FollowerCapFull_KeepsPetStabled()
    {
        var world = CreateWorld();
        var stable = new StableEngine();

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Fido";
        pet.NpcBrain = NpcBrainType.Animal;
        pet.TryAssignOwnership(owner, owner);
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        Assert.True(stable.StablePet(owner, pet, world));
        Assert.Equal(1, stable.GetStabledCount(owner));

        // Cap full → claim must fail WITHOUT removing the pet from the stable.
        owner.MaxFollower = 0;
        var claimedFail = stable.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0));
        Assert.Null(claimedFail);
        Assert.Equal(1, stable.GetStabledCount(owner)); // still stabled, not lost

        // Cap available → claim succeeds and the stable entry is consumed.
        owner.MaxFollower = 5;
        var claimedOk = stable.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0));
        Assert.NotNull(claimedOk);
        Assert.Equal(0, stable.GetStabledCount(owner));
    }

    // ---- #4: buy price uses the selected stock entry, not the first same-id one ----

    [Fact]
    public void ProcessBuy_SameBaseIdDifferentPrice_ChargesSelectedEntry()
    {
        var world = CreateWorld();
        VendorEngine.World = world;

        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(100, 100, 0, 0));

        var stock = world.CreateItem();
        stock.ItemType = ItemType.Container;
        stock.BaseId = 0x0E75;
        vendor.Equip(stock, Layer.VendorStock);

        // Two stock entries share a BaseId but have different prices; the cheap
        // one is added first (it is what GetServerBuyPrice-by-BaseId returns).
        var cheap = world.CreateItem();
        cheap.BaseId = 0x1F03; cheap.Amount = 5; cheap.SetTag("PRICE", "10");
        stock.AddItem(cheap);
        var dear = world.CreateItem();
        dear.BaseId = 0x1F03; dear.Amount = 5; dear.SetTag("PRICE", "999");
        stock.AddItem(dear);

        var buyer = world.CreateCharacter();
        buyer.IsPlayer = true;
        world.PlaceCharacter(buyer, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, buyer);
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED; gold.ItemType = ItemType.Gold; gold.Amount = 5000;
        pack.AddItem(gold);

        // Buy the EXPENSIVE entry — the server must charge 999, not the cheap 10.
        int cost = VendorEngine.ProcessBuy(buyer, vendor,
            new[] { new TradeEntry { ItemUid = dear.Uid, ItemId = dear.BaseId, Amount = 1 } });

        Assert.Equal(999, cost);
    }
}
