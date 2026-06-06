using System.Reflection;
using Microsoft.Extensions.Logging;
using SphereNet.Core.Enums;
using SphereNet.Core.Types;
using SphereNet.Game.Accounts;
using SphereNet.Network.State;
using SphereNet.Game.Objects.Characters;
using SphereNet.Game.Objects.Items;
using SphereNet.Game.Scripting;
using SphereNet.Game.World;
using Xunit;

namespace SphereNet.Tests;

// Verifies HandleItemPickup selects the correct pickup-source trigger variant.
// SelectPickupTrigger distinguishes four cases that scripts can gate separately:
//   Self  — dragged off the picker's own equipment layers,
//   Stack — a partial amount split out of a larger stack,
//   Pack  — taken from inside a container,
//   Ground— loose on the ground.
// Equipped items report ContainedIn = the wearer, so without the equip-first
// ordering they would be misclassified as Pack pickups; that ordering is asserted.
public class PickupTriggerVariantTests
{
    private static GameWorld CreateWorld()
    {
        var lf = LoggerFactory.Create(_ => { });
        var world = new GameWorld(lf);
        world.InitMap(0, 6144, 4096);
        SphereNet.Game.Objects.ObjBase.ResolveWorld = () => world;
        Item.ResolveWorld = () => world;
        return world;
    }

    private static SphereNet.Game.Clients.GameClient MakeClient(GameWorld world, Character ch,
        TriggerDispatcher dispatcher)
    {
        var lf = LoggerFactory.Create(_ => { });
        var netState = new NetState(lf.CreateLogger<NetState>()) { Id = 7001 };
        typeof(NetState).GetField("<IsInUse>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(netState, true);
        var client = new SphereNet.Game.Clients.GameClient(netState, world,
            new AccountManager(lf), lf.CreateLogger<SphereNet.Game.Clients.GameClient>());
        client.SetEngines(triggerDispatcher: dispatcher);
        typeof(SphereNet.Game.Clients.GameClient)
            .GetField("_character", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, ch);
        return client;
    }

    // Captures which Pickup_* trigger fired by registering all four names globally.
    private static (SphereNet.Game.Clients.GameClient client, Character ch, GameWorld world,
        Func<string?> fired) Setup()
    {
        var world = CreateWorld();
        var ch = world.CreateCharacter();
        ch.IsPlayer = true;
        ch.PrivLevel = PrivLevel.GM; // bypass distance/access gates; the trigger fires first regardless
        world.PlaceCharacter(ch, new Point3D(100, 100, 0, 0));

        var dispatcher = new TriggerDispatcher();
        string? last = null;
        foreach (var name in new[] { "Pickup_Ground", "Pickup_Pack", "Pickup_Self", "Pickup_Stack" })
            dispatcher.RegisterItemEvent("EVENTSITEM", name, (_, _) => { last = name; return TriggerResult.Default; });

        var client = MakeClient(world, ch, dispatcher);
        return (client, ch, world, () => last);
    }

    [Fact]
    public void LooseGroundItem_FiresPickupGround()
    {
        var (client, ch, world, fired) = Setup();
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        world.PlaceItem(item, ch.Position);

        client.HandleItemPickup(item.Uid.Value, 1);

        Assert.Equal("Pickup_Ground", fired());
    }

    [Fact]
    public void ItemInsideContainer_FiresPickupPack()
    {
        var (client, ch, world, fired) = Setup();
        var container = world.CreateItem();
        container.ItemType = ItemType.Container;
        world.PlaceItem(container, ch.Position);
        var item = world.CreateItem();
        item.BaseId = 0x0F7A;
        container.AddItem(item);

        client.HandleItemPickup(item.Uid.Value, 1);

        Assert.Equal("Pickup_Pack", fired());
    }

    [Fact]
    public void EquippedItem_FiresPickupSelf_NotPack()
    {
        var (client, ch, world, fired) = Setup();
        var item = world.CreateItem();
        item.BaseId = 0x1F03;
        ch.Equip(item, Layer.Shirt); // ContainedIn becomes the wearer — must NOT read as Pack

        client.HandleItemPickup(item.Uid.Value, 1);

        Assert.Equal("Pickup_Self", fired());
    }

    [Fact]
    public void PartialStackSplit_FiresPickupStack()
    {
        var (client, ch, world, fired) = Setup();
        var item = world.CreateItem();
        item.BaseId = 0x0EED; // gold-like stackable
        item.Amount = 10;
        world.PlaceItem(item, ch.Position);

        client.HandleItemPickup(item.Uid.Value, 3); // partial → split

        Assert.Equal("Pickup_Stack", fired());
        Assert.Equal(7, item.Amount); // remainder left behind
    }
}
