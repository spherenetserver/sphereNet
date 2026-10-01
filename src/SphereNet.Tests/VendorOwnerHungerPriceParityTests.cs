using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Game.Clients;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

/// <summary>
/// Player-vendor ownership change, the wild-NPC food clock and the PRICE pet verb
/// with a spoken price, each against Source-X:
/// NPC_PetSetOwner -> NPC_PetClearOwners (CCharNPCPet.cpp:562-584, :601-628),
/// Stats_Regen on every character (CCharAct.cpp:6031) -> OnTickFood (:5748), and
/// PC_PRICE's prefix match and argument (CCharNPCPet.cpp:120, :362-367, :534-538).
/// </summary>
[Collection("DefinitionLoaderSerial")]
public sealed class VendorOwnerHungerPriceParityTests
{
    private sealed record Bench(GameWorld World, GameClient Client, Character OwnerA, Character OwnerB,
        Character Vendor, Item BankA);

    private static Bench OwnedVendor()
    {
        var world = TestHarness.CreateWorld();
        var lf = LoggerFactory.Create(_ => { });
        var state = TestHarness.CreateActiveNetState(lf, 7101);
        var client = new GameClient(state, world, new AccountManager(lf), lf.CreateLogger<GameClient>());

        var a = world.CreateCharacter();
        a.IsPlayer = true;
        a.Name = "alice";
        world.PlaceCharacter(a, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, a);
        var bankA = world.CreateItem();
        bankA.ItemType = ItemType.EqBankBox;
        a.Equip(bankA, Layer.BankBox);

        var b = world.CreateCharacter();
        b.IsPlayer = true;
        b.Name = "carol";
        world.PlaceCharacter(b, new Point3D(102, 100, 0, 0));

        var vendor = world.CreateCharacter();
        vendor.Name = "bob";
        vendor.BodyId = 0x0190;
        vendor.NpcBrain = NpcBrainType.Vendor;
        world.PlaceCharacter(vendor, new Point3D(101, 100, 0, 0));
        Assert.True(vendor.TryAssignOwnership(a, a, summoned: false, enforceFollowerCap: false));
        VendorEngine.World = world;
        return new Bench(world, client, a, b, vendor, bankA);
    }

    private static int GoldIn(GameWorld world, Item container) =>
        world.GetContainerContents(container.Uid)
            .Where(i => !i.IsDeleted && i.ItemType == ItemType.Gold)
            .Sum(i => i.Amount);

    // ---------------------------------------------------------------- 1. transfer

    [Fact]
    public void TransferringAVendorHandsThePreviousOwnerItsGoodsAndPurse()
    {
        var b = OwnedVendor();
        var stocked = b.World.CreateItem();
        stocked.BaseId = 0x0F0E;
        Assert.True(VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!.TryAddItem(stocked));
        var bought = b.World.CreateItem();
        bought.BaseId = 0x0F0F;
        Assert.True(VendorEngine.GetVendorBox(b.Vendor, Layer.VendorExtra)!.TryAddItem(bought));
        VendorEngine.SetVendorGold(b.Vendor, 100);

        Assert.True(b.Vendor.TryAssignOwnership(b.OwnerB, b.OwnerB, summoned: false, enforceFollowerCap: false));

        // NPC_PetClearOwners: purse and every vendor-layer box into the OLD owner's bank.
        Assert.Equal(b.BankA.Uid, stocked.ContainedIn);
        Assert.Equal(b.BankA.Uid, bought.ContainedIn);
        Assert.Equal(100, GoldIn(b.World, b.BankA));
        Assert.Equal(0, VendorEngine.GetVendorGold(b.Vendor));
        // NPC_PetSetOwner :622-628 - the new owner's vendor is invulnerable.
        Assert.True(b.Vendor.HasOwner(b.OwnerB.Uid));
        Assert.True(b.Vendor.IsStatFlag(StatFlag.Invul));
    }

    [Fact]
    public void ReassigningTheSameOwnerKeepsTheVendorsHoldings()
    {
        // NPC_PetSetOwner returns untouched when the owner does not change (:610).
        var b = OwnedVendor();
        var stocked = b.World.CreateItem();
        stocked.BaseId = 0x0F0E;
        var box = VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!;
        Assert.True(box.TryAddItem(stocked));
        VendorEngine.SetVendorGold(b.Vendor, 100);

        Assert.True(b.Vendor.TryAssignOwnership(b.OwnerA, b.OwnerA, summoned: false, enforceFollowerCap: false));

        Assert.Equal(box.Uid, stocked.ContainedIn);
        Assert.Equal(100, VendorEngine.GetVendorGold(b.Vendor));
        Assert.Equal(0, GoldIn(b.World, b.BankA));
    }

    [Fact]
    public void ARefusedTransferKeepsTheVendorsHoldings()
    {
        var b = OwnedVendor();
        var stocked = b.World.CreateItem();
        stocked.BaseId = 0x0F0E;
        var box = VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!;
        Assert.True(box.TryAddItem(stocked));
        VendorEngine.SetVendorGold(b.Vendor, 100);
        GameClient.ServerOptionFlags = OptionFlags.PetSlots;   // reset by ResetEngineStatics
        b.OwnerB.MaxFollower = 0;   // no follower slot left: the cap refuses it

        Assert.False(b.Vendor.TryAssignOwnership(b.OwnerB, b.OwnerB, summoned: false, enforceFollowerCap: true));

        Assert.True(b.Vendor.HasOwner(b.OwnerA.Uid));
        Assert.Equal(box.Uid, stocked.ContainedIn);
        Assert.Equal(100, VendorEngine.GetVendorGold(b.Vendor));
    }

