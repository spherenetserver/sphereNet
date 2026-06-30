using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
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

    // ---- #10: stable snapshots the FULL skill (no 1000 clip = data loss) ----

    [Fact]
    public void StablePet_SnapshotsFullSkill_NotClippedAt1000()
    {
        var world = CreateWorld();
        var stable = new StableEngine();

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.MaxFollower = 5;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var pet = world.CreateCharacter();
        pet.Name = "Magus";
        pet.NpcBrain = NpcBrainType.Animal;
        pet.TryAssignOwnership(owner, owner);
        pet.SetSkill(SkillType.Magery, 1200); // 120.0 — above the old 1000 clip
        world.PlaceCharacter(pet, new Point3D(101, 100, 0, 0));

        Assert.True(stable.StablePet(owner, pet, world));
        var claimed = stable.ClaimPet(owner, 0, world, new Point3D(100, 100, 0, 0));

        Assert.NotNull(claimed);
        Assert.Equal(1200, claimed!.GetSkill(SkillType.Magery)); // full value, not clipped
    }

    // ---- #10: stable capacity scales with handling skills / MAXPLAYERPETS ----

    [Fact]
    public void GetMaxStabledPets_ScalesWithSkillsAndTagOverride()
    {
        var world = CreateWorld();
        var owner = world.CreateCharacter();

        Assert.Equal(5, StableEngine.GetMaxStabledPets(owner)); // unskilled = base

        owner.SetSkill(SkillType.Taming, 1200);
        owner.SetSkill(SkillType.AnimalLore, 1200);
        owner.SetSkill(SkillType.Veterinary, 1200); // sum 3600 -> +6 slots
        Assert.Equal(11, StableEngine.GetMaxStabledPets(owner));

        owner.SetTag("MAXPLAYERPETS", "3"); // explicit override wins
        Assert.Equal(3, StableEngine.GetMaxStabledPets(owner));
    }

    // ---- #5: bought item is a FULL clone (per-instance state travels with it) ----

    [Fact]
    public void ProcessBuy_FullClonesStockState()
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

        var entry = world.CreateItem();
        entry.BaseId = 0x0F52; entry.Amount = 9; entry.SetTag("PRICE", "10");
        entry.SetTag("CUSTOM_STATE", "kept"); // per-instance state to verify the clone
        stock.AddItem(entry);

        var buyer = world.CreateCharacter();
        buyer.IsPlayer = true;
        world.PlaceCharacter(buyer, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, buyer);
        var gold = world.CreateItem();
        gold.BaseId = 0x0EED; gold.ItemType = ItemType.Gold; gold.Amount = 5000;
        pack.AddItem(gold);

        int cost = VendorEngine.ProcessBuy(buyer, vendor,
            new[] { new TradeEntry { ItemUid = entry.Uid, ItemId = entry.BaseId, Amount = 3 } });
        Assert.Equal(30, cost);

        // The bought item is a full clone: it carries the per-instance tag, not just
        // id/hue/name (the old shallow copy silently dropped it).
        var bought = world.GetContainerContents(pack.Uid)
            .FirstOrDefault(i => i.BaseId == 0x0F52);
        Assert.NotNull(bought);
        Assert.Equal(3, bought!.Amount);
        Assert.True(bought.TryGetTag("CUSTOM_STATE", out string? v));
        Assert.Equal("kept", v);
    }

    // ---- #1: a vendor with no BUY list buys anything (legacy behaviour kept) ----

    [Fact]
    public void GetVendorBuyFilter_NoBuyList_ReturnsNull()
    {
        var world = CreateWorld();
        var vendor = world.CreateCharacter();
        vendor.NpcBrain = NpcBrainType.Vendor;

        // No VENDOR_BUY_LIST tag -> null filter -> the vendor buys anything.
        Assert.Null(VendorEngine.GetVendorBuyFilter(vendor));
    }

    // ---- #7: double-clicking a bank check redeems it to gold ----

    [Fact]
    public void BankCheck_DoubleClick_RedeemsToGoldAndConsumesCheck()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var client = TestHarness.CreateClient(lf, world, new AccountManager(lf), 1850);

        var player = world.CreateCharacter();
        player.IsPlayer = true;
        world.PlaceCharacter(player, new Point3D(100, 100, 0, 0));
        var pack = AddPack(world, player);
        TestHarness.AttachCharacter(client, player);

        var check = world.CreateItem();
        check.BaseId = 0x14F0;
        check.SetTag("BANKCHECK_AMOUNT", "1234");
        pack.AddItem(check);

        client.HandleDoubleClick(check.Uid.Value);

        Assert.True(check.IsDeleted); // check consumed
        var gold = world.GetContainerContents(pack.Uid)
            .FirstOrDefault(i => i.ItemType == ItemType.Gold);
        Assert.NotNull(gold);
        Assert.Equal(1234, gold!.Amount);
    }
}
