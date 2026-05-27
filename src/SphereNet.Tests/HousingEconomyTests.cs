using Microsoft.Extensions.Logging;
using System.Reflection;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Housing;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Trade;
using SphereNet.Game.World;

namespace SphereNet.Tests;

public class HousingEconomyTests
{
    private static GameWorld CreateWorld()
    {
        var world = new GameWorld(LoggerFactory.Create(_ => { }));
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        SphereNet.Game.Objects.Items.Item.ResolveWorld = () => world;
        return world;
    }

    [Fact]
    public void HousingEngine_CanPickupHouseItem_OnlyOwnerOrCoOwnerCanLiftLockdown()
    {
        var world = CreateWorld();
        var registry = new MultiRegistry();
        var engine = new HousingEngine(world, registry);
        var owner = world.CreateCharacter();
        var friend = world.CreateCharacter();
        var coOwner = world.CreateCharacter();
        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.SetTag("HOUSE.OWNER", $"0{owner.Uid.Value:X}");
        var locked = world.CreateItem();

        var house = engine.RegisterExistingMulti(multi);
        Assert.NotNull(house);
        house!.AddFriend(friend.Uid);
        house.AddCoOwner(coOwner.Uid);
        Assert.True(house.Lockdown(locked.Uid, owner.Uid));

        Assert.True(engine.CanPickupHouseItem(owner, locked));
        Assert.True(engine.CanPickupHouseItem(coOwner, locked));
        Assert.False(engine.CanPickupHouseItem(friend, locked));
    }

    [Fact]
    public void HouseScriptFacingProperties_ExposeAccessListsAndStorageControls()
    {
        var oldHouseResolver = Item.ResolveHouse;
        var oldOwnerResolver = Character.ResolveHouseUidsByOwner;
        try
        {
            var world = CreateWorld();
            var engine = new HousingEngine(world, new MultiRegistry());
            Item.ResolveHouse = uid => engine.GetHouse(uid);
            Character.ResolveHouseUidsByOwner = ownerUid =>
                engine.GetHousesByOwner(ownerUid).Select(house => house.MultiItem.Uid).ToArray();

            var owner = world.CreateCharacter();
            var coOwner = world.CreateCharacter();
            var friend = world.CreateCharacter();
            var banned = world.CreateCharacter();
            var multi = world.CreateItem();
            multi.ItemType = ItemType.Multi;
            multi.SetTag("HOUSE.OWNER", FormatSerial(owner.Uid));

            var house = engine.RegisterExistingMulti(multi);
            Assert.NotNull(house);
            house!.AddCoOwner(coOwner.Uid);
            house.AddFriend(friend.Uid);
            house.AddBan(banned.Uid);

            var locked = world.CreateItem();
            var secure = world.CreateItem();
            Assert.True(house.Lockdown(locked.Uid, owner.Uid));
            Assert.True(house.SecureContainer(secure.Uid, owner.Uid));

            Assert.True(owner.TryGetProperty("HOUSES", out var houseCount));
            Assert.Equal("1", houseCount);
            Assert.True(owner.TryGetProperty("HOUSE.0", out var houseUid));
            Assert.Equal(FormatSerial(multi.Uid), houseUid);

            Assert.True(multi.TryGetProperty("HOUSE.OWNER", out var ownerUid));
            Assert.Equal(FormatSerial(owner.Uid), ownerUid);
            Assert.True(multi.TryGetProperty("HOUSE.COOWNERS", out var coOwnerCount));
            Assert.Equal("1", coOwnerCount);
            Assert.True(multi.TryGetProperty("HOUSE.COOWNER.0", out var coOwnerUid));
            Assert.Equal(FormatSerial(coOwner.Uid), coOwnerUid);
            Assert.True(multi.TryGetProperty("HOUSE.FRIEND.0", out var friendUid));
            Assert.Equal(FormatSerial(friend.Uid), friendUid);
            Assert.True(multi.TryGetProperty("HOUSE.BAN.0", out var bannedUid));
            Assert.Equal(FormatSerial(banned.Uid), bannedUid);
            Assert.True(multi.TryGetProperty("HOUSE.PRIV." + FormatSerial(coOwner.Uid), out var coOwnerPriv));
            Assert.Equal(((byte)HousePriv.CoOwner).ToString(), coOwnerPriv);
            Assert.True(multi.TryGetProperty("HOUSE.CANACCESS." + FormatSerial(banned.Uid), out var bannedAccess));
            Assert.Equal("0", bannedAccess);
            Assert.True(multi.TryGetProperty("HOUSE.CANLOCKDOWN." + FormatSerial(friend.Uid), out var friendLockdown));
            Assert.Equal("0", friendLockdown);
            Assert.True(multi.TryGetProperty("HOUSE.ISLOCKEDDOWN." + FormatSerial(locked.Uid), out var isLocked));
            Assert.Equal("1", isLocked);
            Assert.True(multi.TryGetProperty("HOUSE.ISSECURED." + FormatSerial(secure.Uid), out var isSecure));
            Assert.Equal("1", isSecure);
        }
        finally
        {
            Item.ResolveHouse = oldHouseResolver;
            Character.ResolveHouseUidsByOwner = oldOwnerResolver;
        }
    }