    // ---------------------------------------------------------------- 2. wild food

    private static Character FedNpc(GameWorld world)
    {
        var npc = world.CreateCharacter();
        npc.MaxHits = 50; npc.Hits = 50;
        npc.NpcBrain = NpcBrainType.Animal;
        npc.SetTag("MAXFOOD", "20");
        world.PlaceCharacter(npc, new Point3D(101, 100, 0, 0));
        npc.Food = 10;
        return npc;
    }

    [Fact]
    public void AnNpcNobodyOwnsStillGetsHungry()
    {
        // Stats_Regen runs from every character's tick (CCharAct.cpp:6031); OnTickFood
        // has no owner condition (CCharAct.cpp:5748-5765).
        var world = TestHarness.CreateWorld();
        var npc = FedNpc(world);
        npc.SetNextFoodTick(1);

        npc.OnTick();

        Assert.Equal(9, npc.Food);
    }

    [Fact]
    public void ASpawnedNpcDoesNotGetHungry()
    {
        // STATF_SPAWNED is one of OnTickFood's exemptions (CCharAct.cpp:5753).
        var world = TestHarness.CreateWorld();
        var npc = FedNpc(world);
        npc.SetStatFlag(StatFlag.Spawned);
        npc.SetNextFoodTick(1);

        npc.OnTick();

        Assert.Equal(10, npc.Food);
    }

    [Fact]
    public void AnOwnedPetsFoodIsNotAlsoTakenOnTheCharacterTick()
    {
        // The pet's clock runs from its AI (TickPetOwnershipTimers); the character tick
        // must not take a second point for the same period.
        var world = TestHarness.CreateWorld();
        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        var pet = FedNpc(world);
        pet.TryAssignOwnership(owner, owner);
        pet.Food = 10;
        pet.SetNextFoodTick(1);

        pet.OnTick();
        Assert.Equal(10, pet.Food);
        pet.TickPetOwnershipTimers(1_000_000);
        Assert.Equal(9, pet.Food);
    }

    // ---------------------------------------------------------------- 3. PRICE 100

    private static bool PetCommand(GameClient client, string text) =>
        (bool)typeof(GameClient)
            .GetMethod("TryHandlePetCommand", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [text])!;

    [Fact]
    public void PriceWithANumberPricesTheTargetedStockItemDirectly()
    {
        // "PRICE" is matched as a prefix (CCharNPCPet.cpp:120) and the rest of the line
        // rides along to the target (:362-367); a leading digit sets the price at once
        // (:537-538).
        var b = OwnedVendor();
        var goods = b.World.CreateItem();
        goods.BaseId = 0x0F0E;
        Assert.True(VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!.TryAddItem(goods));

        Assert.True(PetCommand(b.Client, "bob price 100"));
        Assert.True(b.Client.HasPendingTarget);
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, goods.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(100, goods.Price);
    }

    [Fact]
    public void PriceWithoutANumberStillAsksAndDoesNotChangeThePrice()
    {
        var b = OwnedVendor();
        var goods = b.World.CreateItem();
        goods.BaseId = 0x0F0E;
        Assert.True(VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!.TryAddItem(goods));

        Assert.True(PetCommand(b.Client, "bob price"));
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, goods.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(0, goods.Price);
    }

    [Fact]
    public void PriceWithANumberCannotPriceAnItemOutsideTheVendor()
    {
        // NPC_SetVendorPrice: only an item whose top-level object is the vendor (:834-839).
        var b = OwnedVendor();
        var mine = b.World.CreateItem();
        mine.BaseId = 0x0F0E;
        b.World.PlaceItem(mine, new Point3D(100, 101, 0, 0));

        Assert.True(PetCommand(b.Client, "bob price 100"));
        b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, mine.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(0, mine.Price);
    }

    [Fact]
    public void ANonOwnerCannotPriceTheVendorsGoods()
    {
        var b = OwnedVendor();
        var goods = b.World.CreateItem();
        goods.BaseId = 0x0F0E;
        Assert.True(VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!.TryAddItem(goods));
        // Hand the vendor to someone else: the speaker is no longer its owner.
        Assert.True(b.Vendor.TryAssignOwnership(b.OwnerB, b.OwnerB, summoned: false, enforceFollowerCap: false));
        var fresh = b.World.CreateItem();
        fresh.BaseId = 0x0F0E;
        Assert.True(VendorEngine.GetVendorBox(b.Vendor, Layer.VendorStock)!.TryAddItem(fresh));

        PetCommand(b.Client, "bob price 100");
        if (b.Client.HasPendingTarget)
            b.Client.HandleTargetResponse(0, b.Client.ActiveTargetCursorId, fresh.Uid.Value, 0, 0, 0, 0);

        Assert.Equal(0, fresh.Price);
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("100gp", 100)]
    [InlineData("99999999999", int.MaxValue)]
    public void TheSpokenPriceIsReadLikeAtoi(string text, int expected)
    {
        Assert.Equal(expected, ClientItemUseHandler.ParseLeadingPrice(text));
    }
}
