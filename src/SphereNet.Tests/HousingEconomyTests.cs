using Microsoft.Extensions.Logging;
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
}