    [Fact]
    public void HousingEngine_OnTickDecay_UpdatesStagesAndCollapsesExpiredHouses()
    {
        var world = CreateWorld();
        var engine = new HousingEngine(world, new MultiRegistry())
        {
            DecayStageIntervalMs = 1_000
        };

        var owner = world.CreateCharacter();
        var wornMulti = world.CreateItem();
        wornMulti.ItemType = ItemType.Multi;
        wornMulti.SetTag("HOUSE.OWNER", FormatSerial(owner.Uid));
        var wornHouse = engine.RegisterExistingMulti(wornMulti);
        Assert.NotNull(wornHouse);

        wornHouse!.LastRefreshTick = Environment.TickCount64 - 3_500;
        Assert.Empty(engine.OnTickDecay());
        Assert.Equal(HouseDecayStage.FairlyWorn, wornHouse.DecayStage);

        var collapsingMulti = world.CreateItem();
        collapsingMulti.ItemType = ItemType.Multi;
        collapsingMulti.SetTag("HOUSE.OWNER", FormatSerial(owner.Uid));
        var collapsingHouse = engine.RegisterExistingMulti(collapsingMulti);
        Assert.NotNull(collapsingHouse);

        collapsingHouse!.LastRefreshTick = Environment.TickCount64 - 6_500;
        var collapsed = engine.OnTickDecay();

        Assert.Contains(collapsed, house => house.MultiItem.Uid == collapsingMulti.Uid);
        Assert.Null(engine.GetHouse(collapsingMulti.Uid));
    }

    [Fact]
    public void HouseGumpTransfer_ResponseTargetsAndTransfersOwner()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var engine = new HousingEngine(world, new MultiRegistry());
        var client = TestHarness.CreateClient(loggerFactory, world, new SphereNet.Game.Accounts.AccountManager(loggerFactory), 903);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Name = "Owner";
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, owner);

        var newOwner = world.CreateCharacter();
        newOwner.IsPlayer = true;
        newOwner.Name = "NewOwner";
        world.PlaceCharacter(newOwner, new Point3D(101, 100, 0, 0));

        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.SetTag("HOUSE.OWNER", FormatSerial(owner.Uid));
        var house = engine.RegisterExistingMulti(multi);
        Assert.NotNull(house);

        client.SetEngines(housingEngine: engine);
        typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod("OpenHouseSignGump", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [multi]);

        var activeGumps = (HashSet<uint>)typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_activeGumps", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
        uint gumpId = Assert.Single(activeGumps);

        client.HandleGumpResponse(multi.Uid.Value, gumpId, 1, [], []);
        client.HandleTargetResponse(0, 0, newOwner.Uid.Value, newOwner.X, newOwner.Y, newOwner.Z, 0);

        Assert.Equal(newOwner.Uid, house!.Owner);
    }

    [Fact]
    public void HouseGumpDemolish_NonOwnerIsRejectedAndOwnerRedeedsHouse()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var engine = new HousingEngine(world, new MultiRegistry());

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        owner.Name = "Owner";
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));

        var friend = world.CreateCharacter();
        friend.IsPlayer = true;
        friend.Name = "Friend";
        world.PlaceCharacter(friend, new Point3D(101, 100, 0, 0));

        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.BaseId = 0x0064;
        multi.Name = "small stone house";
        multi.SetTag("HOUSE.OWNER", FormatSerial(owner.Uid));
        var house = engine.RegisterExistingMulti(multi);
        Assert.NotNull(house);
        house!.AddFriend(friend.Uid);

        var client = TestHarness.CreateClient(loggerFactory, world, new SphereNet.Game.Accounts.AccountManager(loggerFactory), 904);
        client.SetEngines(housingEngine: engine);
        TestHarness.AttachCharacter(client, friend);
        OpenHouseGump(client, multi);
        client.HandleGumpResponse(multi.Uid.Value, GetSingleActiveGump(client), 2, [], []);

        Assert.NotNull(engine.GetHouse(multi.Uid));
        Assert.False(multi.IsDeleted);

        TestHarness.AttachCharacter(client, owner);
        OpenHouseGump(client, multi);
        client.HandleGumpResponse(multi.Uid.Value, GetSingleActiveGump(client), 2, [], []);

        Assert.Null(engine.GetHouse(multi.Uid));
        Assert.True(multi.IsDeleted);
        var deed = world.FindItem(world.LastNewItem);
        Assert.NotNull(deed);
        Assert.Equal(0x14F0, deed!.BaseId);
        Assert.True(deed.TryGetTag("HOUSE_MULTI_BASEID", out string? baseId));
        Assert.Equal("100", baseId);
    }

    [Fact]
    public void HouseGumpForgeryGuard_RejectsInactiveGumpResponse()
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var world = CreateWorld();
        var engine = new HousingEngine(world, new MultiRegistry());
        var client = TestHarness.CreateClient(loggerFactory, world, new SphereNet.Game.Accounts.AccountManager(loggerFactory), 905);

        var owner = world.CreateCharacter();
        owner.IsPlayer = true;
        world.PlaceCharacter(owner, new Point3D(100, 100, 0, 0));
        TestHarness.AttachCharacter(client, owner);

        var newOwner = world.CreateCharacter();
        newOwner.IsPlayer = true;
        world.PlaceCharacter(newOwner, new Point3D(101, 100, 0, 0));

        var multi = world.CreateItem();
        multi.ItemType = ItemType.Multi;
        multi.SetTag("HOUSE.OWNER", FormatSerial(owner.Uid));
        var house = engine.RegisterExistingMulti(multi);
        Assert.NotNull(house);

        client.SetEngines(housingEngine: engine);
        OpenHouseGump(client, multi);
        uint realGumpId = GetSingleActiveGump(client);

        client.HandleGumpResponse(multi.Uid.Value, realGumpId + 1, 1, [], []);
        client.HandleTargetResponse(0, 0, newOwner.Uid.Value, newOwner.X, newOwner.Y, newOwner.Z, 0);

        Assert.Equal(owner.Uid, house!.Owner);
        Assert.Equal(realGumpId, GetSingleActiveGump(client));
    }

    [Fact]
    public void VendorEngine_ProcessSell_RejectsOverflowingGoldTotals()
    {
        var oldWorld = VendorEngine.World;
        try
        {
            var world = CreateWorld();
            VendorEngine.World = world;
            var player = world.CreateCharacter();
            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            player.Backpack = pack;
            var item = world.CreateItem();
            pack.AddItem(item);

            var vendor = world.CreateCharacter();
            vendor.NpcBrain = NpcBrainType.Vendor;
            var entries = new[]
            {
                new TradeEntry
                {
                    ItemUid = item.Uid,
                    ItemId = item.BaseId,
                    Name = item.Name,
                    Price = int.MaxValue,
                    Amount = 2
                }
            };

            Assert.Equal(0, VendorEngine.ProcessSell(player, vendor, entries));
            Assert.NotNull(world.FindItem(item.Uid));
        }
        finally
        {
            VendorEngine.World = oldWorld;
        }
    }

    [Fact]
    public void VendorEngine_ProcessSell_RejectsItemsOutsidePlayerBackpack()
    {
        var oldWorld = VendorEngine.World;
        try
        {
            var world = CreateWorld();
            VendorEngine.World = world;
            var player = world.CreateCharacter();
            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            player.Backpack = pack;

            var item = world.CreateItem();
            item.Amount = 1;

            var vendor = world.CreateCharacter();
            vendor.NpcBrain = NpcBrainType.Vendor;
            var entries = new[]
            {
                new TradeEntry
                {
                    ItemUid = item.Uid,
                    ItemId = item.BaseId,
                    Name = item.Name,
                    Price = 25,
                    Amount = 1
                }
            };

            Assert.Equal(0, VendorEngine.ProcessSell(player, vendor, entries));
            Assert.False(item.IsDeleted);
            Assert.Equal(0, VendorEngine.CountGold(player));
        }
        finally
        {
            VendorEngine.World = oldWorld;
        }
    }

    [Fact]
    public void VendorEngine_ProcessSell_RejectsOversizedStackWithoutPartialDeletion()
    {
        var oldWorld = VendorEngine.World;
        try
        {
            var world = CreateWorld();
            VendorEngine.World = world;
            var player = world.CreateCharacter();
            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            player.Backpack = pack;

            var item = world.CreateItem();
            item.Amount = 2;
            pack.AddItem(item);

            var vendor = world.CreateCharacter();
            vendor.NpcBrain = NpcBrainType.Vendor;
            var entries = new[]
            {
                new TradeEntry
                {
                    ItemUid = item.Uid,
                    ItemId = item.BaseId,
                    Name = item.Name,
                    Price = 10,
                    Amount = 3
                }
            };

            Assert.Equal(0, VendorEngine.ProcessSell(player, vendor, entries));
            Assert.False(item.IsDeleted);
            Assert.Equal(2, item.Amount);
            Assert.Equal(0, VendorEngine.CountGold(player));
        }
        finally
        {
            VendorEngine.World = oldWorld;
        }
    }

    [Fact]
    public void VendorEngine_ProcessBuy_CountsAndRemovesGoldInsideNestedPouches()
    {
        var oldWorld = VendorEngine.World;
        try
        {
            var world = CreateWorld();
            VendorEngine.World = world;
            var player = world.CreateCharacter();
            var pack = world.CreateItem();
            pack.ItemType = ItemType.Container;
            player.Backpack = pack;

            var pouch = world.CreateItem();
            pouch.ItemType = ItemType.Container;
            pack.AddItem(pouch);

            var gold = world.CreateItem();
            gold.BaseId = 0x0EED;
            gold.ItemType = ItemType.Gold;
            gold.Amount = 75;
            pouch.AddItem(gold);

            var vendor = world.CreateCharacter();
            vendor.NpcBrain = NpcBrainType.Vendor;
            var vendorPack = world.CreateItem();
            vendorPack.ItemType = ItemType.Container;
            vendor.Backpack = vendorPack;
            var stockItem = world.CreateItem();
            stockItem.BaseId = 0x0F7A;
            stockItem.SetTag("PRICE", "50");
            vendorPack.AddItem(stockItem);
            var entries = new[]
            {
                new TradeEntry
                {
                    ItemId = 0x0F7A,
                    Name = "black pearl",
                    Price = 50,
                    Amount = 1
                }
            };

            Assert.Equal(50, VendorEngine.ProcessBuy(player, vendor, entries));
            Assert.Equal(25, VendorEngine.CountGold(player));
            Assert.Contains(pack.Contents, item => item.BaseId == 0x0F7A);
        }
        finally
        {
            VendorEngine.World = oldWorld;
        }
    }

    private static string FormatSerial(Serial uid) =>
        uid.IsValid && uid.Value != 0 ? $"0{uid.Value:X8}" : "0";

    private static void OpenHouseGump(SphereNet.Game.Clients.GameClient client, Item multi)
    {
        typeof(SphereNet.Game.Clients.GameClient)
            .GetMethod("OpenHouseSignGump", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(client, [multi]);
    }

    private static uint GetSingleActiveGump(SphereNet.Game.Clients.GameClient client)
    {
        var activeGumps = (HashSet<uint>)typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_activeGumps", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(client)!;
        return Assert.Single(activeGumps);
    }
}
